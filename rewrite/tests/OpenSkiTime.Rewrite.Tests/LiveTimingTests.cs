using System.Text;
using System.Threading.Channels;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using OpenSkiTime.Rewrite.Timing;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class LiveTimingTests
{
    [Fact]
    public void RunningDisplayUsesDeviceClockAndMonotonicElapsedTimeWithoutChangingResults()
    {
        var time = new ManualTime();
        var clock = new RunningTimingClock(time);
        var capture = TimingRulesTests.Session(TimingRulesTests.List(1));
        var decoder = new AlgeAsciiDecoder(capture, "Test", "1");
        var start = Assert.Single(decoder.Feed(TimingRulesTests.Packet(capture, 1, " *0001 C0 12:00:00.9999999\r")));
        Assert.Null(clock.ElapsedHundredths(start));
        clock.Observe(start, time.GetUtcNow());
        time.Advance(TimeSpan.FromMilliseconds(1234));
        Assert.Equal(123, clock.ElapsedHundredths(start));
        time.MoveWallClock(TimeSpan.FromHours(-2));
        Assert.Equal(123, clock.ElapsedHundredths(start)); // changing Windows time must not move the counter
        var heartbeat = Assert.Single(decoder.Feed(TimingRulesTests.Packet(capture, 2, "12:00:10.0000000\r")));
        clock.Observe(heartbeat, time.GetUtcNow());
        Assert.Equal(900, clock.ElapsedHundredths(start));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1000, clock.ElapsedHundredths(start));
        var reset = Assert.Single(decoder.Feed(TimingRulesTests.Packet(capture, 3, "10:00:00.0000000\r")));
        clock.Observe(reset, time.GetUtcNow());
        Assert.Equal(1000, clock.ElapsedHundredths(start));
        Assert.Null(clock.ElapsedHundredths(start with { ClockId = "different-clock" }));
        clock.Clear();
        Assert.Null(clock.ElapsedHundredths(start));
    }

    [Fact]
    public async Task RunTwoCanBePreparedAndCapturedWithoutReconnectingOrSplittingADeviceMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-live-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var first = await TimingStorageTests.SeedAsync(workspace, Path.Combine(root, "synthetic.ost"), 1);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(first.Id);
            await timing.FollowStartOrderAsync(true);
            var source = new FragmentSource();
            await timing.StartAsync(source, new("Test", "Synthetic", TimingRulesTests.Date, Simulation: true), "Test operator");
            await source.Send(" *0001 C0 12:00:00.0000\r");
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
            await Assert.ThrowsAsync<DomainValidationException>(() => timing.SelectRunAsync(Guid.NewGuid()));
            Assert.True(timing.IsActive);
            await source.Send(" *0001 C1 12:01:00.0000\r");
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Complete);
            var recorded = await workspace.ReadTimingAsync(first.Id);
            var plan = FisStartOrder.SecondRun(recorded.List, timing.Snapshot!.ToRunFinishes());
            var series = await workspace.ReadAsync();
            var legacyVersion = $"{recorded.Sessions[0].Id:N}:2:False:audit={recorded.Audit[^1].Id}";
            Assert.True(TimingReplay.InputVersionMatches(legacyVersion, TimingReplay.InputVersion(recorded)));
            Assert.False(TimingReplay.InputVersionMatches(legacyVersion + "0", TimingReplay.InputVersion(recorded)));
            var lists = await workspace.SaveStartListAsync(new(plan, series.Revision, "Test operator", "Run 2", TimingRulesTests.At, legacyVersion));
            var second = lists.Revisions.Single(x => x.Plan.RunNumber == 2);
            // A failed selection must leave the current capture intact, including its write lease.
            await Assert.ThrowsAsync<DomainValidationException>(() => timing.SelectRunAsync(Guid.NewGuid()));
            Assert.Equal(first.Id, timing.ListId);
            await source.Send("12:01:");
            await TimingStorageTests.UntilAsync(() => timing.SavedPackets == 3);
            var change = timing.SelectRunAsync(second.Id);
            await Task.Delay(30);
            Assert.False(change.IsCompleted);
            await source.Send("01.0000\r");
            await change.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(second.Id, timing.ListId);
            Assert.True(timing.IsActive);
            Assert.Equal(1, source.ReceiveCalls);
            Assert.False(source.Disposed);
            var old = await workspace.ReadTimingAsync(first.Id);
            Assert.True(Assert.Single(old.Sessions).CleanStop);
            Assert.Equal(TimingReplay.InputVersion(recorded), TimingReplay.InputVersion(old));
            Assert.EndsWith("12:01:01.0000\r", string.Concat(old.Packets.OrderBy(x => x.Sequence).Select(x => Encoding.ASCII.GetString(x.Bytes))), StringComparison.Ordinal);
            await source.Send(" *0001 C0 13:00:00.0000\r *0001 C1 13:01:02.3499\r");
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Complete);
            Assert.Equal(6234, timing.Snapshot!.Results[0].Hundredths);
            await timing.StopAsync();
            Assert.True(source.Disposed);
            var current = await workspace.ReadTimingAsync(second.Id);
            Assert.Single(current.Packets);
            Assert.True(Assert.Single(current.Sessions).CleanStop);
            await workspace.CloseAsync();
            await workspace.OpenAsync(Path.Combine(root, "synthetic.ost"));
            await workspace.Timing!.SelectRunAsync(second.Id);
            Assert.Equal(6234, workspace.Timing.Snapshot!.Results[0].Hundredths);
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-live-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }

    private sealed class FragmentSource : ITimingSource
    {
        private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
        public int ReceiveCalls { get; private set; }
        public bool Disposed { get; private set; }
        public ValueTask Send(string text) => _input.Writer.WriteAsync(text);
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            ReceiveCalls++; status("Connected test source");
            using var stop = ct.Register(() => _input.Writer.TryComplete());
            await foreach (var text in _input.Reader.ReadAllAsync(CancellationToken.None))
            { await receive(new("alge-ascii/v1", "Test", "1", Encoding.ASCII.GetBytes(text))); }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan value) { _timestamp += value.Ticks; _utc += value; }
        public void MoveWallClock(TimeSpan value) => _utc += value;
    }
}

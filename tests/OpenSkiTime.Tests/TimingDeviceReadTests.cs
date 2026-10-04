using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingDeviceReadTests
{
    private static readonly DateOnly Date = new(2026, 10, 3);
    private static long At(int hour, int minute, int second, long fractionTicks = 0)
        => Date.ToDateTime(new TimeOnly(hour, minute, second)).Ticks + fractionTicks;

    [Fact]
    public async Task ReplayFileIsReadInMemoryAndBecomesSharedEvidenceWithFullPrecisionAndRawText()
    {
        var path = Path.Combine(Path.GetTempPath(), "openskitime-device-read-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "0001 C0 12:00:00.0123\r\nnot a timing line\r\n0002 C1 12:01:00.0456789\r\n0003 C5 12:01:30.0000\r\n", Encoding.ASCII);
        try
        {
            var options = new CaptureOptions(TimingSourceTypes.ReplayFileLabel, "Replay", Date, 0, 1, Simulation: true);
            await using var read = new TimingDeviceRead(new ReplayFileTimingSource(path), AuxiliaryTimingRole.B, options);
            read.Start();
            await read.Completion;
            Assert.Null(read.Fault); Assert.False(read.Truncated); Assert.True(read.PacketCount > 0);
            var observations = read.Decode(new AlgeDecoderFactory());
            Assert.Contains(observations, x => x.Observation.Kind == ObservationKind.Invalid && x.Observation.Message.Contains("not a timing line", StringComparison.Ordinal));

            var provenance = DeviceEvidence.Provenance(options.Device, options.Endpoint);
            var set = DeviceEvidence.ToEvidence(observations, targetUsesUtc: false, provenance);
            Assert.Empty(set.Warnings); Assert.Equal(0, set.ShiftedCount);
            Assert.Equal(3, set.Evidence.Count);
            Assert.All(set.Evidence, x => Assert.StartsWith("device:Replay file:Replay:", x.Key, StringComparison.Ordinal));
            var start = Assert.Single(set.Evidence, x => x.Channel == 0);
            Assert.Equal(At(12, 0, 0, 123_000), start.Ticks); Assert.Equal(4, start.Precision);
            Assert.Equal("0001 C0 12:00:00.0123", start.Text);
            var finish = Assert.Single(set.Evidence, x => x.Channel == 1);
            Assert.Equal(At(12, 1, 0, 456_789), finish.Ticks); Assert.Equal(7, finish.Precision);
            Assert.Single(set.Evidence, x => x.Channel is not (0 or 1)); // Unmapped channel is kept but cannot match.

            var targets = new EvidenceTarget[] { new("1:10:0", 10, 0, At(12, 0, 0)), new("1:10:1", 10, 1, At(12, 1, 0)) };
            var matches = TimingEvidenceMatching.Match(targets, set.Evidence, TimeSpan.TicksPerSecond, allowAdjacentDay: true);
            Assert.All(matches, x => Assert.Equal("Proposed - verify", x.State));
            Assert.Equal(At(12, 1, 0, 456_789), matches[1].Evidence!.Ticks);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HandRoleUsesOneChannelAndTheRoleFixesThePosition()
    {
        var source = new ScriptedSource("0001 C3 12:01:00.12\r");
        var options = new CaptureOptions(TimingSourceTypes.Mt1SerialLabel, "COM7", Date, 3, 3);
        await using var read = new TimingDeviceRead(source, AuxiliaryTimingRole.HandFinish, options);
        read.Start(); await read.Completion;
        var evidence = Assert.Single(DeviceEvidence.ToEvidence(read.Decode(new AlgeDecoderFactory()), false, "device:test").Evidence);
        Assert.Equal(1, evidence.Channel); Assert.Equal(2, evidence.Precision);
    }

    [Fact]
    public async Task ReadIsBoundedAndStopsTheSource()
    {
        var source = new EndlessSource();
        var options = new CaptureOptions(TimingSourceTypes.Mt1SerialLabel, "COM7", Date);
        await using var read = new TimingDeviceRead(source, AuxiliaryTimingRole.B, options, maxPackets: 3);
        read.Start();
        await read.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(read.Truncated); Assert.Equal(3, read.PacketCount);
        Assert.Contains("limit", read.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopCancelsAWaitingSourceAndFailuresAreVisible()
    {
        var waiting = new EndlessSource(waitForever: true);
        var options = new CaptureOptions(TimingSourceTypes.Mt1SerialLabel, "COM7", Date);
        var read = new TimingDeviceRead(waiting, AuxiliaryTimingRole.B, options);
        read.Start();
        await read.StopAsync();
        Assert.True(read.Completion.IsCompleted); Assert.Null(read.Fault);
        await read.DisposeAsync();
        Assert.True(waiting.Disposed);

        await using var failing = new TimingDeviceRead(new FailingSource(), AuxiliaryTimingRole.B, options);
        failing.Start(); await failing.Completion;
        Assert.Equal("Port is unavailable.", failing.Fault);
        Assert.Contains(failing.Decode(new AlgeDecoderFactory()), x => x.Observation.Kind == ObservationKind.Invalid);
    }

    [Fact]
    public void UtcDeviceEvidenceUsesTheExplicitOffsetOnlyForComparisonValues()
    {
        var utc = new TimingObservation("k1", Guid.Empty, 1, "123", "f", ObservationKind.Impulse, 0, At(9, 0, 0, 12_345), 5,
            null, false, "UTC", "123 C0 · 09:00:00.0012345 UTC");
        var withOffset = new AuxiliaryTimingObservation(AuxiliaryTimingRole.B, false, utc) { ComparisonUtcOffsetMinutes = 180 };
        var set = DeviceEvidence.ToEvidence([withOffset], targetUsesUtc: false, "device:MT1 · ALGE Results:123/0;123/1");
        var evidence = Assert.Single(set.Evidence);
        Assert.Equal(At(12, 0, 0, 12_345), evidence.Ticks); Assert.Equal(5, evidence.Precision);
        Assert.Contains("+180 min", evidence.Text, StringComparison.Ordinal);
        Assert.Equal(1, set.ShiftedCount);
        Assert.Equal(At(9, 0, 0, 12_345), utc.DeviceTicks); // The original decoded value is unchanged.

        var missing = DeviceEvidence.ToEvidence([withOffset with { ComparisonUtcOffsetMinutes = null }], false, "device:x");
        Assert.Empty(missing.Evidence);
        Assert.Contains("UTC", Assert.Single(missing.Warnings), StringComparison.Ordinal);
        Assert.Empty(DeviceEvidence.ToEvidence([withOffset with { ComparisonUtcOffsetMinutes = null }], true, "device:x").Warnings);
    }

    private sealed class ScriptedSource(string text) : ITimingSource
    {
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        { status("scripted"); await receive(new("alge-ascii/v1", "Scripted", "1", Encoding.ASCII.GetBytes(text))); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EndlessSource(bool waitForever = false) : ITimingSource
    {
        public bool Disposed { get; private set; }
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            if (waitForever) { await Task.Delay(Timeout.Infinite, ct); }
            var i = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await receive(new("alge-ascii/v1", "Endless", "1", Encoding.ASCII.GetBytes($"{++i:0000} C0 12:00:00.0000\r")));
            }
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FailingSource : ITimingSource
    {
        public Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
            => throw new IOException("Port is unavailable.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

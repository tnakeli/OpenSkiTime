using System.Text;
using System.Threading.Channels;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests.FullRace;

// Controlled race exceptions kept outside the 100-athlete scenario so that scenario stays deterministic.
public sealed class RaceExceptionTests
{
    [Fact]
    public async Task RetransmittedDevicePacketIsKeptAsDuplicateWithoutChangingResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-race-exceptions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 2);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var source = new LineSource();
            await timing.StartAsync(source, new("Synthetic ALGE", "Test", TimingRulesTests.Date, Simulation: true), "Test operator");
            async Task Send(string line, int observations)
            {
                await source.SendAsync(line);
                await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count == observations);
                await timing.FollowStartOrderAsync(true);
            }
            await Send(" 0001 C0 12:00:00.0000 00\r", 1);
            await Send(" 0001 C0 12:00:00.0000 00\r", 2); // the device repeats the identical start line
            await Send(" 0002 C1 12:00:51.2345 00\r", 3);
            var snapshot = timing.Snapshot!;
            Assert.Equal("Duplicate", snapshot.Observations[1].State);
            Assert.Equal(snapshot.Observations[0].Observation.Key, snapshot.Observations[1].DuplicateOf);
            var first = snapshot.Results.Single(x => x.Bib == list.Plan.Entries[0].Bib);
            Assert.Equal(TimingStatus.Finished, first.Status);
            Assert.Equal(5123, first.Hundredths);
            Assert.Equal(TimingStatus.Ready, snapshot.Results.Single(x => x.Bib == list.Plan.Entries[1].Bib).Status);
            Assert.Equal(0, snapshot.Unresolved);
            await timing.StopAsync();
            Assert.Equal(3, (await workspace.ReadTimingAsync(list.Id)).Packets.Count); // raw input is preserved unchanged
            await workspace.CloseAsync();
            await workspace.OpenAsync(file);
            await workspace.Timing!.SelectRunAsync(list.Id);
            Assert.Equal("Duplicate", workspace.Timing.Snapshot!.Observations[1].State);
            Assert.Equal(5123, workspace.Timing.Snapshot.Results.Single(x => x.Bib == list.Plan.Entries[0].Bib).Hundredths);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingFinishWithoutHandTimeKeepsTheRunIncompleteAndBlocksRunTwo()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-race-exceptions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 2);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Test operator");
            var noon = TimeSpan.FromHours(12).Ticks;
            await source.PulseAsync(0, noon);
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count == 1);
            await timing.CorrectStatusesAsync([list.Plan.Entries[1].Bib], TimingStatus.DNS, "Test operator", "Did not start");
            Assert.False(timing.Snapshot!.Complete);
            Assert.Throws<OpenSkiTime.Domain.DomainValidationException>(() => timing.Snapshot!.ToRunFinishes());
            // A manual time with an invalid format or without a reason is rejected without writing an audit row.
            var audit = timing.Snapshot.Audit.Count;
            await Assert.ThrowsAsync<OpenSkiTime.Domain.DomainValidationException>(() =>
                timing.AddManualTimestampAsync(list.Id, 1, "12:00:5", "Test operator", "Hand time"));
            await Assert.ThrowsAsync<OpenSkiTime.Domain.DomainValidationException>(() =>
                timing.AddManualTimestampAsync(list.Id, 1, "12:00:52.10", "Test operator", " "));
            Assert.Equal(audit, timing.Snapshot!.Audit.Count);
            var key = await timing.AddManualTimestampAsync(list.Id, 1, "12:00:52.10", "Test operator", "Hand time from finish judge");
            await timing.CorrectAsync(new(DecisionKind.Assignment, key, Bib: list.Plan.Entries[0].Bib), "Test operator", "Assign hand time");
            Assert.True(timing.Snapshot!.Complete);
            var finished = timing.Snapshot.Results.Single(x => x.Bib == list.Plan.Entries[0].Bib);
            Assert.Equal(5210, finished.Hundredths);
            Assert.True(finished.FinishManual);
            var manual = timing.Snapshot.Audit.Single(x => x.After.Kind == DecisionKind.ManualTime);
            Assert.Equal("Test operator", manual.Operator);
            Assert.Equal("Hand time from finish judge", manual.Reason);
            await timing.StopAsync();
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class LineSource : ITimingSource
    {
        private readonly Channel<byte[]> _lines = Channel.CreateUnbounded<byte[]>();
        public ValueTask SendAsync(string line) => _lines.Writer.WriteAsync(Encoding.ASCII.GetBytes(line));
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            status("SIMULATION · scripted ALGE lines");
            using var stop = ct.Register(() => _lines.Writer.TryComplete());
            await foreach (var bytes in _lines.Reader.ReadAllAsync(CancellationToken.None)) { await receive(new("alge-ascii/v1", "Synthetic", "1", bytes)); }
        }
        public ValueTask DisposeAsync() { _lines.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}

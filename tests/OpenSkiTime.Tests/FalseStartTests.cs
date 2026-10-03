using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class FalseStartTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReturnToStartPreservesEvidenceUndoHoldsAndOtherRacersThenTimesTheNewStart(bool followOrder)
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 3, 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.FollowStartOrderAsync(followOrder);
        var source = new SimulatorTimingSource();
        var options = new CaptureOptions("Simulator", "Test", TimingRulesTests.Date, Simulation: true) { IntermediateChannels = [2] };
        await timing.StartAsync(source, options, "Test operator");
        var other = list.Plan.Entries[0].Bib;
        var bib = list.Plan.Entries[2].Bib; // deliberate out-of-order start
        await timing.ExpectAsync(0, other);
        await PulseAsync(timing, source, 0, 0, followOrder);
        var otherStart = timing.Snapshot!.Results.Single(x => x.Bib == other).StartKey;
        await timing.ExpectAsync(0, bib);
        await PulseAsync(timing, source, 0, 5, followOrder);
        var falseStart = timing.Snapshot!.Results.Single(x => x.Bib == bib).StartKey!;
        var original = await workspace.ReadTimingAsync(list.Id);
        await timing.ExpectAsync(1, bib);
        await timing.ExpectAsync(2, bib);
        await timing.ReturnToStartAsync(bib, falseStart, "Test operator");
        Assert.Equal(bib, timing.ArmedStart);
        Assert.NotEqual(bib, timing.ArmedFinish);
        Assert.NotEqual(bib, timing.ExpectedBib(2));
        var returned = timing.Snapshot!.Results.Single(x => x.Bib == bib);
        Assert.Equal(TimingStatus.Ready, returned.Status);
        Assert.Null(returned.StartKey);
        Assert.Equal(other, Assert.Single(RaceFlow.OnCourse(timing.Snapshot)).Bib);
        Assert.Equal(otherStart, timing.Snapshot.Results.Single(x => x.Bib == other).StartKey);
        var correction = timing.Snapshot.Audit[^1];
        Assert.Equal(bib, correction.Before.Bib);
        Assert.Equal(falseStart, correction.After.ObservationKey);
        Assert.True(correction.After.Ignored);
        Assert.Equal("Test operator", correction.Operator);
        Assert.Contains("returned to start", correction.Reason, StringComparison.Ordinal);
        Assert.NotEqual(default, correction.At);
        Assert.Equal("Ignored", timing.Snapshot.Observations.Single(x => x.Observation.Key == falseStart).State);

        await timing.UndoAsync(correction.Id, "Test operator", "Restore the original start");
        Assert.Equal(TimingStatus.OnCourse, timing.Snapshot!.Results.Single(x => x.Bib == bib).Status);
        Assert.NotEqual(bib, timing.ArmedStart);
        Assert.Equal(correction.Id, timing.Snapshot.Audit[^1].ReversesId);
        await timing.ExpectAsync(0, null, held: true);
        await timing.ReturnToStartAsync(bib, falseStart, "Test operator");
        Assert.True(timing.IsHeld(0)); // returning a racer must not silently release a safety hold
        Assert.Null(timing.ArmedStart);
        await timing.StopAsync();
        await workspace.CloseAsync();
        await workspace.OpenAsync(folder.File);
        timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.FollowStartOrderAsync(followOrder);
        Assert.Equal(TimingStatus.Ready, timing.Snapshot!.Results.Single(x => x.Bib == bib).Status);
        Assert.Equal("Ignored", timing.Snapshot.Observations.Single(x => x.Observation.Key == falseStart).State);
        var reopened = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(original.Packets.Select(x => Convert.ToHexString(x.Bytes)), reopened.Packets.Select(x => Convert.ToHexString(x.Bytes)));
        Assert.Equal(original.Audit.Count + 3, reopened.Audit.Count);

        source = new SimulatorTimingSource();
        await timing.StartAsync(source, options, "Test operator");
        await timing.ExpectAsync(0, bib);
        await PulseAsync(timing, source, 0, 20, followOrder);
        var version = timing.Snapshot!.AuditVersion;
        // A delayed/repeated click referring to the old start must not cancel the real new start.
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.ReturnToStartAsync(bib, falseStart, "Test operator"));
        Assert.Equal(version, timing.Snapshot!.AuditVersion);
        await timing.ExpectAsync(1, bib);
        await PulseAsync(timing, source, 1, 30, followOrder);
        Assert.Equal(1000, timing.Snapshot!.Results.Single(x => x.Bib == bib).Hundredths);
        Assert.Equal(TimingStatus.OnCourse, timing.Snapshot.Results.Single(x => x.Bib == other).Status);
        await timing.StopAsync();
        await workspace.CloseAsync();
        await workspace.OpenAsync(folder.File);
        await workspace.Timing!.SelectRunAsync(list.Id);
        Assert.Equal(1000, workspace.Timing.Snapshot!.Results.Single(x => x.Bib == bib).Hundredths);
    }

    [Theory]
    [InlineData("intermediate")]
    [InlineData("finish")]
    [InlineData("duplicate start")]
    [InlineData("classification")]
    [InlineData("corrected time")]
    public async Task ReturnToStartRejectsExistingProgressWithoutChangingSavedData(string progress)
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1, 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var entry = list.Plan.Entries[0];
        var source = new SimulatorTimingSource();
        await timing.FollowStartOrderAsync(true);
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true) { IntermediateChannels = [2] }, "Test operator");
        await PulseAsync(timing, source, 0, 0, true);
        var start = timing.Snapshot!.Results[0].StartKey!;
        if (progress == "classification")
        { await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: entry.Entrant.CompetitorId, Status: TimingStatus.DNF), "Test operator", "Left the course"); }
        else if (progress == "corrected time")
        { await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: 2000), "Test operator", "Verified backup"); }
        else
        { await PulseAsync(timing, source, progress == "intermediate" ? 2 : progress == "finish" ? 1 : 0, 20, true, entry.Bib); }
        var before = await workspace.ReadTimingAsync(list.Id);
        Assert.False(RaceFlow.CanReturnToStart(timing.Snapshot!.Results[0]));
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.ReturnToStartAsync(entry.Bib, start, "Test operator"));
        var after = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(before.Audit, after.Audit);
        Assert.Equal(before.Packets.Select(x => Convert.ToHexString(x.Bytes)), after.Packets.Select(x => Convert.ToHexString(x.Bytes)));
        await timing.StopAsync();
    }

    private static async Task PulseAsync(TimingWorkspace timing, SimulatorTimingSource source, int channel, int seconds, bool followOrder, int? bib = null)
    {
        var count = timing.Snapshot!.Observations.Count;
        await source.PulseAsync(channel, TimeSpan.FromHours(12).Ticks + seconds * TimeSpan.TicksPerSecond, bib);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count > count);
        await timing.FollowStartOrderAsync(followOrder); // wait for attribution under the capture state gate
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-false-start", Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(_root, "synthetic.ost");
        public TestFolder() => Directory.CreateDirectory(_root);
        public void Dispose()
        {
            if (!Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-false-start") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Unexpected test folder."); }
            Directory.Delete(_root, true);
        }
    }
}

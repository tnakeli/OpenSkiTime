using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class RaceFlowTests
{
    [Fact]
    public async Task GroupClassificationIsAtomicAndPersistsAcrossReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-group-status", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 3);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var bibs = list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).ToArray();
            await Assert.ThrowsAsync<OpenSkiTime.Domain.DomainValidationException>(() =>
                timing.CorrectStatusesAsync([bibs[0], -1], TimingStatus.DNS, "Test operator", "Group DNS"));
            Assert.Empty(timing.Snapshot!.Audit);
            await timing.CorrectStatusesAsync(bibs[..2], TimingStatus.DNS, "Test operator", "Group DNS");
            Assert.Equal(new[] { TimingStatus.DNS, TimingStatus.DNS, TimingStatus.Ready },
                bibs.Select(bib => timing.Snapshot!.Results.Single(x => x.Bib == bib).Status));
            Assert.Equal(bibs[2], timing.ArmedStart);
            Assert.Equal(2, timing.Snapshot!.Audit.Count);
            Assert.All(timing.Snapshot.Audit, x => Assert.Equal("Group DNS", x.Reason));
            await workspace.CloseAsync();
            await workspace.OpenAsync(file);
            await workspace.Timing!.SelectRunAsync(list.Id);
            Assert.Equal(2, workspace.Timing.Snapshot!.Results.Count(x => x.Status == TimingStatus.DNS));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StartQueueCanBeReorderedAndReopenedWithoutChangingTheDraw()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-start-order", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 3);
            var original = list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).ToArray();
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            Assert.Equal(original[0], timing.ArmedStart);
            await timing.MoveWaitingAsync(original[0], 1, "Test operator");
            Assert.Equal(original[1], timing.ArmedStart);
            Assert.Equal(new[] { original[1], original[0], original[2] }, RaceFlow.Waiting(timing.Snapshot!).Select(x => x.Bib));
            await timing.MoveWaitingRelativeAsync(original[2], original[1], visuallyAbove: false, "Test operator");
            Assert.Equal(original[2], timing.ArmedStart);
            Assert.Equal(new[] { original[2], original[1], original[0] }, RaceFlow.Waiting(timing.Snapshot!).Select(x => x.Bib));
            Assert.Equal(original, list.Plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib));
            await workspace.CloseAsync();
            await workspace.OpenAsync(file);
            await workspace.Timing!.SelectRunAsync(list.Id);
            await workspace.Timing.FollowStartOrderAsync(true);
            Assert.Equal(original[2], workspace.Timing.ArmedStart);
            Assert.Equal(new[] { original[2], original[1], original[0] }, RaceFlow.Waiting(workspace.Timing.Snapshot!).Select(x => x.Bib));
            var changes = workspace.Timing.Snapshot!.Audit.ToArray();
            Assert.Equal(2, changes.Length);
            await workspace.Timing.UndoAsync(changes[^1].Id, "Test operator", "Restore order");
            Assert.Equal(new[] { original[1], original[0], original[2] }, RaceFlow.Waiting(workspace.Timing.Snapshot!).Select(x => x.Bib));
            await workspace.Timing.UndoAsync(changes[0].Id, "Test operator", "Restore draw order");
            Assert.Equal(original, RaceFlow.Waiting(workspace.Timing.Snapshot!).Select(x => x.Bib));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ConfiguredButUnusedIntermediateIsIgnoredWithoutBlockingTheResult()
    {
        var list = TimingRulesTests.List(1); // Competition has no intermediate positions.
        var capture = TimingRulesTests.Session(list);
        capture = capture with { Options = capture.Options with { IntermediateChannels = [2] } };
        var decoder = new AlgeAsciiDecoder(capture, "Synthetic", "1");
        var observations = decoder.Feed(TimingRulesTests.Packet(capture, 1,
            " *0001 C0 12:00:00.0000 00\r *0001 C2 12:00:10.0000 00\r *0001 C1 12:01:00.0000 00\r"));
        var audit = new[] { observations[0], observations[2] }.Select((observation, index) =>
            new TimingAudit(index + 1, list.Id, TimingRulesTests.At, "Test operator", "Identified bib",
                new(DecisionKind.Assignment, observation.Key), new(DecisionKind.Assignment, observation.Key, Bib: 1))).ToArray();
        var result = TimingEngine.Replay(list, observations, audit, 0, 1);
        Assert.Equal(TimingStatus.Finished, result.Results[0].Status);
        Assert.Equal(6000, result.Results[0].Hundredths);
        Assert.True(result.Complete);
        Assert.Equal("Ignored", result.Observations[1].State);
        Assert.Equal(0, result.Unresolved);
    }

    [Fact]
    public async Task UnusedConfiguredIntermediateStillPreservesTheOriginalPacket()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-unused-intermediate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 1);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true)
                { IntermediateChannels = [2] }, "Test operator");
            var noon = TimeSpan.FromHours(12).Ticks;
            await source.PulseAsync(0, noon);
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
            await source.PulseAsync(2, noon + TimeSpan.FromSeconds(10).Ticks);
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count == 2);
            Assert.Equal("Ignored", timing.Snapshot!.Observations[1].State);
            await source.PulseAsync(1, noon + TimeSpan.FromMinutes(1).Ticks);
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Complete);
            Assert.Equal(6000, timing.Snapshot!.Results[0].Hundredths);
            await timing.StopAsync();
            Assert.Equal(3, (await workspace.ReadTimingAsync(list.Id)).Packets.Count);
            await workspace.CloseAsync();
            await workspace.OpenAsync(file);
            await workspace.Timing!.SelectRunAsync(list.Id);
            Assert.Equal("Ignored", workspace.Timing.Snapshot!.Observations[1].State);
            Assert.Equal(6000, workspace.Timing.Snapshot.Results[0].Hundredths);
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-unused-intermediate") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void IntermediateUsesFullPrecisionAndRequiresReviewAcrossClockContexts()
    {
        var original = TimingRulesTests.List(1);
        var list = original with { Plan = original.Plan with { Competition = original.Plan.Competition with { IntermediateCount = 1 } } };
        var capture = TimingRulesTests.Session(list);
        capture = capture with { Options = capture.Options with { IntermediateChannels = [5] } };
        var decoder = new AlgeAsciiDecoder(capture, "Synthetic", "1");
        var observations = decoder.Feed(TimingRulesTests.Packet(capture, 1,
            " *0001 C0 12:00:00.9999999 00\r *0001 C5 12:00:11.0000000 00\r *0001 C1 12:00:20.9999999 00\r")).ToArray();
        Assert.Equal(2, observations[1].Channel);
        var audit = observations.Select((x, i) => new TimingAudit(i + 1, list.Id, TimingRulesTests.At,
            "Test operator", "Identified bib", new(DecisionKind.Assignment, x.Key), new(DecisionKind.Assignment, x.Key, Bib: 1))).ToArray();
        var result = TimingEngine.Replay(list, observations, audit, 0, 1);
        Assert.Equal(1000, result.Results[0].Splits[0].Hundredths);
        Assert.Equal(2000, result.Results[0].Hundredths);
        Assert.True(result.Complete);
        observations[1] = observations[1] with { ClockId = "different-clock" };
        result = TimingEngine.Replay(list, observations, audit, 0, 1);
        Assert.Null(result.Results[0].Splits[0].Hundredths);
        Assert.Equal("Review", result.Observations[1].State);
        Assert.True(result.Complete); // an invalid optional split must not hold a valid finish/result for approval
        Assert.Equal(2000, result.Results[0].Hundredths);
        Assert.Equal(1, result.Unresolved);
        TimingEngine.ValidateDecision(new(DecisionKind.Assignment, observations[1].Key, Bib: 1), result);
    }

    [Fact]
    public async Task RaceQueuesHandleAbsentReorderedDnfSplitsAndFalseFinishThenReplaySavedEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-race-flow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic.ost");
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 6, 2);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var entries = list.Plan.Entries;
            var a = entries[0]; var b = entries[1]; var c = entries[2]; var d = entries[3];
            var source = new SimulatorTimingSource();
            await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true)
                { IntermediateChannels = [2, 3] }, "Test operator");
            var at = TimeSpan.FromHours(12).Ticks;
            async Task Pulse(int channel, int seconds)
            {
                var count = timing.Snapshot!.Observations.Count;
                await source.PulseAsync(channel, at + seconds * TimeSpan.TicksPerSecond);
                await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count > count);
                // Synchronize through the same state gate as auto-assignment, not a UI refresh delay.
                await timing.FollowStartOrderAsync(true);
            }
            Assert.Equal(a.Bib, timing.ArmedStart);
            await Pulse(0, 0);
            Assert.Equal(b.Bib, timing.ArmedStart);
            Assert.Equal(a.Bib, timing.ArmedFinish);
            await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: b.Entrant.CompetitorId, Status: TimingStatus.DNS), "Test operator", "Absent at start");
            Assert.Equal(c.Bib, timing.ArmedStart);
            await timing.ExpectAsync(0, d.Bib); // permitted out-of-order start without redrawing the race
            await Pulse(0, 5);
            Assert.Equal(c.Bib, timing.ArmedStart);
            Assert.Equal(new[] { a.Bib, d.Bib }, RaceFlow.OnCourse(timing.Snapshot!).Select(x => x.Bib));
            Assert.Equal(a.Bib, timing.ExpectedBib(2));
            await Pulse(2, 10);
            Assert.Equal(d.Bib, timing.ExpectedBib(2));
            await Pulse(3, 22);
            Assert.Equal(new long?[] { 1000, 2200 }, timing.Snapshot!.Results.Single(x => x.Bib == a.Bib).Splits.Select(x => x.Hundredths));
            await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: d.Entrant.CompetitorId, Status: TimingStatus.DNF), "Test operator", "Left course");
            Assert.Single(RaceFlow.OnCourse(timing.Snapshot!));
            Assert.Null(timing.ExpectedBib(2));
            await timing.ExpectAsync(1, null, held: true);
            await Pulse(1, 25);
            Assert.Equal("Unassigned", timing.Snapshot!.Observations[^1].State);
            await timing.ExpectAsync(1, null);
            await Pulse(1, 30); // course worker trips the beam; undo attribution without deleting the pulse
            var falseFinish = timing.Snapshot!.Results.Single(x => x.Bib == a.Bib).FinishKey;
            Assert.NotNull(falseFinish);
            await timing.CorrectAsync(new(DecisionKind.Assignment, falseFinish, Ignored: true), "Test operator", "No racer at finish");
            Assert.Equal(a.Bib, timing.ArmedFinish);
            Assert.Equal(TimingStatus.OnCourse, timing.Snapshot!.Results.Single(x => x.Bib == a.Bib).Status);
            await Pulse(1, 40);
            Assert.Equal(4000, timing.Snapshot!.Results.Single(x => x.Bib == a.Bib).Hundredths);
            await timing.StopAsync();
            var before = await workspace.ReadTimingAsync(list.Id);
            await workspace.CloseAsync(); await workspace.OpenAsync(file);
            await workspace.Timing!.SelectRunAsync(list.Id);
            var replay = workspace.Timing.Snapshot!;
            Assert.Equal(4000, replay.Results.Single(x => x.Bib == a.Bib).Hundredths);
            Assert.Equal(new long?[] { 1000, 2200 }, replay.Results.Single(x => x.Bib == a.Bib).Splits.Select(x => x.Hundredths));
            Assert.Equal("Ignored", replay.Observations.Single(x => x.Observation.Key == falseFinish).State);
            var after = await workspace.ReadTimingAsync(list.Id);
            Assert.Equal(before.Packets.Select(x => Convert.ToHexString(x.Bytes)), after.Packets.Select(x => Convert.ToHexString(x.Bytes)));
            Assert.All(after.Audit, x => Assert.False(string.IsNullOrWhiteSpace(x.Reason)));
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-race-flow") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public void IntermediateMappingsRejectAmbiguousChannels()
    {
        var options = new CaptureOptions("Simulator", "Test", TimingRulesTests.Date);
        Assert.Throws<Domain.DomainValidationException>(() => (options with { IntermediateChannels = [2, 2] }).Validate());
        Assert.Throws<Domain.DomainValidationException>(() => (options with { IntermediateChannels = [1] }).Validate());
        Assert.Throws<Domain.DomainValidationException>(() => (options with { IntermediateChannels = [9] }).Validate());
    }
}

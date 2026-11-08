using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// The Edit result dialog's start/finish entry: device-record (System A), hand and EET times for one competitor.
public sealed class CompetitorTimeEntryTests
{
    private static readonly long Noon = TimeSpan.FromHours(12).Ticks;

    [Fact]
    public async Task DeviceRecordFinishIsAuditedSystemATimeAndSurvivesReopen()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        await Pulse(timing, source, 0, Noon);
        var bib = timing.Snapshot!.Results.Single().Bib;
        var start = timing.Snapshot.Results.Single().StartKey!;

        // A device record has System A precision: hundredths are rejected and nothing is written.
        var before = timing.Snapshot.AuditVersion;
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(bib, 1, ManualTimeSource.DeviceRecord, "12:00:41.23"), "Operator", "USB cable disconnected"));
        Assert.Equal(before, timing.Snapshot.AuditVersion);

        var key = await timing.EnterCompetitorTimeAsync(list.Id,
            new(bib, 1, ManualTimeSource.DeviceRecord, " 12:00:41.2345 ", " Printout line 0187 "), "Operator", "USB cable disconnected");
        await timing.StopAsync();
        AssertResult(timing.Snapshot!);
        var saved = await workspace.ReadTimingAsync(list.Id);
        var rows = saved.Audit.Where(x => x.Id > before).ToArray();
        Assert.Equal([DecisionKind.ManualTime, DecisionKind.Assignment], rows.Select(x => x.After.Kind));
        Assert.All(rows, x => Assert.Equal(("Operator", "USB cable disconnected"), (x.Operator, x.Reason)));
        var entry = rows[0].After.Timestamp!;
        Assert.Equal((ManualTimeSource.DeviceRecord, "Printout line 0187", start), (entry.Source, entry.SourceReference, entry.ReferenceKey));

        // A System A time counts as System A in the timing report.
        var report = TimingReportProjection.FromTiming(saved, timing.Snapshot!);
        Assert.True(report.AllResultsA);

        await workspace.CloseAsync(); await workspace.OpenAsync(folder.File);
        await workspace.Timing!.SelectRunAsync(list.Id);
        AssertResult(workspace.Timing.Snapshot!);
        AssertResult(TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), new AlgeDecoderFactory()));

        void AssertResult(TimingSnapshot snapshot)
        {
            var result = snapshot.Results.Single();
            Assert.Equal((TimingStatus.Finished, 4123L, key), (result.Status, result.Hundredths!.Value, result.FinishKey));
            Assert.False(result.FinishManual);
            Assert.False(result.FinishEet);
            Assert.Equal("Includes a System A time entered from the device record · see history.", result.Detail);
            var observation = snapshot.Observations.Single(x => x.Observation.Key == key).Observation;
            Assert.False(observation.Manual);
            Assert.True(observation.ManualEntry);
            Assert.Equal(ManualTimeSource.DeviceRecord, observation.EntrySource);
        }
    }

    [Fact]
    public async Task HandFinishReplacesTheAssignedImpulseInOneBatchAndRejectsAnImpossibleTime()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        await Pulse(timing, source, 0, Noon);
        await Pulse(timing, source, 1, Noon + 50 * TimeSpan.TicksPerSecond);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results.Single().Status == TimingStatus.Finished);
        var old = timing.Snapshot!.Results.Single().FinishKey!;
        var bib = timing.Snapshot.Results.Single().Bib;

        // A finish before the start would leave the result in review: rejected before anything is written.
        var version = timing.Snapshot.AuditVersion;
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(bib, 1, ManualTimeSource.Hand, "11:59:00.00", Replacing: [old]), "Operator", "Photocell missed"));
        Assert.Contains("Nothing was saved", error.Message, StringComparison.Ordinal);
        Assert.Equal(version, timing.Snapshot.AuditVersion);
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(bib, 2, ManualTimeSource.Hand, "12:00:30.00"), "Operator", "Split"));
        // An entry made while the finish looked missing never replaces the impulse assigned meanwhile.
        error = await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(bib, 1, ManualTimeSource.Hand, "12:00:45.67"), "Operator", "Wrong impulse"));
        Assert.Contains("changed after it was shown", error.Message, StringComparison.Ordinal);
        Assert.Equal(version, timing.Snapshot.AuditVersion);
        // A classification would hide the review outcome; the impossible time is still refused.
        var competitor = timing.Snapshot.Results.Single().CompetitorId;
        await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: competitor, Status: TimingStatus.DNF), "Operator", "Test");
        error = await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(bib, 1, ManualTimeSource.Hand, "11:59:00.00", Replacing: [old]), "Operator", "Photocell missed"));
        Assert.Contains("not after the start", error.Message, StringComparison.Ordinal);
        await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: competitor), "Operator", "Test");
        version = timing.Snapshot.AuditVersion;

        var key = await timing.EnterCompetitorTimeAsync(list.Id, new(bib, 1, ManualTimeSource.Hand, "12:00:45.67", Replacing: [old]), "Operator", "Wrong impulse");
        var result = timing.Snapshot!.Results.Single();
        Assert.Equal((TimingStatus.Finished, 4567L, key), (result.Status, result.Hundredths!.Value, result.FinishKey));
        Assert.True(result.FinishManual);
        Assert.Equal("Unassigned", timing.Snapshot.Observations.Single(x => x.Observation.Key == old).State);
        var batch = timing.Snapshot.Audit.Where(x => x.Id > version).ToArray();
        Assert.Equal([DecisionKind.ManualTime, DecisionKind.Assignment, DecisionKind.Assignment], batch.Select(x => x.After.Kind));
        Assert.Equal(old, batch[1].Before.ObservationKey);

        // Undo adds history: the replaced impulse can be reassigned and the entered time removed.
        await timing.UndoAsync(batch[2].Id, "Operator", "Undo entry");
        await timing.UndoAsync(batch[1].Id, "Operator", "Undo entry");
        await timing.UndoAsync(batch[0].Id, "Operator", "Undo entry");
        Assert.Equal((old, 5000L), (timing.Snapshot!.Results.Single().FinishKey, timing.Snapshot.Results.Single().Hundredths!.Value));
        Assert.DoesNotContain(timing.Snapshot.Observations, x => x.Observation.Key == key);
        await timing.StopAsync();
    }

    [Fact]
    public async Task EetFinishFromCapturedBackupIsCalculatedSavedAndReportedAsReplacement()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 12);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        // One competitor at a time; the last one's finish impulse never arrives.
        for (var i = 0; i < 12; i++)
        {
            await Pulse(timing, source, 0, Noon + i * TimeSpan.TicksPerMinute);
            if (i < 11) { await Pulse(timing, source, 1, Noon + i * TimeSpan.TicksPerMinute + 40 * TimeSpan.TicksPerSecond + i * 1000); }
        }
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results.Count(x => x.Status == TimingStatus.Finished) == 11);
        var snapshot = timing.Snapshot!;
        var order = snapshot.StartOrder;
        var target = order[^1];
        // System B reads every finish 0.0123 s late (bib-dependent tenths of a millisecond on top), plus the missing one.
        var backup = snapshot.Results.Where(x => x.FinishKey is not null).Select(x =>
        {
            var a = snapshot.Observations.Single(o => o.Observation.Key == x.FinishKey).Observation;
            return B($"b{x.Bib}", a.DeviceTicks!.Value + 123_000 + x.Bib * 10);
        }).Append(B("b-target", TimingRulesTests.Date.ToDateTime(TimeOnly.MinValue).Ticks + Noon + 11 * TimeSpan.TicksPerMinute + 41 * TimeSpan.TicksPerSecond))
          .Append(B("b-start", TimingRulesTests.Date.ToDateTime(TimeOnly.MinValue).Ticks + Noon, channel: 0)).ToArray();

        var preparation = EetPreparation.Prepare(snapshot, target, 1, backup, 2 * TimeSpan.TicksPerSecond);
        Assert.Equal("b-target", preparation.ProposedTarget?.Key);
        Assert.Equal(11, preparation.TargetIndex);
        Assert.All(preparation.StartOrder, x => Assert.Equal("Paired", x.BackupState));
        var values = preparation.StartOrder.ToDictionary(x => x.Bib, x => (x.Backup!.DeviceTicks!.Value % TimeSpan.TicksPerDay, x.Backup.Precision, (string?)x.Backup.Key));
        // The operator leaves the earliest competitor out; the remaining ten are used.
        var excluded = new HashSet<int> { order[0] };
        var selection = EetCalculator.Select(EetPreparation.Candidates(preparation, values, excluded), target);
        Assert.Equal(order.Skip(1).Take(10), selection.Pairs.Select(x => x.Bib));
        var backupTarget = preparation.ProposedTarget!;
        var eet = EetCalculator.Calculate(target, 1, EetBackupSource.SystemB, backupTarget.DeviceTicks!.Value % TimeSpan.TicksPerDay,
            backupTarget.Precision, backupTarget.Key, selection.Pairs, [new(order[0], "Backup cell blocked")], "Wire break");
        // Simulator impulses carry seven decimals, so the mean (+0.0123 s plus the bibs' share) needs no rounding.
        Assert.Equal(7, eet.CorrectionPrecision);
        Assert.Equal(123_000 + selection.Pairs.Sum(x => (long)x.Bib), eet.CorrectionTicks);

        var key = await timing.EnterCompetitorTimeAsync(list.Id, new(target, 1, ManualTimeSource.Eet, Eet: eet), "Operator", "EET for missing finish");
        // The EET belongs to its competitor: it cannot be moved to a reference competitor.
        var other = order[0];
        var moveError = await Assert.ThrowsAsync<DomainValidationException>(() => timing.CorrectAsync(new(DecisionKind.Assignment, key, Bib: other), "Operator", "Move"));
        Assert.Contains($"calculated for bib {target}", moveError.Message, StringComparison.Ordinal);
        await timing.StopAsync();
        AssertResult(timing.Snapshot!);
        var saved = await workspace.ReadTimingAsync(list.Id);
        var report = TimingReportProjection.FromTiming(saved, timing.Snapshot!);
        Assert.False(report.AllResultsA);
        Assert.Equal(new TimingReportMissed(target, "Wire break", "System B"), Assert.Single(report.MissedA));

        await workspace.CloseAsync(); await workspace.OpenAsync(folder.File);
        await workspace.Timing!.SelectRunAsync(list.Id);
        AssertResult(workspace.Timing.Snapshot!);
        AssertResult(TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), new AlgeDecoderFactory()));

        void AssertResult(TimingSnapshot current)
        {
            var result = current.Results.Single(x => x.Bib == target);
            Assert.Equal((TimingStatus.Finished, key), (result.Status, result.FinishKey));
            Assert.True(result.FinishEet);
            Assert.False(result.FinishManual);
            Assert.Equal("Includes an EET replacement time · see history.", result.Detail);
            var observation = current.Observations.Single(x => x.Observation.Key == key).Observation;
            Assert.Equal(TimingRulesTests.Date, DateOnly.FromDateTime(new DateTime(observation.DeviceTicks!.Value)));
            Assert.Equal(eet.ResultTimeOfDay, observation.DeviceTicks!.Value % TimeSpan.TicksPerDay);
            Assert.Equal(eet, TimingEngine.CurrentDecision(new(DecisionKind.ManualTime, key), current.Audit).Timestamp!.Eet);
        }
    }

    [Fact]
    public async Task TheBackupReadingIsProposedOnlyForTheOneCompetitorWhoCouldHaveMadeIt()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 4);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        for (var i = 0; i < 4; i++)
        {
            await Pulse(timing, source, 0, Noon + i * TimeSpan.TicksPerMinute);
            await Pulse(timing, source, 1, Noon + i * TimeSpan.TicksPerMinute + 40 * TimeSpan.TicksPerSecond);
        }
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results.Count(x => x.Status == TimingStatus.Finished) == 4);
        var order = timing.Snapshot!.StartOrder;
        var finish = order.ToDictionary(x => x, x => timing.Snapshot.Results.Single(r => r.Bib == x).FinishKey!);
        // The second and third racers' finishes are lost; System B has the first, the third and the fourth.
        await timing.CorrectAsync(new(DecisionKind.Assignment, finish[order[1]], Ignored: true), "Operator", "Lost");
        await timing.CorrectAsync(new(DecisionKind.Assignment, finish[order[2]], Ignored: true), "Operator", "Lost");
        TimingObservation A(int bib) => timing.Snapshot!.Observations.Single(x => x.Observation.Key == finish[bib]).Observation;
        var backup = new[] { B("b0", A(order[0]).DeviceTicks!.Value + 123_000), B("b2", A(order[2]).DeviceTicks!.Value + 123_000),
            B("b3", A(order[3]).DeviceTicks!.Value + 123_000) };

        // Either racer without a finish could have made the reading between the neighbours: nothing is proposed.
        foreach (var bib in new[] { order[1], order[2] })
        {
            var ambiguous = EetPreparation.Prepare(timing.Snapshot!, bib, 1, backup, 2 * TimeSpan.TicksPerSecond);
            Assert.Null(ambiguous.ProposedTarget);
            Assert.Equal("b2", Assert.Single(ambiguous.TargetCandidates).Key);
        }
        // A racer who did not finish made no passage, so the reading is the other one's.
        var dnf = timing.Snapshot!.Results.Single(x => x.Bib == order[1]).CompetitorId;
        await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: dnf, Status: TimingStatus.DNF), "Operator", "Fell");
        var preparation = EetPreparation.Prepare(timing.Snapshot!, order[2], 1, backup, 2 * TimeSpan.TicksPerSecond);
        Assert.Equal("b2", preparation.ProposedTarget?.Key);

        var values = preparation.StartOrder.Where(x => x.Backup is not null)
            .ToDictionary(x => x.Bib, x => (x.Backup!.DeviceTicks!.Value % TimeSpan.TicksPerDay, x.Backup.Precision, (string?)x.Backup.Key));
        var pairs = EetCalculator.Select(EetPreparation.Candidates(preparation, values, new HashSet<int>()), order[2]).Pairs;
        Assert.Equal([order[0], order[3]], pairs.Select(x => x.Bib));
        var proposed = preparation.ProposedTarget!;
        var eet = EetCalculator.Calculate(order[2], 1, EetBackupSource.SystemB, proposed.DeviceTicks!.Value % TimeSpan.TicksPerDay, proposed.Precision,
            proposed.Key, pairs, [], "Wire break");
        // A repeated copy of a paired reading is the same reading, not a second candidate.
        var copied = EetPreparation.Prepare(timing.Snapshot!, order[2], 1, [.. backup, B("b0-copy", backup[0].DeviceTicks!.Value)], 2 * TimeSpan.TicksPerSecond);
        Assert.Equal("b2", copied.ProposedTarget?.Key);
        // A reference that gets a second finish after the calculation is in review, also behind a classification:
        // the EET is no longer saved.
        await timing.CorrectAsync(new(DecisionKind.Assignment, finish[order[1]], Bib: order[0]), "Operator", "Wrong bib");
        var classified = timing.Snapshot!.Results.Single(x => x.Bib == order[0]).CompetitorId;
        await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: classified, Status: TimingStatus.DSQ), "Operator", "Gate 3");
        Assert.Equal("Timing needs review", EetPreparation.Prepare(timing.Snapshot!, order[2], 1, backup, 2 * TimeSpan.TicksPerSecond)
            .StartOrder.Single(x => x.Bib == order[0]).Unavailable);
        var version = timing.Snapshot!.AuditVersion;
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(order[2], 1, ManualTimeSource.Eet, Eet: eet), "Operator", "EET"));
        Assert.Contains($"bib {order[0]} changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(version, timing.Snapshot.AuditVersion);
        await timing.StopAsync();
    }

    [Fact]
    public async Task AnOvertakingRacerBlocksTheProposalAndReferencesFollowTheActualStartOrder()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 5);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        // Starts every 20 s, runs of 90 s: several racers are on course at once.
        for (var i = 0; i < 5; i++) { await Pulse(timing, source, 0, Noon + i * 20 * TimeSpan.TicksPerSecond); }
        for (var i = 0; i < 5; i++) { await Pulse(timing, source, 1, Noon + (i * 20 + 90) * TimeSpan.TicksPerSecond); }
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results.Count(x => x.Status == TimingStatus.Finished) == 5);
        var order = timing.Snapshot!.StartOrder;
        var keys = order.ToDictionary(x => x, x => (Start: timing.Snapshot.Results.Single(r => r.Bib == x).StartKey!,
            Finish: timing.Snapshot.Results.Single(r => r.Bib == x).FinishKey!));
        TimingObservation A(string key) => timing.Snapshot!.Observations.Single(x => x.Observation.Key == key).Observation;
        var backup = Enumerable.Range(0, 4).Select(i => B($"b{i}", A(keys[order[i]].Finish).DeviceTicks!.Value + 123_000)).ToArray();
        // The second and the last racer's finishes are lost. The last one started before the second one's B reading and
        // could have overtaken, so the reading is not proposed until that racer is known not to have finished.
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[1]].Finish, Ignored: true), "Operator", "Lost");
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[4]].Finish, Ignored: true), "Operator", "Lost");
        var preparation = EetPreparation.Prepare(timing.Snapshot!, order[1], 1, backup, 2 * TimeSpan.TicksPerSecond);
        Assert.Null(preparation.ProposedTarget);
        Assert.Equal("b1", Assert.Single(preparation.TargetCandidates).Key);
        var dnf = timing.Snapshot!.Results.Single(x => x.Bib == order[4]).CompetitorId;
        await timing.CorrectAsync(new(DecisionKind.Status, CompetitorId: dnf, Status: TimingStatus.DNF), "Operator", "Fell");
        Assert.Equal("b1", EetPreparation.Prepare(timing.Snapshot!, order[1], 1, backup, 2 * TimeSpan.TicksPerSecond).ProposedTarget?.Key);

        // The third racer actually started before the second one: "started before" follows the start times.
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[1]].Start), "Operator", "Swap");
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[2]].Start), "Operator", "Swap");
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[2]].Finish), "Operator", "Swap");
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[1]].Start, Bib: order[2]), "Operator", "Swap");
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[2]].Start, Bib: order[1]), "Operator", "Swap");
        await timing.CorrectAsync(new(DecisionKind.Assignment, keys[order[2]].Finish, Bib: order[2]), "Operator", "Swap");
        var swapped = EetPreparation.Prepare(timing.Snapshot!, order[3], 1, backup, 2 * TimeSpan.TicksPerSecond);
        Assert.Equal([order[0], order[2], order[1], order[4]], swapped.StartOrder.Select(x => x.Bib));
        Assert.Equal(3, swapped.TargetIndex);
        await timing.StopAsync();
    }

    [Fact]
    public async Task AForerunnerOnCourseCouldHaveMadeTheBackupReading()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var forerunner = new TimingForerunner("A", "Ada", "SYNTHRUNNER", "FIN");
        await ForerunnerTimingTests.SaveForerunnersAsync(workspace, list, forerunner);
        await timing.SetForerunnersAsync(list.Id, [forerunner]);
        await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        var second = TimeSpan.TicksPerSecond;
        await Pulse(timing, source, 0, Noon);
        await Pulse(timing, source, 0, Noon + 20 * second);
        await Pulse(timing, source, 0, Noon + 40 * second);
        // The forerunner's finish is lost; the first competitor's finish is assigned to them by hand. The second
        // competitor's finish is lost too, and System B read one finish at 12:01:30.
        await Pulse(timing, source, 1, Noon + 70 * second);
        var order = timing.Snapshot!.StartOrder;
        var first = timing.Snapshot.Forerunners.Single().FinishKey!;
        await timing.CorrectAsync(new(DecisionKind.Assignment, first, Bib: order[0]), "Operator", "Forerunner missed the finish");
        var day = TimingRulesTests.Date.ToDateTime(TimeOnly.MinValue).Ticks;
        var backup = new[] { B("b0", day + Noon + 70 * second + 123_000), B("b1", day + Noon + 90 * second + 123_000) };
        Assert.Null(EetPreparation.Prepare(timing.Snapshot!, order[1], 1, backup, 2 * second).ProposedTarget);
        // Once the forerunner's own finish is known, the reading can only be the competitor's.
        await Pulse(timing, source, 1, Noon + 100 * second);
        Assert.Equal(TimingStatus.Finished, timing.Snapshot!.Forerunners.Single().Status);
        Assert.Equal("b1", EetPreparation.Prepare(timing.Snapshot!, order[1], 1, backup, 2 * second).ProposedTarget?.Key);
        await timing.StopAsync();
    }

    [Fact]
    public async Task AnEetIsRejectedWhenItsReferenceTimesChanged()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 3);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        for (var i = 0; i < 3; i++)
        {
            await Pulse(timing, source, 0, Noon + i * TimeSpan.TicksPerMinute);
            if (i < 2) { await Pulse(timing, source, 1, Noon + i * TimeSpan.TicksPerMinute + 40 * TimeSpan.TicksPerSecond); }
        }
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results.Count(x => x.Status == TimingStatus.Finished) == 2);
        var snapshot = timing.Snapshot!;
        var target = snapshot.StartOrder[^1];
        var pairs = snapshot.Results.Where(x => x.FinishKey is not null).Select(x =>
        {
            var a = snapshot.Observations.Single(o => o.Observation.Key == x.FinishKey).Observation;
            return new EetPair(x.Bib, a.Key, a.DeviceTicks!.Value, a.Precision, a.DeviceTicks.Value % TimeSpan.TicksPerDay + 100_000, 4);
        }).ToArray();
        var eet = EetCalculator.Calculate(target, 1, EetBackupSource.HandTime, Noon + 2 * TimeSpan.TicksPerMinute + 41 * TimeSpan.TicksPerSecond,
            2, null, pairs, [], "Batteries");

        // A reference finish is unassigned after the calculation: the EET must be calculated again.
        var moved = snapshot.Results.First(x => x.FinishKey is not null);
        await timing.CorrectAsync(new(DecisionKind.Assignment, moved.FinishKey), "Operator", "Wrong bib");
        var version = timing.Snapshot!.AuditVersion;
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(target, 1, ManualTimeSource.Eet, Eet: eet), "Operator", "EET"));
        // A calculation for another position or with a changed result is never saved either.
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(target, 0, ManualTimeSource.Eet, Eet: eet), "Operator", "EET"));
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.EnterCompetitorTimeAsync(list.Id,
            new(target, 1, ManualTimeSource.Eet, Eet: eet with { ResultTimeOfDay = eet.ResultTimeOfDay + 10_000 }), "Operator", "EET"));
        Assert.Equal(version, timing.Snapshot.AuditVersion);
        await timing.StopAsync();
    }

    [Fact]
    public async Task StoreRejectsAnEetWhoseReferencesAreNotDeviceInputOfTheRun()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1);
        await workspace.CloseAsync();
        await using var session = await new SqliteSeriesFileStore().OpenAsync(folder.File);
        var store = (ITimingStore)session;
        var day = TimingRulesTests.Date.ToDateTime(TimeOnly.MinValue).Ticks;
        var pairs = Enumerable.Range(1, 10).Select(i => new EetPair(100 + i, $"{Guid.NewGuid():N}:{i}", day + Noon + i * TimeSpan.TicksPerMinute, 4,
            Noon + i * TimeSpan.TicksPerMinute + 1000, 4)).ToArray();
        var eet = EetCalculator.Calculate(list.Plan.Entries[0].Bib, 1, EetBackupSource.SystemB, Noon + 20 * TimeSpan.TicksPerMinute, 4, null, pairs, [], "Wire break");
        var key = ManualTimestamp.KeyPrefix + Guid.NewGuid().ToString("N");
        var entry = new ManualTimestamp(1, day + eet.ResultTimeOfDay, eet.ResultPrecision, "x") { Source = ManualTimeSource.Eet, Eet = eet };
        Assert.True(entry.IsValid);
        await Assert.ThrowsAsync<DomainValidationException>(() => store.AppendTimingAuditAsync(list.Id, 0,
            new(DecisionKind.ManualTime, key), new(DecisionKind.ManualTime, key, Timestamp: entry), "Operator", "EET", TimingRulesTests.At));
        // A stored calculation must also add up: a changed result is invalid before any reference is checked.
        await Assert.ThrowsAsync<DomainValidationException>(() => store.AppendTimingAuditAsync(list.Id, 0,
            new(DecisionKind.ManualTime, key), new(DecisionKind.ManualTime, key, Timestamp: entry with { DeviceTicks = entry.DeviceTicks + 1000 }),
            "Operator", "EET", TimingRulesTests.At));
        Assert.Empty((await store.ReadTimingAsync(list.Id)).Audit);
    }

    private static TimingObservation B(string key, long ticks, int channel = 1) =>
        new(key, Guid.Empty, 0, "B", key, ObservationKind.Impulse, channel, ticks, 7, null, false, "b-clock", "B impulse");

    private static async Task Pulse(TimingWorkspace timing, SimulatorTimingSource source, int channel, long ticks)
    {
        var count = timing.Snapshot!.Observations.Count;
        await source.PulseAsync(channel, ticks);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count > count
            && timing.Snapshot.Observations[^1].State != "Unassigned");
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-time-entry-tests", Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(_root, "synthetic.ost");
        public TestFolder() => Directory.CreateDirectory(_root);
        public void Dispose()
        {
            if (!Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-time-entry-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Unexpected test folder."); }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }
}

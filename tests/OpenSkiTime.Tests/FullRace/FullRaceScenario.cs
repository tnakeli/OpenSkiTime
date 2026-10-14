using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests.FullRace;

internal sealed record RunEvidence(int Starters, int Finished, int Dns, int Dnf, int Dsq, int RawPackets, int Observations,
    int AuditEntries, int ManualTimestamps, int IgnoredImpulses, IReadOnlyList<string> Discrepancies);

// Race-day changes on top of the reference race. The default reproduces the reference race exactly.
// SavedPoints: the FIS points are saved with the series and the draw reads them from the file instead of the list.
// FirstBib/SkippedBibs: bib numbering of the draw. Move: an athlete (index into SyntheticRace.Athletes) moved after the
// draw to another Run 1 start position with a reason, keeping the bib. Forerunners: timed before each run's competitors.
internal sealed record FullRaceVariant(bool SavedPoints = false, int FirstBib = 1, IReadOnlyList<int>? SkippedBibs = null,
    (int Athlete, int ToPosition)? Move = null, IReadOnlyList<TimingForerunner>? Forerunners = null)
{
    public static FullRaceVariant Reference { get; } = new();
}

internal sealed record FullRaceOutcome(string FilePath, IReadOnlyList<(int Position, int Bib, string Code)> Run1Order,
    IReadOnlyList<int> Run2Order, RunEvidence Run1, RunEvidence Run2, IReadOnlyList<ExpectedFinalRow> Expected,
    IReadOnlyList<FisResultRow> Actual, decimal CalculatedPenalty, decimal AppliedPenalty, string XmlFileName,
    int XmlRankedResults, int XmlNotRanked, IReadOnlyList<string> Discrepancies, IReadOnlyList<string> Log)
{
    // Order-independent canonical summary used to compare repeated executions.
    public string Canonical()
    {
        var text = new StringBuilder();
        foreach (var x in Run1Order) { text.Append(CultureInfo.InvariantCulture, $"R1 {x.Position} {x.Bib} {x.Code}\n"); }
        foreach (var bib in Run2Order) { text.Append(CultureInfo.InvariantCulture, $"R2 {bib}\n"); }
        foreach (var x in Actual)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"F {x.Entry.Bib} {x.Entry.Entrant.Athlete.FederationCode} {x.Status} {x.Run1Hundredths} {x.Run2Hundredths} {x.TotalHundredths} {x.Rank}\n");
        }
        text.Append(CultureInfo.InvariantCulture, $"P {CalculatedPenalty} {AppliedPenalty} {XmlFileName} {XmlRankedResults} {XmlNotRanked}\n");
        return text.ToString();
    }
}

// Drives the complete synthetic two-run slalom through the application boundary: real SQLite series file, TSV import,
// the FIS draw, the simulator timing source with the ALGE decoder, audited corrections, Run 2 order, results, penalty,
// XML approval, reopen and backup. Every step is checked against ExpectedResults.
internal sealed class FullRaceScenario(SyntheticRace race, string root, FullRaceVariant? variant = null)
{
    private readonly FullRaceVariant _variant = variant ?? FullRaceVariant.Reference;
    private IReadOnlyList<TimingForerunner> Forerunners => _variant.Forerunners ?? [];
    public const string Operator = "Race Secretary";
    private readonly List<string> _log = [];
    private readonly List<string> _discrepancies = [];

    // Optional observer for checkpoints during and after each run (for example live-timing snapshots). It receives the
    // run number, the run's start list, the current timing snapshot and a checkpoint label; it must not change timing.
    public Func<int, StartListRevision, TimingSnapshot, string, Task>? Observe { get; init; }

    public async Task<FullRaceOutcome> RunAsync()
    {
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "synthetic-full-race.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());

        // Preparation: series, competition, import, points list.
        var series = await workspace.CreateAsync(file, race.Series);
        series = await workspace.SaveCompetitionAsync(null, race.Competition, series.Revision);
        var competition = Assert.Single(series.Competitions);
        Log($"Created series '{race.Series.Name}' and competition {competition.Values.ShortLabel} ({competition.Values.Discipline}, {competition.Values.RunCount} runs, {competition.Values.IntermediateCount} intermediate).");
        var tsv = race.CompetitorTsv();
        var preview = TsvExchange.Preview(tsv, series, await workspace.ReadCompetitorDeskAsync());
        Check(preview.Rows.Count == SyntheticRace.AthleteCount, $"Import preview rows {preview.Rows.Count}");
        Check(preview.Rows.All(x => x.IsNew && x.Warnings.Count == 0), "Import preview has warnings or matched rows");
        Check(preview.Warnings.Count == 0, "Import preview warnings: " + string.Join("; ", preview.Warnings));
        var commit = new ImportCommit(preview.SeriesId, preview.Revision, preview.SourceHash,
            preview.Rows.Select(x => new ImportCommitRow(x.CompetitorId, x.Values, x.Entries)).ToArray());
        var imported = await workspace.ApplyImportAsync(commit);
        Check(imported is { Created: SyntheticRace.AthleteCount, Updated: 0, AlreadyApplied: false }, $"Import result {imported}");
        // Repeated action: applying the same accepted preview again must not create duplicates.
        var again = await workspace.ApplyImportAsync(commit);
        Check(again.AlreadyApplied, "Re-applying the same import was not idempotent");
        var desk = await workspace.ReadCompetitorDeskAsync();
        var entered = desk.Participations.Where(x => x.CompetitionId == competition.Id && x.Participates).Select(x => x.CompetitorId).ToHashSet();
        Check(desk.Competitors.Count == SyntheticRace.AthleteCount && entered.Count == SyntheticRace.AthleteCount,
            $"Registered {desk.Competitors.Count}, participating {entered.Count}");
        foreach (var athlete in race.Athletes)
        {
            var saved = desk.Competitors.SingleOrDefault(x => x.Values.FederationCode == athlete.Code);
            Check(saved is not null && saved.Values == athlete.Values, $"Imported athlete {athlete.Code} differs");
        }
        Log($"Imported {imported.Created} athletes via TSV preview; {entered.Count} participate; re-import AlreadyApplied={again.AlreadyApplied}.");

        var list = FisPointsListReader.Read(race.PointsListArchive());
        Check(list.ValidFrom <= SyntheticRace.RaceDate && list.ValidTo >= SyntheticRace.RaceDate && list.PenaltyRules is not null,
            "Synthetic points list is not valid on the race date");

        // Run 1 draw (fixed seed) and independent draw-rule checks.
        decimal? ListPoints(string code) => list.Athletes.TryGetValue(code, out var fis) && fis.Points?.TryGetValue("SL", out var value) == true ? value : null;
        var pointsSource = new PointsListSource(list.ListCode, list.ValidFrom, list.ValidTo, list.PenaltyRules);
        if (_variant.SavedPoints)
        {
            // Update from FIS list + Save: every competitor's points saved with the series in one batch with the list.
            var info = new FisPointsListInfo(list.ListCode, list.Name, list.ValidFrom, list.ValidTo, list.PenaltyRules);
            var rows = desk.Competitors.Select(x => new DeskBatchRow(x.Id, x.Values, [],
                list.Athletes.TryGetValue(x.Values.FederationCode!, out var fis) ? CompetitorFisPoints.FromList(list.ListCode, fis.Points) : null, true)).ToArray();
            await workspace.ApplyDeskBatchAsync(new DeskBatch(series.Id, desk.Revision, rows, [], [info]));
            desk = await workspace.ReadCompetitorDeskAsync();
            Check(desk.PointsList(list.ListCode) is { } saved && saved with { PenaltyRules = null } == info with { PenaltyRules = null }
                && saved.PenaltyRules is not null, "Saved points list differs from the loaded list");
            Check(desk.Competitors.All(x => desk.FisPointsOf(x.Id)?.Sl == ListPoints(x.Values.FederationCode!)), "Saved SL points differ from the list");
            Log($"Saved the SL points of {desk.Competitors.Count(x => desk.FisPointsOf(x.Id)?.Sl is not null)} athletes with points list {list.ListCode}.");
        }
        // The draw reads the points saved with the series (or, for the reference race, the loaded list).
        var entrants = desk.Competitors.Where(x => entered.Contains(x.Id)).Select(x => new DrawEntrant(x.Id, x.Values,
            _variant.SavedPoints ? desk.FisPointsOf(x.Id)?.Sl : ListPoints(x.Values.FederationCode!))).ToArray();
        var options = new DrawOptions(15, 30, _variant.FirstBib, _variant.SkippedBibs);
        var plan1 = FisStartOrder.FirstRun(competition.Id, competition.Values, Gender.Female, entrants, pointsSource,
            options, SyntheticRace.DrawSeed);
        var replay = FisStartOrder.FirstRun(competition.Id, competition.Values, Gender.Female, entrants.Reverse().ToArray(), pointsSource,
            options, SyntheticRace.DrawSeed);
        Check(plan1.Entries.Select(x => x.Entrant.CompetitorId).SequenceEqual(replay.Entries.Select(x => x.Entrant.CompetitorId)),
            "Draw is not reproducible from the saved seed and input");
        CheckFirstRunDraw(plan1);
        if (_variant.Move is { } move) { plan1 = MoveAfterDraw(plan1, race.Athletes[move.Athlete].Code, move.ToPosition); }
        var desk1 = await workspace.SaveStartListAsync(new(plan1, desk.Revision, Operator, "Draw", At(10, 0)));
        var list1 = desk1.Revisions.Single(x => x.Plan.RunNumber == 1);
        Check(list1.Plan.Entries.Select(x => (x.Position, x.Bib, x.Entrant.CompetitorId)).SequenceEqual(plan1.Entries.Select(x => (x.Position, x.Bib, x.Entrant.CompetitorId)))
            && (list1.Plan.Changes?.Count ?? 0) == (plan1.Changes?.Count ?? 0), "Saved Run 1 start list differs from the drawn and changed plan");
        var run1Order = list1.Plan.Entries.Select(x => (x.Position, x.Bib, x.Entrant.Athlete.FederationCode!)).ToArray();
        Log($"Run 1 draw saved: {list1.Plan.Entries.Count} starters, first group {list1.Plan.Entries.Count(x => x.Group == "First group")}, " +
            $"no points {list1.Plan.Entries.Count(x => x.Group == "No points")}, seed {SyntheticRace.DrawSeed}.");

        // Forerunners of both runs are saved with the race information (as in Results or the Timing Forerunners dialog).
        if (Forerunners.Count > 0)
        {
            var information = (await workspace.ReadRaceInformationAsync(competition.Id))?.Values ?? RaceInformation.Empty(competition.Values);
            information = information with { Runs = information.Runs.Select(x => x with
            { Forerunners = Forerunners.Select(r => new RaceForerunner(r.Letter, new(r.FirstName, r.LastName, r.Nation))).ToArray() }).ToArray() };
            await workspace.SaveRaceInformationAsync(competition.Id, information, (await workspace.ReadAsync()).Revision, At(9, 30));
            Log($"Saved forerunners {string.Join(", ", Forerunners.Select(x => x.Letter))} for both runs.");
        }

        // Run 1 timing.
        var expected1 = ExpectedResults.Run(1, run1Order, race.Run1);
        var evidence1 = await TimeRunAsync(workspace, list1, 1, race.Run1);
        var timing1 = workspace.Timing!.Snapshot!;
        CompareRun(1, timing1, expected1);

        // Run 2 start list from Run 1 classification, verified independently.
        var replay1 = await workspace.ReadTimingAsync(list1.Id);
        var finishes = timing1.ToRunFinishes();
        var plan2 = FisStartOrder.SecondRun(list1, finishes, 30);
        var expectedOrder = ExpectedResults.SecondRunOrder(expected1, 30);
        var run2Order = plan2.Entries.Select(x => x.Bib).ToArray();
        Check(run2Order.SequenceEqual(expectedOrder), "Run 2 order differs: expected " + string.Join(",", expectedOrder) + " actual " + string.Join(",", run2Order));
        var eligible = expected1.Where(x => x.Outcome == RunOutcome.Finished).Select(x => x.Bib).ToHashSet();
        Check(run2Order.ToHashSet().SetEquals(eligible), "Run 2 includes ineligible competitors or misses eligible ones");
        Check(plan2.Entries.All(x => list1.Plan.Entries.Single(e => e.Entrant.CompetitorId == x.Entrant.CompetitorId).Bib == x.Bib), "Run 2 changed a bib");
        var reversedCount = plan2.Entries.Count(x => x.Group == "Reversed group");
        Check(reversedCount == 31, $"Reversed group should expand to 31 at the 30/31 tie, was {reversedCount}");
        var desk2 = await workspace.SaveStartListAsync(new(plan2, (await workspace.ReadStartListsAsync(competition.Id)).SeriesRevision, Operator, "Run 2 start order", At(12, 30),
            TimingReplay.InputVersion(replay1)));
        var list2 = desk2.Revisions.Single(x => x.Plan.RunNumber == 2);
        Log($"Run 2 start list: {plan2.Entries.Count} eligible of {list1.Plan.Entries.Count}; reversed group {reversedCount} (tie at rank 30).");
        var run2Positions = list2.Plan.Entries.Select(x => (x.Position, x.Bib, x.Entrant.Athlete.FederationCode!)).ToArray();
        var expected2 = ExpectedResults.Run(2, run2Positions, race.Run2);
        var evidence2 = await TimeRunAsync(workspace, list2, 2, race.Run2);
        var timing2 = workspace.Timing!.Snapshot!;
        CompareRun(2, timing2, expected2);
        await workspace.Timing.SelectRunAsync(list1.Id);
        timing1 = workspace.Timing.Snapshot!;

        // Results, penalty, XML and approval.
        var result = FisRaceResults.Assemble(list1, timing1, list2, timing2);
        var expectedFinal = ExpectedResults.Final(expected1, expected2);
        var listed = result.Rows.OrderByOfficialResult(x => x.TotalHundredths, x => x.Entry.Bib).ToArray();
        CompareFinal(expectedFinal, listed);
        var athletes = race.Athletes.ToDictionary(x => x.Code);
        var profile = list.PenaltyRules!.Resolve("FIS", Discipline.Slalom, Gender.Female);
        var penalty = FisPenalty.Calculate(profile, result.PenaltyCompetitors);
        var starters = expected1.Where(x => x.Outcome != RunOutcome.DNS).Select(x => x.Code).ToHashSet();
        var expectedPenalty = ExpectedResults.Penalty(expectedFinal, athletes, starters, profile.FValue, profile.MaximumPoints,
            profile.Minimum, profile.Maximum, profile.Adder);
        Check(penalty.Calculated == expectedPenalty.Calculated && penalty.Applied == expectedPenalty.Applied,
            $"Penalty expected {expectedPenalty} actual ({penalty.Calculated}, {penalty.Applied})");
        Log(FormattableString.Invariant($"Penalty F={profile.FValue} max={profile.MaximumPoints}: calculated {penalty.Calculated}, applied {penalty.Applied} (expected {expectedPenalty.Calculated}/{expectedPenalty.Applied}; double minimum {penalty.DoubleMinimum})."));
        var details = new FisXmlDetails("FIS", new("Tanja", "SYNTHDELEGATE", "SWE", "9001"), new("Pekka", "SYNTHCHIEF", "FIN"),
            [new(58, 56, "10:00", new("Ola", "SYNTHSETTER", "NOR")), new(60, 58, "13:00", new("Ulla", "SYNTHSETTER", "AUT"))]);
        var xml = FisResultXml.Create(result, race.Series, penalty, details);
        var xmlName = FisResultXml.FileName(result, race.Series);
        var (ranked, notRanked) = CheckXml(xml, expectedFinal);
        var replay2 = await workspace.ReadTimingAsync(list2.Id);
        var seriesRevision = (await workspace.ReadAsync()).Revision;
        var approved = await workspace.ApproveResultAsync(new(competition.Id, list1.Id, list2.Id,
            ResultSourceFingerprint.Create(replay1, replay2), seriesRevision, "Chief of Timing", xmlName, xml, penalty.Calculated, penalty.Applied));
        Log($"Approved results revision {approved.Revision}: {xmlName}, {ranked} ranked + {notRanked} not ranked in XML.");

        // Persistence: close, reopen and compare; backup, open the backup as a restored series.
        var before = Fingerprint(timing1, timing2);
        var forerunnersBefore = ForerunnerFingerprint(timing1, timing2);
        var evidence = await EvidenceAsync(workspace, list1.Id, list2.Id);
        var backup = Path.Combine(root, "backup", "synthetic-full-race-backup.ost");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await workspace.BackupAsync(backup);
        await workspace.CloseAsync();
        foreach (var path in new[] { file, backup })
        {
            await workspace.OpenAsync(path);
            await workspace.Timing!.SelectRunAsync(list1.Id);
            await workspace.Timing.SetForerunnersAsync(list1.Id, Forerunners);
            var reopened1 = workspace.Timing.Snapshot!;
            await workspace.Timing.SelectRunAsync(list2.Id);
            await workspace.Timing.SetForerunnersAsync(list2.Id, Forerunners);
            var reopened2 = workspace.Timing.Snapshot!;
            Check(ForerunnerFingerprint(reopened1, reopened2) == forerunnersBefore, $"Forerunner timing changed after reopening {Path.GetFileName(path)}");
            if (_variant.SavedPoints)
            {
                var reopenedDesk = await workspace.ReadCompetitorDeskAsync();
                Check(reopenedDesk.Competitors.All(x => reopenedDesk.FisPointsOf(x.Id)?.Sl == ListPoints(x.Values.FederationCode!)),
                    $"Saved FIS points changed after reopening {Path.GetFileName(path)}");
            }
            Check(Fingerprint(reopened1, reopened2) == before, $"Timing changed after reopening {Path.GetFileName(path)}");
            var stored = Assert.Single(await workspace.ReadApprovedResultsAsync(competition.Id));
            Check(stored.Xml.AsSpan().SequenceEqual(xml) && stored.AppliedPenalty == penalty.Applied, "Approved XML changed after reopen");
            var reopenedResult = FisRaceResults.Assemble(list1, reopened1, list2, reopened2);
            Check(reopenedResult.Rows.Select(x => (x.Entry.Bib, x.Status, x.TotalHundredths, x.Rank))
                .SequenceEqual(result.Rows.Select(x => (x.Entry.Bib, x.Status, x.TotalHundredths, x.Rank))), "Results changed after reopen");
            Check((await workspace.ReadTimingAsync(list1.Id)).Packets.Count == evidence1.RawPackets
                && (await workspace.ReadTimingAsync(list2.Id)).Packets.Count == evidence2.RawPackets, "Raw packets changed after reopen");
            Check(await EvidenceAsync(workspace, list1.Id, list2.Id) == evidence,
                $"Raw packet bytes, capture sessions or audit records differ after reopening {Path.GetFileName(path)}");
            await workspace.CloseAsync();
            Log($"Reopened {Path.GetFileName(path)}: timing, audit ({reopened1.Audit.Count}+{reopened2.Audit.Count}), raw packets, results and approved XML intact.");
        }
        return new(file, run1Order, run2Order, evidence1, evidence2, expectedFinal, listed, penalty.Calculated, penalty.Applied,
            xmlName, ranked, notRanked, _discrepancies.ToArray(), _log.ToArray());
    }

    private void CheckFirstRunDraw(StartListPlan plan)
    {
        var entries = plan.Entries;
        // Independent bib numbering: from the first bib upwards in start order, never handing out a skipped number.
        var skipped = (_variant.SkippedBibs ?? []).ToHashSet();
        var expectedBibs = Enumerable.Range(_variant.FirstBib, SyntheticRace.AthleteCount + skipped.Count).Where(x => !skipped.Contains(x))
            .Take(SyntheticRace.AthleteCount);
        Check(entries.Count == SyntheticRace.AthleteCount && entries.Select(x => x.Bib).SequenceEqual(expectedBibs),
            $"Run 1 bibs are not {_variant.FirstBib}.. in start order without the skipped bibs");
        var withPoints = entries.Where(x => x.Entrant.Points is not null).ToArray();
        var sortedPoints = withPoints.Select(x => x.Entrant.Points!.Value).Order().ToArray();
        // First group: the best 15, expanded by equal points at the boundary (indices 13–15 share points → 16).
        var boundary = sortedPoints[14];
        var firstSize = sortedPoints.Count(x => x <= boundary);
        Check(firstSize == 16, $"First group expected 16 (equal points at the boundary), got {firstSize}");
        var first = entries.Take(firstSize).ToArray();
        Check(first.All(x => x.Group == "First group" && x.Entrant.Points <= boundary), "First group composition is wrong");
        var rest = entries.Skip(firstSize).TakeWhile(x => x.Entrant.Points is not null).ToArray();
        Check(rest.Zip(rest.Skip(1)).All(x => x.First.Entrant.Points <= x.Second.Entrant.Points), "Points order after the first group is not ascending");
        Check(entries.Skip(firstSize + rest.Length).All(x => x.Entrant.Points is null && x.Group == "No points")
            && entries.Count(x => x.Entrant.Points is null) == SyntheticRace.AthleteCount - SyntheticRace.PointsAthletes, "No-points group is not last");
        var equal = FisStartOrder.EqualPointsCompetitors(entries);
        Check(equal.Count == 8, $"Expected 8 competitors sharing points (3+2+3), got {equal.Count}");
    }

    private async Task<RunEvidence> TimeRunAsync(SeriesWorkspace workspace, StartListRevision list, int run,
        IReadOnlyDictionary<string, SyntheticRun> plans)
    {
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.SetForerunnersAsync(list.Id, Forerunners);
        await timing.FollowStartOrderAsync(true);
        if (Forerunners.Count > 0)
        {
            Check(RaceFlow.Waiting(timing.Snapshot!).Select(x => x.Bib).Take(Forerunners.Count + 1)
                .SequenceEqual(Forerunners.Select(x => x.Key).Append(list.Plan.Entries.OrderBy(x => x.Position).First().Bib)),
                $"Run {run}: forerunners do not wait before the first competitor");
        }
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Synthetic full race", SyntheticRace.RaceDate, Simulation: true)
            { IntermediateChannels = [2], Operator = Operator }, Operator);
        var events = new List<(long At, int Order, Func<Task> Act)>();
        var pulses = 0; var manual = 0; var ignored = 0;
        // Forerunners start one minute apart, the last one a minute before the first competitor, and are fully timed.
        var forerunnerTimes = new Dictionary<int, long>();
        for (var i = 0; i < Forerunners.Count; i++)
        {
            var start = SyntheticRace.StartTimeOfDay(run, 1) - (Forerunners.Count - i) * TimeSpan.TicksPerMinute;
            var intermediate = start + TimeSpan.FromSeconds(24).Ticks + i * 1_230_000L;
            var finish = start + TimeSpan.FromSeconds(52).Ticks + i * 3_170_000L;
            events.Add((start, 1, () => PulseAsync(timing, source, 0, start)));
            events.Add((intermediate, 1, () => PulseAsync(timing, source, 2, intermediate)));
            events.Add((finish, 1, () => PulseAsync(timing, source, 1, finish)));
            pulses += 3;
            forerunnerTimes[Forerunners[i].Key] = (finish - start) / (TimeSpan.TicksPerMillisecond * 10);
        }
        foreach (var entry in list.Plan.Entries)
        {
            var code = entry.Entrant.Athlete.FederationCode!;
            var plan = plans[code];
            var bib = entry.Bib;
            var start = SyntheticRace.StartTimeOfDay(run, entry.Position);
            if (plan.Outcome == RunOutcome.DNS)
            {
                events.Add((start, 0, () => timing.ClassifyExpectedStarterAsync(bib, TimingStatus.DNS, Operator, "Did not start")));
                continue;
            }
            events.Add((start, 1, async () => { await PulseAsync(timing, source, 0, start); }));
            pulses++;
            var dnfBeforeSplit = plan.Outcome == RunOutcome.DNF && plan.MissingIntermediate;
            if (!plan.MissingIntermediate && !dnfBeforeSplit)
            {
                events.Add((start + plan.IntermediateTicks, 1, () => PulseAsync(timing, source, 2, start + plan.IntermediateTicks)));
                pulses++;
            }
            var finish = start + plan.FinishTicks;
            if (plan.Outcome == RunOutcome.DNF)
            {
                events.Add((finish, 2, () => timing.CorrectStatusesAsync([bib], TimingStatus.DNF, Operator, "Fell, did not finish")));
            }
            else if (plan.MissingFinish)
            {
                var hand = SyntheticRace.HandTimedFinish(start, plan);
                events.Add((finish + TimeSpan.TicksPerSecond * 2, 2, async () =>
                {
                    var key = await timing.AddManualTimestampAsync(list.Id, 1,
                        new TimeOnly(hand).ToString("HH:mm:ss.ff", CultureInfo.InvariantCulture), Operator,
                        "Finish photocell missed the impulse; hand time from the finish judge");
                    await timing.CorrectAsync(new(DecisionKind.Assignment, key, Bib: bib), Operator, "Assign hand-timed finish");
                }));
                manual++;
            }
            else
            {
                events.Add((finish, 1, () => PulseAsync(timing, source, 1, finish)));
                pulses++;
                if (plan.DuplicateFinish)
                {
                    // A photocell double impulse 0.03 s after the real finish. The next skier is on course, so the
                    // operator must remove it before that skier's finish arrives.
                    var bounce = finish + 300_000;
                    events.Add((bounce, 1, async () =>
                    {
                        await PulseAsync(timing, source, 1, bounce);
                        var observation = timing.Snapshot!.Observations.Single(x => x.Observation.Channel == 1
                            && x.Observation.DeviceTicks % TimeSpan.TicksPerDay == bounce);
                        await timing.CorrectAsync(new(DecisionKind.Assignment, observation.Observation.Key, Ignored: true), Operator,
                            "Photocell double impulse");
                        await timing.FollowStartOrderAsync(true);
                    }));
                    pulses++; ignored++;
                }
            }
        }
        var ordered = events.OrderBy(x => x.At).ThenBy(x => x.Order).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            await ordered[i].Act();
            if (Observe is not null && i + 1 is var done && done < ordered.Length && done % (ordered.Length / 4) == 0)
            { await Observe(run, list, timing.Snapshot!, $"run{run}-{done * 100 / ordered.Length}pct"); }
        }

        // Post-run classification and an operator mistake that is undone (audit appends a reversal).
        foreach (var entry in list.Plan.Entries.Where(x => plans[x.Entrant.Athlete.FederationCode!].Outcome == RunOutcome.DSQ))
        {
            var plan = plans[entry.Entrant.Athlete.FederationCode!];
            await timing.CorrectStatusesAsync([entry.Bib], TimingStatus.DSQ, Operator, "Referee report",
                new DisqualificationDetails(plan.DsqGate, plan.DsqReason!, "Gate judge 3"));
        }
        if (run == 1)
        {
            var mistaken = list.Plan.Entries.Single(x => x.Entrant.Athlete.FederationCode == race.Athletes[SyntheticRace.Run1UndoneMistake].Code).Bib;
            await timing.CorrectStatusesAsync([mistaken], TimingStatus.DSQ, Operator, "DSQ entered on the wrong bib");
            await timing.UndoAsync(timing.Snapshot!.Audit[^1].Id, Operator, "Wrong bib; undo DSQ");
        }
        await timing.StopAsync();
        var snapshot = timing.Snapshot!;
        if (Observe is not null) { await Observe(run, list, snapshot, $"run{run}-complete"); }
        var data = await workspace.ReadTimingAsync(list.Id);
        var discrepancies = new List<string>();
        if (!snapshot.Complete) { discrepancies.Add($"Run {run} is not complete"); }
        if (snapshot.Unresolved != 0) { discrepancies.Add($"Run {run} has {snapshot.Unresolved} unresolved impulses"); }
        // Forerunners: finished with their own time (truncated to hundredths), never ranked, never among the results.
        foreach (var runner in Forerunners)
        {
            var row = snapshot.Forerunners.SingleOrDefault(x => x.Bib == runner.Key);
            if (row is not { Status: TimingStatus.Finished, Rank: null, IsForerunner: true } || row.Hundredths != forerunnerTimes[runner.Key])
            { discrepancies.Add($"Run {run} forerunner {runner.Letter}: expected finished {forerunnerTimes[runner.Key]} unranked, actual {row?.Status} {row?.Hundredths} rank {row?.Rank}"); }
        }
        if (snapshot.Results.Any(x => x.IsForerunner || TimingForerunner.IsKey(x.Bib)) || snapshot.Results.Count != list.Plan.Entries.Count)
        { discrepancies.Add($"Run {run} results include forerunners or miss competitors"); }
        if (data.Packets.Count != pulses) { discrepancies.Add($"Run {run} raw packets {data.Packets.Count}, pulses sent {pulses}"); }
        foreach (var d in discrepancies) { _discrepancies.Add(d); }
        var evidence = new RunEvidence(list.Plan.Entries.Count,
            snapshot.Results.Count(x => x.Status == TimingStatus.Finished), snapshot.Results.Count(x => x.Status == TimingStatus.DNS),
            snapshot.Results.Count(x => x.Status == TimingStatus.DNF), snapshot.Results.Count(x => x.Status == TimingStatus.DSQ),
            data.Packets.Count, snapshot.Observations.Count, snapshot.Audit.Count,
            snapshot.Audit.Count(x => x.After.Kind == DecisionKind.ManualTime), snapshot.Observations.Count(x => x.Ignored), discrepancies);
        Check(manual == evidence.ManualTimestamps && ignored == evidence.IgnoredImpulses,
            $"Run {run} manual/ignored expected {manual}/{ignored} actual {evidence.ManualTimestamps}/{evidence.IgnoredImpulses}");
        if (Forerunners.Count > 0)
        {
            Log($"Run {run} forerunners {string.Join(", ", snapshot.Forerunners.Select(x => $"{Forerunners.Single(r => r.Key == x.Bib).Letter} {ExpectedResults.FormatHundredths(x.Hundredths)}"))}; " +
                $"{snapshot.Audit.Count(x => x.After.Bib is { } b && TimingForerunner.IsKey(b))} audited forerunner decisions.");
        }
        Log($"Run {run} timed: {evidence.Starters} on list, {evidence.Finished} finished, {evidence.Dns} DNS, {evidence.Dnf} DNF, {evidence.Dsq} DSQ; " +
            $"{evidence.RawPackets} raw packets, {evidence.Observations} observations, {evidence.AuditEntries} audit entries, " +
            $"{evidence.ManualTimestamps} manual timestamp(s), {evidence.IgnoredImpulses} ignored impulse(s).");
        return evidence;
    }

    private static async Task PulseAsync(TimingWorkspace timing, SimulatorTimingSource source, int channel, long timeOfDay)
    {
        var count = timing.Snapshot!.Observations.Count;
        await source.PulseAsync(channel, timeOfDay);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (timing.Snapshot!.Observations.Count <= count) { await Task.Delay(2, timeout.Token); }
        // Synchronize through the same state gate as automatic assignment.
        await timing.FollowStartOrderAsync(true);
    }

    private void CompareRun(int run, TimingSnapshot actual, IReadOnlyList<ExpectedRunRow> expected)
    {
        var mismatches = 0;
        foreach (var row in expected)
        {
            var result = actual.Results.Single(x => x.Bib == row.Bib);
            var status = row.Outcome.ToString();
            var split = result.Splits.Count > 0 ? result.Splits[0].Hundredths : null;
            if (result.Status.ToString() != status || result.Hundredths != row.Hundredths
                || (row.Outcome == RunOutcome.Finished && split != row.IntermediateHundredths)
                || (row.Outcome == RunOutcome.Finished && result.Rank != row.Rank))
            {
                mismatches++;
                _discrepancies.Add($"Run {run} bib {row.Bib} {row.Code}: expected {status} {ExpectedResults.FormatHundredths(row.Hundredths)} split {ExpectedResults.FormatHundredths(row.IntermediateHundredths)} rank {row.Rank}; " +
                    $"actual {result.Status} {ExpectedResults.FormatHundredths(result.Hundredths)} split {ExpectedResults.FormatHundredths(split)} rank {result.Rank}");
            }
            if (row.Outcome == RunOutcome.DSQ && result.Disqualification?.Gate is null)
            { _discrepancies.Add($"Run {run} bib {row.Bib}: DSQ details missing"); }
        }
        var ties = expected.Where(x => x.Rank is not null).GroupBy(x => x.Rank).Count(x => x.Count() > 1);
        Log($"Run {run} compared {expected.Count} rows against independent expectations: {mismatches} mismatches; {ties} tied rank group(s).");
    }

    private void CompareFinal(IReadOnlyList<ExpectedFinalRow> expected, IReadOnlyList<FisResultRow> actual)
    {
        var mismatches = 0;
        var classifiedExpected = expected.Where(x => x.Total is not null).ToArray();
        var classifiedActual = actual.Where(x => x.TotalHundredths is not null).ToArray();
        if (!classifiedExpected.Select(x => x.Bib).SequenceEqual(classifiedActual.Select(x => x.Entry.Bib)))
        { _discrepancies.Add("Final listing order differs from ICR 617.3.3 expectation"); mismatches++; }
        foreach (var row in expected)
        {
            var result = actual.Single(x => x.Entry.Bib == row.Bib);
            if (result.Status.ToString() != row.Status || result.Run1Hundredths != row.Run1 || result.Run2Hundredths != row.Run2
                || result.TotalHundredths != row.Total || result.Rank != row.Rank)
            {
                mismatches++;
                _discrepancies.Add($"Final bib {row.Bib} {row.Code}: expected {row.Status} {row.Run1}/{row.Run2}/{row.Total} rank {row.Rank}; " +
                    $"actual {result.Status} {result.Run1Hundredths}/{result.Run2Hundredths}/{result.TotalHundredths} rank {result.Rank}");
            }
        }
        foreach (var (a, b) in race.CombinedTies)
        {
            var ra = actual.Single(x => x.Entry.Entrant.Athlete.FederationCode == a);
            var rb = actual.Single(x => x.Entry.Entrant.Athlete.FederationCode == b);
            if (ra.Rank is null || ra.Rank != rb.Rank) { _discrepancies.Add($"Combined tie {a}/{b} not ex aequo: {ra.Rank}/{rb.Rank}"); }
        }
        Log($"Final results compared {expected.Count} rows: {mismatches} mismatches; {classifiedActual.Length} classified; " +
            $"combined ties at ranks {string.Join(", ", race.CombinedTies.Select(t => actual.Single(x => x.Entry.Entrant.Athlete.FederationCode == t.A).Rank))}.");
    }

    private (int Ranked, int NotRanked) CheckXml(byte[] xml, IReadOnlyList<ExpectedFinalRow> expected)
    {
        var document = XDocument.Parse(Encoding.UTF8.GetString(xml));
        XElement[] Elements(string name) => document.Descendants().Where(x => x.Name.LocalName == name).ToArray();
        string Child(XElement e, string name) => e.Elements().Single(x => x.Name.LocalName == name).Value;
        var ranked = Elements("AL_ranked");
        var notRanked = Elements("AL_notranked");
        var classified = expected.Where(x => x.Total is not null).ToArray();
        Check(ranked.Length == classified.Length, $"XML ranked {ranked.Length}, expected {classified.Length}");
        Check(ranked.Select(x => (int.Parse(Child(x, "Bib"), CultureInfo.InvariantCulture), int.Parse(Child(x, "Rank"), CultureInfo.InvariantCulture)))
            .SequenceEqual(classified.Select(x => (x.Bib, x.Rank!.Value))), "XML ranked bibs/ranks differ from the expected official order");
        var expectedStatuses = expected.Where(x => x.Total is null)
            .Select(x => x.Status + (x.Run1 is null ? "1" : "2")).Order(StringComparer.Ordinal).ToArray();
        var actualStatuses = notRanked.Select(x => x.Attribute("Status")!.Value).Order(StringComparer.Ordinal).ToArray();
        Check(expectedStatuses.SequenceEqual(actualStatuses), "XML not-ranked statuses differ: expected " + string.Join(",", expectedStatuses)
            + " actual " + string.Join(",", actualStatuses));
        Check(notRanked.Where(x => x.Attribute("Status")!.Value.StartsWith("DSQ", StringComparison.Ordinal))
            .All(x => x.Elements().Any(e => e.Name.LocalName == "Gate")), "XML DSQ rows are missing the gate");
        Check(ranked.Length + notRanked.Length == SyntheticRace.AthleteCount, $"XML does not account for all {SyntheticRace.AthleteCount} competitors");
        return (ranked.Length, notRanked.Length);
    }

    // Every stored timing record of both runs: capture sessions, each raw packet with its original bytes and receive
    // time, and every audit record with operator, reason, time and before/after decisions.
    private static async Task<string> EvidenceAsync(SeriesWorkspace workspace, params Guid[] lists)
    {
        var text = new StringBuilder();
        foreach (var id in lists)
        {
            var data = await workspace.ReadTimingAsync(id);
            text.AppendLine(JsonSerializer.Serialize(data.Sessions));
            foreach (var packet in data.Packets)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"{packet.SessionId:N} {packet.Sequence} {packet.ReceivedAt:O} {packet.Protocol} {packet.Source} {packet.Stream} {Convert.ToHexString(packet.Bytes)}");
            }
            foreach (var audit in data.Audit) { text.AppendLine(JsonSerializer.Serialize(audit)); }
        }
        return text.ToString();
    }

    // A recorded move after the draw: the athlete takes the new position, everyone keeps their bib, the others keep
    // their relative order, and the reason is kept with the change.
    private StartListPlan MoveAfterDraw(StartListPlan drawn, string code, int toPosition)
    {
        var moved = drawn.Entries.Single(x => x.Entrant.Athlete.FederationCode == code);
        var plan = FisStartOrder.Apply(drawn, new(StartListChangeKind.Move, "Late at the start gate: moved by the jury", moved.Entrant.CompetitorId, toPosition));
        var others = drawn.Entries.Where(x => x != moved).Select(x => x.Entrant.CompetitorId);
        Check(plan.Entries.Single(x => x.Position == toPosition).Entrant.CompetitorId == moved.Entrant.CompetitorId
            && plan.Entries.Single(x => x.Entrant.CompetitorId == moved.Entrant.CompetitorId).Bib == moved.Bib
            && plan.Entries.Where(x => x.Position != toPosition).Select(x => x.Entrant.CompetitorId).SequenceEqual(others)
            && plan.Entries.Select(x => x.Position).SequenceEqual(Enumerable.Range(1, drawn.Entries.Count))
            && plan.Entries.Select(x => x.Bib).Order().SequenceEqual(drawn.Entries.Select(x => x.Bib).Order())
            && plan.Changes is [{ Kind: StartListChangeKind.Move, Reason.Length: > 0 }], "Start position move is wrong");
        Log($"Moved bib {moved.Bib} ({code}) from position {moved.Position} to {toPosition} with a reason; bibs unchanged.");
        return plan;
    }

    private static string ForerunnerFingerprint(TimingSnapshot first, TimingSnapshot second) => string.Join("\n",
        new[] { first, second }.SelectMany(s => s.Forerunners.Select(x => $"{s.ListId:N} {x.Bib} {x.Status} {x.Hundredths} {x.Rank}")));

    private static string Fingerprint(TimingSnapshot first, TimingSnapshot second) => string.Join("\n",
        new[] { first, second }.SelectMany(s => s.Results.Select(x => $"{s.ListId:N} {x.Bib} {x.Status} {x.Hundredths} {x.Rank} {string.Join('/', x.Splits.Select(y => y.Hundredths))} {x.Disqualification}"))
            .Concat(new[] { first, second }.SelectMany(s => s.Audit.Select(x => $"{x.Id} {x.Operator} {x.Reason} {x.After.Kind} {x.ReversesId}"))));

    private static DateTimeOffset At(int hour, int minute) => new(SyntheticRace.RaceDate.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.Zero);

    private void Check(bool condition, string message)
    {
        if (!condition) { _discrepancies.Add(message); }
    }

    private void Log(string message) => _log.Add(message);
}

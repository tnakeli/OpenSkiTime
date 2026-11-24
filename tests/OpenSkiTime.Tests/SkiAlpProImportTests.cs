using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Test import of Ski-Alp-Pro events: reading the files, building the import, and replacing a competition in a series file.
public sealed class SkiAlpProImportTests : IDisposable
{
    private static readonly DateTimeOffset s_at = new(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(2));
    private static readonly AlgeDecoderFactory s_decoders = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-ski-alp-pro", Guid.NewGuid().ToString("N"));

    public SkiAlpProImportTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<SkiAlpProRace> ReadAsync(SkiAlpProTestEvent source, string name = "Event901", bool times = true)
        => SkiAlpProFileReader.ReadAsync(source.Write(Path.Combine(_root, "scdb-" + Guid.NewGuid().ToString("N")), name, times));

    private async Task<(SeriesWorkspace Workspace, string Path)> SeriesAsync()
    {
        var path = Path.Combine(_root, "series.ost");
        var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var date = new DateOnly(2026, 3, 21);
        await workspace.CreateAsync(path, new("Synthetic", "Test slope", "Test club", date, date, "FIN", "2025/26"));
        return (workspace, path);
    }

    private static async Task<SkiAlpProCommit> BuildAsync(SeriesWorkspace workspace, SkiAlpProRace race, SkiAlpProScope scope, Guid? target = null, string? codex = null)
        => SkiAlpProImport.Build(new(race, scope, await workspace.ReadAsync(), await workspace.ReadCompetitorDeskAsync(), target, codex ?? race.Codex, [], "Tester", s_at), s_decoders, Guid.NewGuid);

    private static async Task<SkiAlpProImportResult> ImportAsync(SeriesWorkspace workspace, SkiAlpProRace race, SkiAlpProScope scope, Guid? target = null)
        => await workspace.ApplySkiAlpProImportAsync(await BuildAsync(workspace, race, scope, target));

    private static async Task<TimingSnapshot> TimingAsync(SeriesWorkspace workspace, Guid competitionId, int run)
    {
        var list = (await workspace.ReadStartListsAsync(competitionId)).Revisions.Single(x => x.Plan.RunNumber == run);
        return TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), s_decoders);
    }

    private static string[] Schema(string path)
    {
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT type || name || coalesce(sql, '') FROM sqlite_master ORDER BY type, name";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read()) { rows.Add(reader.GetString(0)); }
        return rows.ToArray();
    }

    [Fact]
    public async Task ReadsTheEventItsStatisticsAndWindowsTextWithoutChangingTheFiles()
    {
        var folder = Path.Combine(_root, "source");
        var path = SkiAlpProTestEvent.TwoRuns().Write(folder);
        var before = Directory.GetFiles(folder).Order().Select(x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))).ToArray();
        var race = await SkiAlpProFileReader.ReadAsync(path);

        Assert.Equal("0592", race.Codex);
        Assert.Equal(new DateOnly(2026, 3, 21), race.Date);
        Assert.Equal(Discipline.Slalom, race.Discipline);
        Assert.Equal("FIS", race.Category);
        Assert.Equal("718", race.TechnicalDelegate!.Number);
        Assert.Equal(2, race.RunCount);
        Assert.Equal("JÄRVI", race.Competitors.Single(x => x.Bib == 2).Surname);
        Assert.Equal(41.00m, race.Competitors.Single(x => x.Bib == 1).Sl);
        Assert.Null(race.Competitors[0].Dh);
        Assert.Equal(new SkiAlpProRunStatistics(1, 6, 4, 1, 1, 0, 0, 0, 15), SkiAlpProRunStatistics.Of(race.Run(1)));
        Assert.Equal(new SkiAlpProRunStatistics(2, 4, 3, 0, 0, 1, 0, 0, 12), SkiAlpProRunStatistics.Of(race.Run(2)));
        Assert.Equal(Enum.GetValues<SkiAlpProScope>(), SkiAlpProImport.AvailableScopes(race));
        Assert.Empty(race.Notices);
        Assert.Equal(before, Directory.GetFiles(folder).Order().Select(x => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))).ToArray());
        // Choosing the times file opens the same event.
        Assert.Equal(race.FileHash, (await SkiAlpProFileReader.ReadAsync(Path.Combine(folder, "Event901Ex.scdb"))).FileHash);
    }

    [Fact]
    public async Task AFileWithoutTimesHasZeroStatisticsAndOffersTheBibOrder()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns(), times: false);
        Assert.Equal(new SkiAlpProRunStatistics(1, 6, 0, 0, 0, 0, 0, 6, 0), SkiAlpProRunStatistics.Of(race.Run(1)));
        Assert.Equal(new SkiAlpProRunStatistics(2, 0, 0, 0, 0, 0, 0, 0, 0), SkiAlpProRunStatistics.Of(race.Run(2)));
        Assert.Equal([SkiAlpProScope.Competitors, SkiAlpProScope.Run1Starters], SkiAlpProImport.AvailableScopes(race));
        Assert.Equal(2, race.Notices.Count);
        Assert.Contains("Event901Ex.scdb", race.Notices[0], StringComparison.Ordinal);

        var empty = new SkiAlpProTestEvent();
        var none = await ReadAsync(empty);
        Assert.Empty(SkiAlpProImport.AvailableScopes(none));
        Assert.Equal(new SkiAlpProRunStatistics(1, 0, 0, 0, 0, 0, 0, 0, 0), SkiAlpProRunStatistics.Of(none.Run(1)));
        Assert.All(none.Runs, x => Assert.Empty(x.Records));
    }

    [Fact]
    public async Task OtherFilesAreRefused()
    {
        var text = Path.Combine(_root, "text.scdb");
        await File.WriteAllTextAsync(text, "not a database");
        await Assert.ThrowsAsync<SeriesFileException>(() => SkiAlpProFileReader.ReadAsync(text));
        await Assert.ThrowsAsync<SeriesFileException>(() => SkiAlpProFileReader.ReadAsync(Path.Combine(_root, "missing.scdb")));
        var (workspace, path) = await SeriesAsync();
        await workspace.DisposeAsync();
        await Assert.ThrowsAsync<SeriesFileException>(() => SkiAlpProFileReader.ReadAsync(path));
        var other = new SkiAlpProTestEvent { Parameters = { ["SOTWARE"] = "MSPORTS PRO" } };
        await Assert.ThrowsAsync<SeriesFileException>(() => ReadAsync(other));
        var orphan = SkiAlpProTestEvent.TwoRuns().Write(Path.Combine(_root, "orphan"));
        File.Delete(orphan);
        await Assert.ThrowsAsync<SeriesFileException>(() => SkiAlpProFileReader.ReadAsync(Path.Combine(_root, "orphan", "Event901Ex.scdb")));
    }

    [Fact]
    public async Task CreatesTheCompetitionWithTheFilesOrderBibsTimesAndStatuses()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns());
        var (workspace, path) = await SeriesAsync();
        await using var owned = workspace;
        Assert.Null(SkiAlpProImport.Target(race, (await workspace.ReadAsync()).Competitions));
        var result = await ImportAsync(workspace, race, SkiAlpProScope.Run2Times);

        Assert.Null(result.BackupPath);
        var competition = Assert.Single(result.Series.Competitions);
        Assert.Equal(("0592", RaceType.Fis, Discipline.Slalom, 2, 1, "SL 0592"), (competition.Values.FisCode, competition.Values.RaceType,
            competition.Values.Discipline, competition.Values.RunCount, competition.Values.IntermediateCount, competition.Values.ShortLabel));
        Assert.Equal("M", competition.Values.Calendar!.Gender);
        Assert.Equal(competition, SkiAlpProImport.Target(race, result.Series.Competitions));
        var desk = await workspace.ReadCompetitorDeskAsync();
        Assert.Equal(6, desk.Participations.Count(x => x.Participates && x.CompetitionId == competition.Id));
        Assert.Equal("1926", desk.FisPointsOf(desk.Competitors.Single(x => x.Values.FederationCode == "180001").Id)!.ListCode);

        var lists = (await workspace.ReadStartListsAsync(competition.Id)).Revisions;
        Assert.Equal([3, 1, 2, 4, 5, 6], lists.Single(x => x.Plan.RunNumber == 1).Plan.Entries.Select(x => x.Bib));
        Assert.Equal([3, 1, 2, 4], lists.Single(x => x.Plan.RunNumber == 2).Plan.Entries.Select(x => x.Bib));
        Assert.All(lists, x => Assert.Equal(StartListChangeKind.ImportOrder, Assert.Single(x.Plan.Changes!).Kind));

        var run1 = await TimingAsync(workspace, competition.Id, 1);
        Assert.True(run1.Complete);
        Assert.Equal(0, run1.Unresolved);
        // Truncated to hundredths from the impulses themselves, not from the stored net time.
        Assert.Equal([5023, 4940, 4880, 4803], run1.Results.Where(x => x.Status == TimingStatus.Finished).Select(x => x.Hundredths!.Value));
        Assert.All(run1.Results.Where(x => x.Status == TimingStatus.Finished), x => Assert.Equal(3000, Assert.Single(x.Splits).Hundredths));
        Assert.DoesNotContain(run1.Audit, x => x.After.Kind == DecisionKind.Time);
        Assert.Equal(TimingStatus.DNF, run1.Results.Single(x => x.Bib == 5).Status);
        Assert.Equal(TimingStatus.DNS, run1.Results.Single(x => x.Bib == 6).Status);
        // The full source precision is kept on the integer scale; the forerunner and the stray impulse are kept and ignored.
        var start = run1.Observations.Single(x => x.Bib == 3 && x.Observation.Channel == 0).Observation;
        Assert.Equal((36_000_000_000 * 10 + new DateTime(2026, 3, 21).Ticks, 4), (start.DeviceTicks, start.Precision));
        Assert.Equal(5, run1.Observations.First(x => x.Bib is not null && x.Observation.Channel == 2).Observation.Precision);
        Assert.Equal(3, run1.Observations.Count(x => x.State == "Ignored"));
        var run2 = await TimingAsync(workspace, competition.Id, 2);
        Assert.Equal((TimingStatus.DSQ, 12), (run2.Results.Single(x => x.Bib == 4).Status, run2.Results.Single(x => x.Bib == 4).Disqualification!.Gate));
        Assert.Equal([4898, 4840, 4823], run2.Results.Where(x => x.Status == TimingStatus.Finished).Select(x => x.Hundredths!.Value));

        // Imported timing is simulation timing: never official, never mixed with devices.
        var data = await workspace.ReadTimingAsync(lists[0].Id);
        Assert.True(Assert.Single(data.Sessions).Options.Simulation);
        Assert.Contains("SHA256 " + race.TimesFileHash, data.Audit[0].Reason, StringComparison.Ordinal);
        // Every row of the file is in the journal, counted or not.
        Assert.Equal(race.Run(1).Records.Count + race.Run(1).Results.Count, data.Packets.Count);

        // The file reopens, and its results assemble like a captured race.
        await workspace.CloseAsync();
        await workspace.OpenAsync(path);
        var first = await workspace.ReadTimingAsync(lists[0].Id);
        var second = await workspace.ReadTimingAsync(lists[1].Id);
        var results = FisRaceResults.Assemble(first.List, TimingReplay.Restore(first, s_decoders), second.List, TimingReplay.Restore(second, s_decoders));
        Assert.Equal([9703, 9780, 9921], results.Rows.Where(x => x.Rank is not null).OrderBy(x => x.Rank).Select(x => x.TotalHundredths!.Value));
    }

    [Fact]
    public async Task StartersCanBeImportedBeforeTheRaceAndTimedWithRealDevices()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns());
        var (workspace, _) = await SeriesAsync();
        await using var owned = workspace;
        var result = await ImportAsync(workspace, race, SkiAlpProScope.Run1Starters);
        var list = Assert.Single((await workspace.ReadStartListsAsync(result.Series.Competitions[0].Id)).Revisions);
        Assert.Null(list.StartedAt);
        Assert.False(list.HasCapture);
        // The same rules that protect a drawn list accept it: it can be marked started.
        var started = await workspace.MarkRunStartedAsync(list.Id, result.Series.Revision, "Tester", s_at);
        Assert.NotNull(started.Revisions[0].StartedAt);
    }

    [Fact]
    public async Task AnImportReplacesTheCompetitionAfterSavingACopyOfTheSeries()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns());
        var (workspace, path) = await SeriesAsync();
        await using var owned = workspace;
        var first = await ImportAsync(workspace, race, SkiAlpProScope.Run2Times);
        var target = first.Series.Competitions[0].Id;
        var schema = Schema(path);
        var replaced = await workspace.ReadSkiAlpProReplacementAsync(target);
        Assert.Equal((6, 2, 2, 0, true), (replaced.Entries, replaced.StartLists, replaced.TimingSessions, replaced.DeviceSessions, replaced.Any));

        // The next draw of the same event: one competitor less, another order, no times yet.
        var redrawn = new SkiAlpProTestEvent();
        redrawn.Athletes.AddRange(SkiAlpProTestEvent.TwoRuns().Athletes.Take(5));
        redrawn.Athletes[0] = redrawn.Athletes[0] with { Bib = 88 };
        redrawn.Starts[1] = [2, 88, 3, 4, 5];
        var second = await ImportAsync(workspace, await ReadAsync(redrawn), SkiAlpProScope.Run1Starters, target);

        Assert.Equal(target, Assert.Single(second.Series.Competitions).Id);
        var list = Assert.Single((await workspace.ReadStartListsAsync(target)).Revisions);
        Assert.Equal([2, 88, 3, 4, 5], list.Plan.Entries.Select(x => x.Bib));
        var desk = await workspace.ReadCompetitorDeskAsync();
        Assert.Equal(6, desk.Competitors.Count);
        Assert.Equal(5, desk.Participations.Count(x => x.CompetitionId == target && x.Participates));
        Assert.Empty((await workspace.ReadTimingAsync(list.Id)).Packets);
        // History protection is exactly as before, and still in force.
        Assert.Equal(schema, Schema(path));
        await ImportAsync(workspace, race, SkiAlpProScope.Run1Times, target);
        await workspace.CloseAsync();
        await using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await db.OpenAsync();
            await using var delete = db.CreateCommand();
            delete.CommandText = "DELETE FROM RawTimingPackets";
            await Assert.ThrowsAsync<SqliteException>(() => delete.ExecuteNonQueryAsync());
        }

        // The copy holds the replaced race completely.
        Assert.StartsWith(Path.Combine(_root, "series.before-import-"), second.BackupPath, StringComparison.OrdinalIgnoreCase);
        await workspace.OpenAsync(second.BackupPath!);
        Assert.Equal(4, (await TimingAsync(workspace, target, 1)).Results.Count(x => x.Status == TimingStatus.Finished));
        Assert.Equal(2, (await workspace.ReadStartListsAsync(target)).Revisions.Count);
    }

    [Fact]
    public async Task AFailedImportChangesNothingAndKeepsNoCopy()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns());
        var (workspace, path) = await SeriesAsync();
        await using var owned = workspace;
        var target = (await ImportAsync(workspace, race, SkiAlpProScope.Run2Times)).Series.Competitions[0].Id;
        var stale = await BuildAsync(workspace, race, SkiAlpProScope.Run1Starters, target);
        var schema = Schema(path);

        // A plan that its recorded draw and changes do not reproduce is refused after the old data was already removed.
        var run = stale.Runs[0];
        var tampered = stale with { Runs = [run with { Plan = run.Plan with { Entries = run.Plan.Entries.Reverse().ToArray() } }] };
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApplySkiAlpProImportAsync(tampered));
        await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.ApplySkiAlpProImportAsync(stale with { ExpectedRevision = stale.ExpectedRevision - 1 }));
        Assert.Equal(schema, Schema(path));
        Assert.Equal(2, (await workspace.ReadStartListsAsync(target)).Revisions.Count);
        Assert.Equal(4, (await TimingAsync(workspace, target, 1)).Results.Count(x => x.Status == TimingStatus.Finished));
        Assert.Empty(Directory.GetFiles(_root, "*before-import*"));
    }

    [Fact]
    public async Task TimingCaptureInProgressBlocksTheImport()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns());
        var (workspace, path) = await SeriesAsync();
        var target = (await ImportAsync(workspace, race, SkiAlpProScope.Run1Starters)).Series.Competitions[0].Id;
        await workspace.DisposeAsync();
        await using var session = await new SqliteSeriesFileStore().OpenAsync(path);
        var list = (await session.ReadStartListsAsync(target)).Revisions.Single();
        var timing = (ITimingStore)session;
        var capture = await timing.BeginCaptureAsync(list.Id, new("Synthetic", "COM1", new(2026, 3, 21)), "Tester", s_at);
        var commit = SkiAlpProImport.Build(new(race, SkiAlpProScope.Run1Times, await session.ReadAsync(), await session.ReadCompetitorDeskAsync(), target, "", [], "Tester", s_at),
            s_decoders, Guid.NewGuid);
        await Assert.ThrowsAsync<SeriesFileException>(() => ((ISkiAlpProImportStore)session).ApplySkiAlpProImportAsync(commit));
        await timing.EndCaptureAsync(capture.Id, s_at);
        // Device timing is named before it is replaced.
        Assert.Equal(1, (await ((ISkiAlpProImportStore)session).ReadSkiAlpProReplacementAsync(target)).DeviceSessions);
        Assert.NotNull((await ((ISkiAlpProImportStore)session).ApplySkiAlpProImportAsync(commit)).BackupPath);
    }

    [Fact]
    public async Task OtherCompetitionsKeepTheirDataAndSavedDetailsSurviveBlankCells()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns());
        var (workspace, _) = await SeriesAsync();
        await using var owned = workspace;
        var women = SkiAlpProTestEvent.TwoRuns();
        women.Parameters["CODEX"] = "FIN5573.718";
        women.Athletes.Clear();
        for (var i = 1; i <= 6; i++) { women.Athletes.Add(new(i, "NAINEN" + (char)('A' + i), "First", "19000" + i, Sex: 2)); }
        var other = (await ImportAsync(workspace, await ReadAsync(women), SkiAlpProScope.Run2Times)).Series.Competitions.Single().Id;
        var target = (await ImportAsync(workspace, race, SkiAlpProScope.Run2Times)).Series.Competitions.Single(x => x.Id != other).Id;

        var blank = SkiAlpProTestEvent.TwoRuns();
        blank.Athletes[0] = blank.Athletes[0] with { FirstName = "" };
        await ImportAsync(workspace, await ReadAsync(blank), SkiAlpProScope.Run1Times, target);
        var desk = await workspace.ReadCompetitorDeskAsync();
        Assert.Equal("FirstB", desk.Competitors.Single(x => x.Values.FederationCode == "180001").Values.FirstName);
        Assert.Equal(2, (await workspace.ReadStartListsAsync(other)).Revisions.Count);
        Assert.Equal(4, (await TimingAsync(workspace, other, 1)).Results.Count(x => x.Status == TimingStatus.Finished));
        // Women cannot be imported over the men's competition.
        var wrong = race with { Competitors = race.Competitors.Select(x => x with { Gender = Gender.Female, Code = "9" + x.Code, Surname = "X" + x.Surname }).ToArray() };
        await Assert.ThrowsAsync<DomainValidationException>(() => ImportAsync(workspace, wrong, SkiAlpProScope.Run1Starters, target));
        Assert.Single((await workspace.ReadStartListsAsync(target)).Revisions);
    }

    [Fact]
    public async Task ACorrectedTimeAndMissingImpulsesUseTheFilesOwnResult()
    {
        var source = SkiAlpProTestEvent.TwoRuns();
        source.Results[1][0] = (3, 0, 50_500, 0); // corrected in Ski-Alp-Pro
        source.Records[1].RemoveAll(x => x.Bib == 1); // no impulses at all
        source.Results[1][5] = (6, 9, 0, 0); // a status this version does not know
        var race = await ReadAsync(source);
        var (workspace, _) = await SeriesAsync();
        await using var owned = workspace;
        var commit = await BuildAsync(workspace, race, SkiAlpProScope.Run1Times);
        Assert.Contains(commit.Notices, x => x.Contains("bib 6", StringComparison.Ordinal));
        var target = (await workspace.ApplySkiAlpProImportAsync(commit)).Series.Competitions[0].Id;
        var run1 = await TimingAsync(workspace, target, 1);
        Assert.Equal(5050, run1.Results.Single(x => x.Bib == 3).Hundredths);
        Assert.Equal(4940, run1.Results.Single(x => x.Bib == 1).Hundredths);
        Assert.Equal(TimingStatus.Ready, run1.Results.Single(x => x.Bib == 6).Status);
        Assert.Equal(2, run1.Audit.Count(x => x.After.Kind == DecisionKind.Time));
        // An open Run 1 result means there is no Run 2 to import.
        await Assert.ThrowsAsync<DomainValidationException>(() => BuildAsync(workspace, race, SkiAlpProScope.Run2Starters, target));
    }

    [Fact]
    public async Task RunTwoKeepsAFinisherWhoIsNotOnTheListAndRefusesAnExtraStarter()
    {
        var source = SkiAlpProTestEvent.TwoRuns();
        source.Starts[2] = [4, 2, 3];
        var (workspace, _) = await SeriesAsync();
        await using var owned = workspace;
        var commit = await BuildAsync(workspace, await ReadAsync(source), SkiAlpProScope.Run2Starters);
        Assert.Equal([4, 2, 3, 1], commit.Runs[1].Plan.Entries.Select(x => x.Bib));
        Assert.Contains(commit.Notices, x => x.StartsWith("Bib 1 finished Run 1", StringComparison.Ordinal));
        source.Starts[2] = [4, 2, 3, 1, 5];
        var extra = await ReadAsync(source, "Event902");
        var error = await Assert.ThrowsAsync<DomainValidationException>(() => BuildAsync(workspace, extra, SkiAlpProScope.Run2Starters));
        Assert.Contains("bib 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMixedLocalRaceBecomesAClubCompetition()
    {
        var source = SkiAlpProTestEvent.TwoRuns();
        source.Parameters["CODEX"] = "";
        source.Parameters["IMPORT_LIST_NUMBER"] = "0";
        source.Athletes[0] = source.Athletes[0] with { Sex = 2 };
        var (workspace, _) = await SeriesAsync();
        await using var owned = workspace;
        var result = await ImportAsync(workspace, await ReadAsync(source), SkiAlpProScope.Run2Times);
        var competition = Assert.Single(result.Series.Competitions);
        Assert.Equal((RaceType.Club, null, "SL"), (competition.Values.RaceType, competition.Values.FisCode, competition.Values.ShortLabel));
        Assert.Equal([3, 1, 2, 4], (await workspace.ReadStartListsAsync(competition.Id)).Revisions.Single(x => x.Plan.RunNumber == 2).Plan.Entries.Select(x => x.Bib));
        Assert.Empty((await workspace.ReadCompetitorDeskAsync()).FisPoints!);
    }

    [Fact]
    public async Task TheSeriesCompetitionWithTheCodexIsTheTargetAndCannotBeDuplicated()
    {
        var race = await ReadAsync(SkiAlpProTestEvent.TwoRuns());
        var (workspace, _) = await SeriesAsync();
        await using var owned = workspace;
        var series = await workspace.ReadAsync();
        series = await workspace.SaveCompetitionAsync(null, new("Planned slalom", "SL1", new(2026, 3, 22), Discipline.Slalom, RaceType.Fis, 2, 0, "592"), series.Revision);
        var planned = series.Competitions[0];
        Assert.Equal(planned, SkiAlpProImport.Target(race, series.Competitions));
        await Assert.ThrowsAsync<DomainValidationException>(() => BuildAsync(workspace, race, SkiAlpProScope.Run1Starters));
        var other = await BuildAsync(workspace, race, SkiAlpProScope.Run1Starters, codex: "9999");
        Assert.Equal(("9999", true), (other.Competition.FisCode, other.IsNewCompetition));
        // The competition keeps its own settings; only the intermediates the times need are added.
        var result = await ImportAsync(workspace, race, SkiAlpProScope.Run1Times, planned.Id);
        Assert.Equal(planned.Values with { IntermediateCount = 1 }, Assert.Single(result.Series.Competitions).Values);
    }

    [Fact]
    public async Task AnUnknownPointsListIsValidOnTheImportedRaceDatesAndAKnownListIsPreferred()
    {
        var source = SkiAlpProTestEvent.TwoRuns();
        var (workspace, _) = await SeriesAsync();
        await using var owned = workspace;
        await ImportAsync(workspace, await ReadAsync(source), SkiAlpProScope.Run1Starters);
        source.Parameters["CODEX"] = "FIN0593.718";
        source.Parameters["DATE"] = "46103";
        var next = await ReadAsync(source, "Event902");
        var commit = await BuildAsync(workspace, next, SkiAlpProScope.Run1Starters);
        Assert.Equal((new DateOnly(2026, 3, 21), new DateOnly(2026, 3, 22)), (commit.PointsList!.ValidFrom, commit.PointsList.ValidTo));
        Assert.True(commit.Runs[0].Plan.PointsList.IsEffectiveOn(next.Date));
        var known = new FisPointsListInfo("1926", "Published list", new(2026, 3, 1), new(2026, 3, 31));
        var request = new SkiAlpProImportRequest(next, SkiAlpProScope.Run1Starters, await workspace.ReadAsync(), await workspace.ReadCompetitorDeskAsync(), null, next.Codex, [known], "Tester", s_at);
        Assert.Equal(known, SkiAlpProImport.Build(request, s_decoders, Guid.NewGuid).PointsList);
    }

    [Fact]
    public void AnImportedOrderListsEveryStarterOnceWithUniqueBibs()
    {
        var race = new CompetitionValues("Race", "SL", new(2026, 3, 21), Discipline.Slalom, RaceType.Fis, 2, 0, "0592");
        var entrants = Enumerable.Range(1, 4).Select(i => new DrawEntrant(Guid.NewGuid(), new("TEST" + i, "First", 2005, "18000" + i, "FIN", null, Gender.Male), i)).ToArray();
        var plan = FisStartOrder.FirstRun(Guid.NewGuid(), race, Gender.Male, entrants, new("1", race.Date, race.Date), new(), "seed");
        var order = entrants.Reverse().Select((x, i) => new ImportedStart(x.CompetitorId, 40 - i)).ToArray();
        var imported = FisStartOrder.Apply(plan, new(StartListChangeKind.ImportOrder, "Imported", Order: order));
        Assert.Equal([40, 39, 38, 37], imported.Entries.Select(x => x.Bib));
        Assert.Equal([1, 2, 3, 4], imported.Entries.Select(x => x.Position));
        Assert.Equal(imported.Entries, FisStartOrder.WithChanges(plan, imported.Changes).Entries);

        void Refused(StartListChange change) => Assert.Throws<DomainValidationException>(() => FisStartOrder.Apply(plan, change));
        Refused(new(StartListChangeKind.ImportOrder, "Missing"));
        Refused(new(StartListChangeKind.ImportOrder, "Short", Order: order[1..]));
        Refused(new(StartListChangeKind.ImportOrder, "Twice", Order: [.. order[1..], order[1]]));
        Refused(new(StartListChangeKind.ImportOrder, "Bib twice", Order: order.Select(x => x with { Bib = 1 }).ToArray()));
        Refused(new(StartListChangeKind.ImportOrder, "Bib", Order: order.Select((x, i) => x with { Bib = i }).ToArray()));
        Refused(new(StartListChangeKind.Move, "Both", entrants[0].CompetitorId, 1, Order: order));
        // Later runs keep the bibs.
        var first = new StartListRevision(Guid.NewGuid(), 1, s_at, null, "T", "R", imported);
        var second = FisStartOrder.SecondRun(first, imported.Entries.Select((x, i) => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 5000 + i)).ToArray());
        Assert.Throws<DomainValidationException>(() => FisStartOrder.Apply(second, new(StartListChangeKind.ImportOrder, "Bibs", Order: order.Select((x, i) => x with { Bib = i + 1 }).ToArray())));
        // Plans without an imported order serialize as before.
        Assert.DoesNotContain("Order", System.Text.Json.JsonSerializer.Serialize(new StartListChange(StartListChangeKind.Move, "x", Guid.NewGuid(), 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDecoderKeepsPrecisionAndNeverTurnsUnreadableRowsIntoImpulses()
    {
        var session = new CaptureSession(Guid.NewGuid(), Guid.NewGuid(), new("Ski-Alp-Pro import", "x", new(2026, 3, 21), Simulation: true), s_at, s_at, true);
        var decoder = s_decoders.Create(session, SkiAlpProPacket.Protocol, "x", "HEAT1");
        TimingObservation One(SkiAlpProPacket row) => Assert.Single(decoder.Feed(new(session.Id, 1, s_at, SkiAlpProPacket.Protocol, "x", "HEAT1", row.ToBytes())));
        SkiAlpProPacket Row(string time, long status = 0, int? position = 1) => new("T", 1, 7, status, position, "0", time, 4);
        Assert.Equal((ObservationKind.Impulse, 4), (One(Row("38938988700")).Kind, One(Row("38938988700")).Precision));
        Assert.Equal(6, One(Row("38938988701")).Precision);
        Assert.Equal(ObservationKind.Information, One(Row("38938988700", status: 131584)).Kind);
        Assert.Equal(ObservationKind.Information, One(Row("", position: null)).Kind);
        foreach (var bad in new[] { "", "0", "-5", "86400000000", "12:00", "99999999999999999999" }) { Assert.Equal(ObservationKind.Invalid, One(Row(bad)).Kind); }
        Assert.Equal(ObservationKind.Invalid, One(Row("38938988700", position: 22)).Kind);
        Assert.Equal(ObservationKind.Invalid, Assert.Single(decoder.Feed(new(session.Id, 1, s_at, SkiAlpProPacket.Protocol, "x", "HEAT1", [1, 2]))).Kind);
        Assert.Equal(ObservationKind.Invalid, Assert.Single(decoder.Feed(new(session.Id, 1, s_at, SkiAlpProPacket.Protocol, "x", "HEAT1", "null"u8.ToArray()))).Kind);
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class StartListTests
{
    private static readonly CompetitionValues s_race = new("Synthetic Slalom", "SL1", new(2026, 9, 27), Discipline.Slalom, RaceType.Fis, 2, 0, "1234");
    private static readonly PointsListSource s_points = new("TEST", new(2026, 9, 1), new(2026, 10, 1));
    private static readonly DateTimeOffset s_at = new(2026, 9, 26, 18, 0, 0, TimeSpan.Zero);

    private static DrawEntrant[] Entrants(int count) => Enumerable.Range(1, count).Select(i => new DrawEntrant(
        new Guid(i, 0, 0, new byte[8]), new($"TEST{i}", "Athlete", 2000, $"{100000 + i}", "FIN", "Synthetic", Gender.Female), i)).ToArray();
    private static StartListPlan Draw(DrawEntrant[] entrants, DrawOptions? options = null)
        => FisStartOrder.FirstRun(Guid.Parse("10000000-0000-0000-0000-000000000001"), s_race, Gender.Female, entrants, s_points, options ?? new(), "fixed-test-seed");
    private static StartListRevision Approved(StartListPlan plan) => new(Guid.NewGuid(), 1, s_at, s_at, "Test operator", "Fixture", plan);

    [Fact]
    public void DrawReplaysRegardlessOfInputOrderAndExpandsTiedFirstGroup()
    {
        var entrants = Entrants(22);
        entrants[15] = entrants[15] with { Points = 15 };
        entrants[20] = entrants[20] with { Points = null };
        entrants[21] = entrants[21] with { Points = null };
        var plan = Draw(entrants);
        Assert.Equal(plan.Entries, Draw(entrants.Reverse().ToArray()).Entries);
        Assert.Equal(16, plan.Entries.Count(x => x.Group == "First group"));
        Assert.All(plan.Entries.Take(16), x => Assert.True(x.Entrant.Points <= 15));
        Assert.Equal(new decimal?[] { 17, 18, 19, 20 }, plan.Entries.Skip(16).Take(4).Select(x => x.Entrant.Points));
        Assert.All(plan.Entries.TakeLast(2), x => Assert.Null(x.Entrant.Points));
        Assert.Equal(Enumerable.Range(1, 22), plan.Entries.Select(x => x.Bib));
        Assert.Equal(22, plan.Entries.Select(x => x.Entrant.CompetitorId).Distinct().Count());
    }

    [Fact]
    public void SecondRunIncludesBoundaryTiesAndKeepsBibsAndResultOrderAfterReversal()
    {
        var first = Approved(Draw(Entrants(36)));
        var results = first.Plan.Entries.Select((x, i) => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished,
            i is 30 ? 6029 : i is 33 ? 6032 : 6000 + i)).ToArray();
        results[35] = results[35] with { Status = FinishStatus.DNF, Hundredths = null };
        var second = FisStartOrder.SecondRun(first, results);
        Assert.Equal(31, second.Entries.Count(x => x.Group == "Reversed group"));
        Assert.Equal([30, 31, 29], second.Entries.Take(3).Select(x => x.Bib));
        Assert.Equal([32, 34, 33, 35], second.Entries.Skip(31).Select(x => x.Bib));
        Assert.DoesNotContain(second.Entries, x => x.Bib == 36);
        Assert.All(second.Entries, x => Assert.Equal(first.Plan.Entries.Single(y => y.Entrant.CompetitorId == x.Entrant.CompetitorId).Bib, x.Bib));
        Assert.Equal(Enumerable.Range(1, 35), second.Entries.Select(x => x.Position));
        Assert.Equal(first.Id, second.SourceListId);
    }

    [Fact]
    public void FifteenReversalAndSmallFieldsExcludeAllNonFinishers()
    {
        var first = Approved(Draw(Entrants(19), new(15, 15, 101)));
        var results = first.Plan.Entries.Select((x, i) => new RunFinish(x.Entrant.CompetitorId,
            i >= 15 ? new[] { FinishStatus.DNS, FinishStatus.DNF, FinishStatus.DSQ, FinishStatus.NPS }[i - 15] : FinishStatus.Finished,
            i >= 15 ? null : 4000 + i)).ToArray();
        var second = FisStartOrder.SecondRun(first, results);
        Assert.Equal(Enumerable.Range(101, 15).Reverse(), second.Entries.Select(x => x.Bib));
        Assert.Throws<DomainValidationException>(() => FisStartOrder.SecondRun(first, results.Skip(1).ToArray()));
        Assert.Throws<DomainValidationException>(() => FisStartOrder.SecondRun(first with { ApprovedAt = null }, results));
    }

    [Fact]
    public void DrawRejectsUnsupportedFormatsStalePointsAndIncompleteAthletes()
    {
        Assert.Throws<DomainValidationException>(() => FisStartOrder.FirstRun(Guid.NewGuid(), s_race with { RunCount = 3 }, Gender.Female, Entrants(2), s_points, new(), "seed"));
        Assert.Throws<DomainValidationException>(() => FisStartOrder.FirstRun(Guid.NewGuid(), s_race, Gender.Female, Entrants(2), s_points with { ValidTo = new(2026, 9, 20) }, new(), "seed"));
        var entrants = Entrants(2);
        entrants[0] = entrants[0] with { Athlete = entrants[0].Athlete with { Surname = "" } };
        Assert.Throws<DomainValidationException>(() => Draw(entrants));
    }

    [Fact]
    public void ExternalResultInputIsStrictAndExportsEscapeUserText()
    {
        var entries = Entrants(3);
        entries[0] = entries[0] with { Athlete = entries[0].Athlete with { Surname = "<script> & \"TEST\"" } };
        var first = Approved(Draw(entries));
        Assert.Throws<DomainValidationException>(() => RunResultInput.ParseTime("79228162514264337593543950335"));
        Assert.Throws<DomainValidationException>(() => Draw(entries, new(FirstBib: int.MaxValue)));
        var parsed = RunResultInput.ParseTsv("Bib\tTime\tStatus\n1\t1:02.34\tFinished\n2\t\tDNF\n3\tDNS", first);
        Assert.Equal(6234, parsed[0].Hundredths);
        Assert.Throws<DomainValidationException>(() => RunResultInput.ParseTsv("1\t62.345\n2\tDNS\n3\tDNF", first));
        Assert.Throws<DomainValidationException>(() => RunResultInput.ParseTsv("1\t62.34\n1\t63.45\n3\tDNF", first));
        Assert.Throws<DomainValidationException>(() => RunResultInput.ParseTsv("1\t62.34\n2\t63.45\tDSQ\n3\tDNF", first));
        var html = StartListExchange.ToPrintHtml(first);
        Assert.Contains("&lt;script&gt; &amp;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("\"\"TEST\"\"", StartListExchange.ToTsv(first), StringComparison.Ordinal);
        Assert.Throws<DomainValidationException>(() => StartListExchange.ToTsv(first with { ApprovedAt = null }));
    }

    [Fact]
    public async Task DraftApprovalRevisionAndTransferPreserveDrawInputsAndHistory()
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("draw.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var plan = await SeedAsync(workspace, path);
        var series = await workspace.ReadAsync();
        var desk = await workspace.SaveStartListAsync(new(plan, series.Revision, "Operator", "Initial draw", s_at));
        var draft = Assert.Single(desk.Revisions);
        Assert.False(draft.IsApproved);
        var approvedDesk = await workspace.ApproveStartListAsync(draft.Id, desk.SeriesRevision, s_at);
        var first = Assert.Single(approvedDesk.Revisions);
        Assert.True(first.IsApproved);
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.RemoveCompetitionAsync(plan.CompetitionId, approvedDesk.SeriesRevision));
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveCompetitionAsync(plan.CompetitionId,
            plan.Competition with { RunCount = 1 }, approvedDesk.SeriesRevision));
        var results = first.Plan.Entries.Select((x, i) => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 6000 + i)).ToArray();
        var secondPlan = FisStartOrder.SecondRun(first, results);
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(secondPlan, approvedDesk.SeriesRevision, "Operator", "Not started", s_at)));
        var started = await workspace.MarkRunStartedAsync(first.Id, approvedDesk.SeriesRevision, "Starter", s_at);
        Assert.Equal(s_at, Assert.Single(started.Revisions).StartedAt);
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.MarkRunStartedAsync(first.Id, started.SeriesRevision, "Starter", s_at));
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(plan, started.SeriesRevision, "Operator", "Unsafe redraw", s_at)));
        var secondDesk = await workspace.SaveStartListAsync(new(secondPlan, started.SeriesRevision, "Operator", "External Run 1 results", s_at));
        var second = secondDesk.Revisions.Single(x => x.Plan.RunNumber == 2);
        var final = await workspace.ApproveStartListAsync(second.Id, secondDesk.SeriesRevision, s_at);
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(plan, final.SeriesRevision, "Operator", "Redraw", s_at)));
        var backup = folder.PathFor("transfer.ost");
        await workspace.BackupAsync(backup);
        await workspace.OpenAsync(backup);
        var reopened = await workspace.ReadStartListsAsync(plan.CompetitionId);
        Assert.Equal(2, reopened.Revisions.Count);
        Assert.All(reopened.Revisions, x => Assert.True(x.IsApproved));
        Assert.Equal("Starter", reopened.Revisions.Single(x => x.Plan.RunNumber == 1).StartedBy);
        Assert.Equal(secondPlan.Entries, reopened.Revisions.Single(x => x.Plan.RunNumber == 2).Plan.Entries);
        Assert.Equal(plan.Entries, FisStartOrder.FirstRun(plan.CompetitionId, plan.Competition, plan.Gender,
            plan.Entries.Select(x => x.Entrant).ToArray(), plan.PointsList, plan.Options, plan.Seed).Entries);
    }

    [Fact]
    public async Task InvalidOrStalePlansCannotWriteOrApproveAndOldDraftsRemain()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var plan = await SeedAsync(workspace, folder.PathFor("stale.ost"));
        var series = await workspace.ReadAsync();
        var altered = plan with { Entries = plan.Entries.Select(x => x with { Bib = 1 }).ToArray() };
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(altered, series.Revision, "Operator", "Invalid", s_at)));
        Assert.Empty((await workspace.ReadStartListsAsync(plan.CompetitionId)).Revisions);
        var saved = await workspace.SaveStartListAsync(new(plan, series.Revision, "Operator", "Initial", s_at));
        await Assert.ThrowsAsync<SeriesConflictException>(() => workspace.SaveStartListAsync(new(plan, series.Revision, "Operator", "Stale", s_at)));
        var second = await workspace.SaveStartListAsync(new(plan, saved.SeriesRevision, "Operator", "Review again", s_at));
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApproveStartListAsync(saved.Revisions[0].Id, second.SeriesRevision, s_at));
        var entry = plan.Entries[0].Entrant;
        var changed = await workspace.SaveDeskRowAsync(entry.CompetitorId, entry.Athlete with { Club = "Changed" }, null, false, null, second.SeriesRevision);
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.ApproveStartListAsync(second.Revisions[^1].Id, changed.Revision, s_at));
        Assert.All((await workspace.ReadStartListsAsync(plan.CompetitionId)).Revisions, x => Assert.False(x.IsApproved));
    }

    [Fact]
    public async Task FisDrawCannotSilentlyOmitTheOtherGenderFromACompetition()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var women = await SeedAsync(workspace, folder.PathFor("fields.ost"));
        var series = await workspace.ReadAsync();
        var saved = await workspace.SaveStartListAsync(new(women, series.Revision, "Operator", "Women", s_at));
        var athlete = new CompetitorValues("TEST MAN", "Athlete", 2000, "200001", "FIN", "Synthetic", Gender.Male);
        var added = await workspace.SaveDeskRowAsync(null, athlete, women.CompetitionId, true, null, saved.SeriesRevision);
        var entrants = new[] { new DrawEntrant(added.Value.Id, athlete, 10) };
        var conflicting = FisStartOrder.FirstRun(women.CompetitionId, s_race, Gender.Male, entrants, s_points, new(), "men-seed");
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(conflicting, added.Revision, "Operator", "Men", s_at)));
        var separate = FisStartOrder.FirstRun(women.CompetitionId, s_race, Gender.Male, entrants, s_points, new(FirstBib: 5), "men-seed");
        await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(separate, added.Revision, "Operator", "Men", s_at)));
        Assert.Single((await workspace.ReadStartListsAsync(women.CompetitionId)).Revisions);
    }

    [Fact]
    public async Task M4UpgradePreservesApprovedListsWithoutInventingAStart()
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("m4.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var plan = await SeedAsync(workspace, path);
        var series = await workspace.ReadAsync();
        var draft = await workspace.SaveStartListAsync(new(plan, series.Revision, "Operator", "Before upgrade", s_at));
        var id = Assert.Single(draft.Revisions).Id;
        await workspace.ApproveStartListAsync(id, draft.SeriesRevision, s_at);
        await workspace.CloseAsync();
        var options = new DbContextOptionsBuilder<SeriesDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using (var db = new SeriesDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260927144413_StartLists");
        }
        await workspace.OpenAsync(path);
        var reopened = Assert.Single((await workspace.ReadStartListsAsync(plan.CompetitionId)).Revisions);
        Assert.Equal(id, reopened.Id);
        Assert.True(reopened.IsApproved);
        Assert.Null(reopened.StartedAt);
        Assert.Equal(plan.Entries, reopened.Plan.Entries);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "m4.ost.before-upgrade-*.ost"));
    }

    [Fact]
    public async Task M3FileUpgradeKeepsOldDataAndCreatesStartListConstraints()
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("m3.ost");
        var options = new DbContextOptionsBuilder<SeriesDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
        await using (var db = new SeriesDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260926161344_ImportReceipts");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Series (SingleRow,FormatId,Id,Name,Location,Organizer,StartDate,EndDate,Nation,Season,Revision) VALUES (1,'OpenSkiTime.New/1','11111111-1111-1111-1111-111111111111','Synthetic','Test','Test','2026-09-27','2026-09-27','FIN','2026/27',1)");
        }
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        Assert.Equal("Synthetic", (await workspace.OpenAsync(path)).Values.Name);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "m3.ost.before-upgrade-*.ost"));
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('Runs','StartLists','StartListEntries')";
        Assert.Equal(3L, await command.ExecuteScalarAsync());
    }

    private static async Task<StartListPlan> SeedAsync(SeriesWorkspace workspace, string path)
    {
        var series = await workspace.CreateAsync(path, new("Synthetic", "Test", "Test", s_race.Date, s_race.Date, "FIN", "2026/27"));
        series = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
        var competitionId = series.Competitions[0].Id;
        var entrants = new List<DrawEntrant>();
        var revision = series.Revision;
        foreach (var entrant in Entrants(4))
        {
            var saved = await workspace.SaveDeskRowAsync(null, entrant.Athlete, competitionId, true, null, revision);
            revision = saved.Revision;
            entrants.Add(entrant with { CompetitorId = saved.Value.Id });
        }
        return FisStartOrder.FirstRun(competitionId, s_race, Gender.Female, entrants, s_points, new(), "storage-seed");
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-m4-tests", Guid.NewGuid().ToString("N"));
        public TestFolder() => Directory.CreateDirectory(_root);
        public string PathFor(string name) => Path.Combine(_root, name);
        public void Dispose()
        {
            var full = Path.GetFullPath(_root);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "openskitime-m4-tests"));
            if (!full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Unexpected test folder."); }
            Directory.Delete(full, recursive: true);
        }
    }
}

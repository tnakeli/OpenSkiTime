using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Reporting;
using OpenSkiTime.Timing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class PdfFactoryTests
{
    private static readonly DateOnly s_date = new(2026, 12, 12);
    private static readonly DateTimeOffset s_at = new(2026, 12, 12, 12, 0, 0, TimeSpan.FromHours(2));
    private static SeriesValues Series => new("Levi FIS", "Levi", "Synthetic club", s_date, s_date, "FIN", "2026/27");
    private static CompetitionValues Competition => new("Synthetic Slalom", "Levi FIS", s_date, Discipline.Slalom, RaceType.Fis, 2, 0,
        "1234", "Test slope", 1000, 800, 200, "TEST/26", new(2027, "Levi", "FIN", "FIS", "M"));
    private static readonly FisPenaltyListRules s_rules = new(2027, [new("FIS", 0, 0, 999), new("ENL", 4, 40, 999)],
        [new("SL", Gender.Male, 730, 165, 0, [0, 0, 0, 0, 0])]);

    [Theory]
    [InlineData("Levi_FIS", 1, "Levi_FIS_2026-12-12_StartList_Run1.pdf")]
    [InlineData("Levi FIS", 2, "Levi_FIS_2026-12-12_StartList_Run2.pdf")]
    [InlineData("  Levi\t   FIS  ", 1, "Levi_FIS_2026-12-12_StartList_Run1.pdf")]
    [InlineData("Levi\\/:*?\"<>|FIS", 1, "Levi_FIS_2026-12-12_StartList_Run1.pdf")]
    public void FileNamesArePortableDeterministicAndCultureIndependent(string name, int run, string expected)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fi-FI");
            Assert.Equal(expected, ReportFileNameBuilder.Build(name, s_date, PdfReportType.StartList, run));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Equal(expected, ReportFileNameBuilder.Build(name, s_date, PdfReportType.StartList, run));
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(9)]
    public void CatalogCreatesEveryConfiguredRunWithStableIdsAndPrerequisites(int runCount)
    {
        var source = MemorySource("", 0) with { Competition = new(Guid.NewGuid(), Competition with { RunCount = runCount }) };
        var catalog = ReportCatalog.GetAvailableReports(source);
        Assert.Equal(9 + 2 * runCount, catalog.Count);
        Assert.Equal(3, catalog.Count(x => x.CompetitionId is null));
        Assert.Equal(Enumerable.Range(1, runCount), catalog.Where(x => x.Type == PdfReportType.RefereeReport).Select(x => x.RunNumber!.Value));
        Assert.Equal(catalog.Select(x => x.Id), ReportCatalog.GetAvailableReports(source).Select(x => x.Id));
        Assert.Equal(catalog.Count, catalog.Select(x => x.Id).Distinct().Count());
        Assert.All(catalog, x => Assert.False(x.CanGenerate));
        source = source with { FilePath = Path.Combine(Path.GetTempPath(), "saved.ost") };
        catalog = ReportCatalog.GetAvailableReports(source);
        Assert.All(catalog.Where(x => x.Type is PdfReportType.StartList or PdfReportType.OfficialResults or PdfReportType.PenaltyCalculation or PdfReportType.TimingReport), x => Assert.False(x.CanGenerate));
        Assert.All(catalog.Where(x => x.Type == PdfReportType.RefereeReport), x => Assert.True(x.CanGenerate));
        Assert.Equal(3, ReportCatalog.GetAvailableReports(source with { Competition = null }).Count);
    }
    [Fact]
    public void SanitizedCompetitionNamesCannotOverwriteAnotherCompetitionsReports()
    {
        var source = MemorySource("saved.ost", 0);
        var a = source.Competition! with { Values = Competition with { ShortLabel = "Levi/FIS" } };
        var b = new CompetitionDetails(Guid.NewGuid(), Competition with { ShortLabel = "Levi:FIS" });
        source = source with { Series = source.Series with { Competitions = [a, b] }, Competition = a };
        var first = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.CompetitionEntries);
        var second = ReportCatalog.GetAvailableReports(source with { Competition = b }).Single(x => x.Type == PdfReportType.CompetitionEntries);
        Assert.NotEqual(first.FileName, second.FileName);
    }
    [Fact]
    public void GroupingUsesDomainCategoriesAndOnlyActualCompetitionParticipants()
    {
        var source = MemorySource("saved.ost", 6);
        var byNation = EntryReportBuilder.Build(source, ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.EventSeriesEntriesByNation));
        Assert.Equal(["FIN", "Nation not supplied", "SWE"], byNation.Select(x => x.Label));
        var byCategory = EntryReportBuilder.Build(source, ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.EventSeriesEntriesByCategory));
        Assert.Equal(["Senior", "Young"], byCategory.Select(x => x.Label));
        Assert.All(byCategory.SelectMany(x => x.Entries), x => Assert.Equal(CategoryResolver.Resolve(x.Athlete, source.Desk.Categories), x.Category));
        var participants = EntryReportBuilder.Build(source, ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.CompetitionEntries));
        Assert.Equal(3, participants.Sum(x => x.Entries.Count));
        var reverse = source with { Desk = source.Desk with { Competitors = source.Desk.Competitors.Reverse().ToArray() } };
        Assert.Equal(byNation.SelectMany(x => x.Entries).Select(x => x.Id),
            EntryReportBuilder.Build(reverse, ReportCatalog.GetAvailableReports(reverse).Single(x => x.Type == PdfReportType.EventSeriesEntriesByNation)).SelectMany(x => x.Entries).Select(x => x.Id));
    }
    [Fact]
    public async Task RealPdfGenerationStatusRegenerationAndFailurePreserveLastValidFile()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("test.ost"), 20);
        var descriptor = ReportCatalog.GetAvailableReports(source)[0];
        var path = folder.PathFor(descriptor.FileName);
        Assert.Equal(PdfReportStatus.NotGenerated, ReportStatusService.GetStatus(source, descriptor));
        GeneratedPdf? metadata = null;
        Task Save(GeneratedPdf value) { metadata = value; return Task.CompletedTask; }
        var service = new ReportGenerationService(new QuestPdfRenderer());
        Assert.True((await service.GenerateAsync(source, descriptor, Save, s_at)).Success);
        AssertPdf(path);
        source = source with { Settings = source.Settings with { Reports = [metadata!] } };
        Assert.Equal(PdfReportStatus.Generated, ReportStatusService.GetStatus(source, descriptor));
        source = source with { Series = source.Series with { Revision = source.Series.Revision + 1 } };
        Assert.Equal(PdfReportStatus.Outdated, ReportStatusService.GetStatus(source, descriptor));
        Assert.True(File.Exists(path));
        var previous = await File.ReadAllBytesAsync(path);
        Assert.False((await new ReportGenerationService(new FailingRenderer()).GenerateAsync(source, descriptor, Save, s_at)).Success);
        Assert.Equal(previous, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.True((await service.GenerateAsync(source, descriptor, Save, s_at.AddMinutes(1))).Success);
        Assert.NotEqual(previous, await File.ReadAllBytesAsync(path));
        source = source with { Settings = source.Settings with { Reports = [metadata!] } };
        Assert.Equal(PdfReportStatus.Generated, ReportStatusService.GetStatus(source, descriptor));
        await File.AppendAllTextAsync(path, "edited");
        Assert.Equal(PdfReportStatus.Outdated, ReportStatusService.GetStatus(source, descriptor));
        File.Delete(path);
        Assert.Equal(PdfReportStatus.NotGenerated, ReportStatusService.GetStatus(source, descriptor));
    }
    [Fact]
    public async Task CorruptEmbeddedBackgroundPreservesTheValidPdfAndReceipt()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("test.ost"), 6);
        var descriptor = ReportCatalog.GetAvailableReports(source)[0];
        var service = new ReportGenerationService(new QuestPdfRenderer());
        var receipts = new List<GeneratedPdf>();
        Task Save(GeneratedPdf receipt) { receipts.Add(receipt); return Task.CompletedTask; }
        Assert.True((await service.GenerateAsync(source, descriptor, Save, s_at)).Success);
        var path = folder.PathFor(descriptor.FileName);
        var before = await File.ReadAllBytesAsync(path);
        source = source with { Settings = source.Settings with { Profile = new(TemplateName: "corrupt.pdf", TemplatePdf: "Invalid synthetic PDF"u8.ToArray()) } };
        Assert.False((await service.GenerateAsync(source, descriptor, Save, s_at.AddMinutes(1))).Success);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(receipts);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.generation.lock"));
    }
    [Fact]
    public async Task GenerateAllContinuesAfterFailureAndSkipsUnavailableReports()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("test.ost"), 6);
        var outcomes = await new ReportGenerationService(new FailingRenderer(PdfReportType.EventSeriesEntriesByNation))
            .GenerateAllAsync(source, _ => Task.CompletedTask, s_at);
        Assert.Single(outcomes, x => !x.Success);
        Assert.Equal(7, outcomes.Count(x => x.Success)); // six entry lists + two referee reports, one failure
        Assert.True(File.Exists(folder.PathFor(ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.CompetitionEntries).FileName)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(source.FilePath)!, "*.tmp"));
    }
    [Fact]
    public async Task BackgroundMarginsMultipageAndMissingBackgroundAreSupported()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("test.ost"), 350);
        var background = BackgroundPdf();
        File.WriteAllBytes(folder.PathFor("branding.pdf"), background);
        CopyQa(folder.PathFor("branding.pdf"), "branding.pdf");
        QuestPdfRenderer.ValidateTemplate(background);
        source = source with { Settings = source.Settings with { Profile = new(TopMm: 28, BottomMm: 25, LeftMm: 18, RightMm: 18, TemplateName: "branding.pdf", TemplatePdf: background) } };
        var descriptor = ReportCatalog.GetAvailableReports(source)[0];
        var path = folder.PathFor(descriptor.FileName);
        new QuestPdfRenderer().Render(new(descriptor, source, EntryReportBuilder.Build(source, descriptor), s_at), path);
        AssertPdf(path);
        // QPDF successfully addresses actual generated pages, rather than assuming a row count implies pagination.
        DocumentOperation.LoadFile(path).TakePages("2").Save(folder.PathFor("page2.pdf"));
        AssertPdf(folder.PathFor("page2.pdf"));
        source = source with { Settings = source.Settings with { Profile = new(TemplateName: "missing.pdf") } };
        new QuestPdfRenderer().Render(new(descriptor, source, EntryReportBuilder.Build(source, descriptor), s_at), folder.PathFor("fallback.pdf"));
        AssertPdf(folder.PathFor("fallback.pdf"));
        Assert.Throws<DomainValidationException>(() => new EventPrintProfile(LeftMm: 80, RightMm: 80).Validate());
        CopyQa(path, "multipage-background.pdf");
    }
    [Fact]
    public async Task SameReportGenerationIsSerializedAcrossServiceInstances()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("test.ost"), 6);
        var renderer = new ConcurrencyRenderer();
        var descriptor = ReportCatalog.GetAvailableReports(source)[0];
        var results = await Task.WhenAll(new ReportGenerationService(renderer).GenerateAsync(source, descriptor, _ => Task.CompletedTask, s_at),
            new ReportGenerationService(renderer).GenerateAsync(source, descriptor, _ => Task.CompletedTask, s_at));
        Assert.All(results, x => Assert.True(x.Success));
        Assert.Equal(1, renderer.MaximumConcurrent);
        AssertPdf(folder.PathFor(descriptor.FileName));
    }
    [Fact]
    public async Task ActiveCaptureCommitsAndReplaysWhilePdfRenderingIsBlockedAndFails()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.PathFor("active.ost"), 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var simulator = new SimulatorTimingSource();
        await timing.StartAsync(simulator, new("Synthetic", "Local simulator", TimingRulesTests.Date, Simulation: true), "Operator");
        await timing.ArmAsync(1, 1);
        var source = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, list.Plan.CompetitionId);
        var descriptor = ReportCatalog.GetAvailableReports(source)[0];
        var renderer = new BlockingFailureRenderer();
        var generation = new ReportGenerationService(renderer).GenerateAsync(source, descriptor, _ => Task.CompletedTask, s_at);
        try
        {
            await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await simulator.PulseAsync(0, TimeSpan.FromHours(12).Ticks);
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results[0].Status == TimingStatus.OnCourse);
            await simulator.PulseAsync(1, TimeSpan.FromHours(12).Ticks + TimeSpan.FromSeconds(60).Ticks);
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Complete);
            // Durable read through the real persistence boundary succeeds before releasing the renderer.
            var persisted = await workspace.ReadTimingAsync(list.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, persisted.Packets.Count);
            Assert.Equal(6000, TimingReplay.Restore(persisted, new AlgeDecoderFactory()).Results[0].Hundredths);
            Assert.False(generation.IsCompleted);
        }
        finally { renderer.Release.Set(); }
        Assert.False((await generation).Success);
        await timing.StopAsync();
        var replay = await workspace.ReadTimingAsync(list.Id);
        Assert.All(replay.Sessions, session => Assert.True(session.CleanStop));
        Assert.Equal(6000, TimingReplay.Restore(replay, new AlgeDecoderFactory()).Results[0].Hundredths);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(source.FilePath)!, "*.tmp"));
    }
    [Fact]
    public async Task OldFilesReadDefaultsWithoutMutationAndEmbeddedProfileSurvivesBackup()
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("old.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        await workspace.CreateAsync(path, Series);
        var before = (await workspace.ReadAsync()).Revision;
        await workspace.CloseAsync();
        var original = await File.ReadAllBytesAsync(path);
        await workspace.OpenAsync(path);
        var defaults = await workspace.ReadPdfFactoryAsync();
        Assert.Equal(new EventPrintProfile(), defaults.Profile);
        Assert.Empty(defaults.Reports);
        await workspace.CloseAsync();
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        await workspace.OpenAsync(path);
        var profile = new EventPrintProfile(TopMm: 25, TemplateName: "background.pdf", TemplatePdf: BackgroundPdf());
        await workspace.SavePrintProfileAsync(profile);
        await workspace.SaveGeneratedPdfAsync(new("test", "file.pdf", s_at, "version", "hash"));
        Assert.Equal(before, (await workspace.ReadAsync()).Revision);
        var backup = folder.PathFor("copy.ost");
        await workspace.BackupAsync(backup);
        await workspace.OpenAsync(backup);
        var restored = await workspace.ReadPdfFactoryAsync();
        Assert.Equal(profile.TopMm, restored.Profile.TopMm);
        Assert.Equal(profile.TemplatePdf, restored.Profile.TemplatePdf);
        Assert.Single(restored.Reports);
        await using var connection = new SqliteConnection($"Data Source={backup};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='__EFMigrationsHistory'";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
    [Fact]
    public async Task SavedTwoRunRaceProducesAllReportsReadOnlyAndTimingChangesInvalidateFreshness()
    {
        using var folder = new TestFolder();
        var path = folder.PathFor("race.ost");
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var series = await workspace.CreateAsync(path, Series, [Competition]);
        var competition = series.Competitions[0];
        var entrants = new List<DrawEntrant>();
        for (var i = 1; i <= 6; i++)
        {
            var athlete = new CompetitorValues("RACER" + i, "Synthetic", i % 2 == 0 ? 2000 : 2010, (900000 + i).ToString(CultureInfo.InvariantCulture),
                i % 2 == 0 ? "FIN" : "SWE", "Synthetic club", Gender.Male);
            var saved = await workspace.SaveDeskRowAsync(null, athlete, competition.Id, true, null, (await workspace.ReadAsync()).Revision);
            entrants.Add(new(saved.Value.Id, athlete, i * 10));
        }
        await workspace.SaveCategoryRuleAsync(null, new("Senior", 1900, 2005, null, 0), (await workspace.ReadAsync()).Revision);
        await workspace.SaveCategoryRuleAsync(null, new("Young", 2006, 2026, null, 1), (await workspace.ReadAsync()).Revision);
        var plan = FisStartOrder.FirstRun(competition.Id, Competition, Gender.Male, entrants, new("1327", s_date, s_date, s_rules), new(), "pdf-fixture");
        var first = (await workspace.SaveStartListAsync(new(plan, (await workspace.ReadAsync()).Revision, "Operator", "Synthetic test", s_at))).Revisions[0];
        first = (await workspace.MarkRunStartedAsync(first.Id, (await workspace.ReadAsync()).Revision, "Operator", s_at)).Revisions[0];
        await CompleteRun(workspace, first, 5000);
        var firstReplay = await workspace.ReadTimingAsync(first.Id);
        var firstTiming = TimingReplay.Restore(firstReplay, new AlgeDecoderFactory());
        var secondPlan = FisStartOrder.SecondRun(first, firstTiming.ToRunFinishes());
        var lists = await workspace.SaveStartListAsync(new(secondPlan, (await workspace.ReadAsync()).Revision, "Operator", "Synthetic second run", s_at, TimingReplay.InputVersion(firstReplay)));
        var second = lists.Revisions.Single(x => x.Plan.RunNumber == 2);
        await CompleteRun(workspace, second, 6000);
        var secondReplay = await workspace.ReadTimingAsync(second.Id);
        firstReplay = await workspace.ReadTimingAsync(first.Id);
        var race = FisRaceResults.Assemble(first, TimingReplay.Restore(firstReplay, new AlgeDecoderFactory()), second, TimingReplay.Restore(secondReplay, new AlgeDecoderFactory()));
        var penalty = FisPenalty.Calculate(s_rules.Resolve("FIS", Discipline.Slalom, Gender.Male), race.PenaltyCompetitors);
        var information = RaceInformation.Empty(Competition) with { Category = "FIS", Runs = [
            RaceInformation.Empty(Competition).Runs[0] with { Gates = 42, TurningGates = 40, StartTime = "12:00", CourseSetter = new("Test", "Setter", "FIN") },
            RaceInformation.Empty(Competition).Runs[1] with { Gates = 42, TurningGates = 40, StartTime = "13:00", CourseSetter = new("Test", "Setter", "FIN") }] };
        var details = new FisXmlDetails("FIS", new("Test", "TD", "FIN"), new("Test", "Chief", "FIN"),
            information.Runs.Select(x => new FisRunXmlDetails(42, 40, x.StartTime, x.CourseSetter)).ToArray(), information);
        var xml = FisResultXml.Create(race, Series, penalty, details);
        await workspace.ApproveResultAsync(new(competition.Id, first.Id, second.Id, ResultSourceFingerprint.Create(firstReplay, secondReplay),
            (await workspace.ReadAsync()).Revision, "Synthetic TD", "FIN1234.xml", xml, penalty.Calculated, penalty.Applied, information));
        var source = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
        Assert.All(ReportCatalog.GetAvailableReports(source), x => Assert.True(x.CanGenerate, x.UnavailableReason));
        Assert.Equal(first.Plan.Entries.Select(x => x.Entrant.CompetitorId), EntryReportBuilder.Build(source,
            ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.StartList && x.RunNumber == 1)).SelectMany(x => x.Entries).Select(x => x.Id));
        var revision = (await workspace.ReadAsync()).Revision;
        var outcomes = await new ReportGenerationService(new QuestPdfRenderer()).GenerateAllAsync(source, r => workspace.SaveGeneratedPdfAsync(r), s_at);
        Assert.Equal(13, outcomes.Count);
        Assert.All(outcomes, x => Assert.True(x.Success, x.Error));
        Assert.Equal(revision, (await workspace.ReadAsync()).Revision);
        var unchanged = await workspace.ReadTimingAsync(first.Id);
        Assert.Equal(ResultSourceFingerprint.Create(firstReplay), ResultSourceFingerprint.Create(unchanged));
        source = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
        Assert.All(ReportCatalog.GetAvailableReports(source), d => Assert.Equal(PdfReportStatus.Generated, ReportStatusService.GetStatus(source, d)));
        foreach (var descriptor in ReportCatalog.GetAvailableReports(source)) { AssertPdf(folder.PathFor(descriptor.FileName)); CopyQa(folder.PathFor(descriptor.FileName), descriptor.FileName); }
        var qa = Environment.GetEnvironmentVariable("OST_PDF_QA_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(qa))
        {
            var backup = Path.Combine(qa, Guid.NewGuid().ToString("N") + ".ost");
            await workspace.BackupAsync(backup);
            File.Move(backup, Path.Combine(qa, "synthetic-race.ost"), overwrite: true);
        }
        var approval = Assert.Single(await workspace.ReadApprovedResultsAsync(competition.Id));
        Assert.Equal(approval.Id, Assert.Single(source.Finals).Approval!.Id);
        Assert.Equal(approval.AppliedPenalty, source.Finals[0].Approval!.AppliedPenalty);
        var official = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.OfficialResults);
        var officialBytes = await File.ReadAllBytesAsync(folder.PathFor(official.FileName));
        var editedInformation = information with { Runs = information.Runs.Select(x => x with { Gates = 43 }).ToArray() };
        await workspace.SaveRaceInformationAsync(competition.Id, editedInformation, (await workspace.ReadAsync()).Revision, s_at);
        var unapproved = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
        var unavailableOfficial = ReportCatalog.GetAvailableReports(unapproved).Single(x => x.Type == PdfReportType.OfficialResults);
        Assert.False(unavailableOfficial.CanGenerate);
        Assert.Equal(PdfReportStatus.Outdated, ReportStatusService.GetStatus(unapproved, unavailableOfficial));
        Assert.False((await new ReportGenerationService(new QuestPdfRenderer()).GenerateAsync(unapproved, unavailableOfficial,
            r => workspace.SaveGeneratedPdfAsync(r), s_at)).Success);
        Assert.Equal(officialBytes, await File.ReadAllBytesAsync(folder.PathFor(official.FileName)));
        await workspace.SaveRaceInformationAsync(competition.Id, information, (await workspace.ReadAsync()).Revision, s_at);
        await workspace.SaveCompetitionAsync(competition.Id, Competition with { Calendar = Competition.Calendar! with { Category = "ENL" } }, (await workspace.ReadAsync()).Revision);
        unapproved = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
        Assert.Equal(40, Assert.Single(unapproved.Finals).Penalty!.Applied);
        Assert.Null(unapproved.Finals[0].Approval);
        Assert.True(ReportCatalog.GetAvailableReports(unapproved).Single(x => x.Type == PdfReportType.PenaltyCalculation).CanGenerate);
        Assert.False(ReportCatalog.GetAvailableReports(unapproved).Single(x => x.Type == PdfReportType.OfficialResults).CanGenerate);
        await workspace.SaveCompetitionAsync(competition.Id, Competition, (await workspace.ReadAsync()).Revision);
        source = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
        Assert.True(ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.OfficialResults).CanGenerate);
        // A fresh raw packet does not advance Series.Revision, yet makes timing-dependent PDFs stale.
        await using var session = await new SqliteSeriesFileStore().OpenAsync(path);
        var store = (ITimingStore)session;
        var capture = await store.BeginCaptureAsync(first.Id, new("Synthetic", "Test", s_date, Simulation: true), "Operator", s_at);
        await store.AppendRawAsync(new(capture.Id, 1, s_at, "alge", "Synthetic", "A", Encoding.ASCII.GetBytes("malformed synthetic input")));
        await store.EndCaptureAsync(capture.Id, s_at.AddMinutes(1));
        var changed = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
        Assert.NotEqual(source.SourceVersion, changed.SourceVersion);
        var timingDescriptor = ReportCatalog.GetAvailableReports(changed).Single(x => x.Type == PdfReportType.TimingReport);
        Assert.Equal(PdfReportStatus.Outdated, ReportStatusService.GetStatus(changed, timingDescriptor));
        var generated = await new ReportGenerationService(new QuestPdfRenderer()).GenerateAsync(changed, timingDescriptor, r => workspace.SaveGeneratedPdfAsync(r), s_at.AddHours(1));
        Assert.True(generated.Success);
        changed = await new PdfReportSourceBuilder(new AlgeDecoderFactory()).BuildAsync(workspace, competition.Id);
        Assert.Equal(PdfReportStatus.Generated, ReportStatusService.GetStatus(changed, timingDescriptor));
    }
    private static async Task CompleteRun(SeriesWorkspace workspace, StartListRevision list, long baseTime)
    {
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await timing.StartAsync(new SimulatorTimingSource(), new("Synthetic", "Local simulator", s_date, Simulation: true), "Operator");
        await timing.StopAsync();
        foreach (var entry in list.Plan.Entries)
        { await timing.CorrectAsync(new(DecisionKind.Time, CompetitorId: entry.Entrant.CompetitorId, Hundredths: baseTime + entry.Position * 100), "Operator", "Synthetic test time"); }
        Assert.True(timing.Snapshot!.Complete);
    }
    public static byte[] BackgroundPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return Document.Create(d => d.Page(p => { p.Size(PageSizes.A4); p.Margin(5, Unit.Millimetre);
            p.Header().Text("SYNTHETIC ORGANIZER BACKGROUND").FontColor("#AA2200");
            p.Footer().Text("SYNTHETIC SPONSOR FOOTER").FontColor("#AA2200"); })).GeneratePdf();
    }
    [Fact]
    public void PrintProfilePreviewRendersOneA4PageWithCurrentMarginsAndOptionalBackground()
    {
        using var folder = new TestFolder();
        foreach (var background in new byte[]?[] { null, BackgroundPdf() })
        {
            var profile = new EventPrintProfile(TopMm: 28, BottomMm: 25, LeftMm: 18, RightMm: 22,
                TemplateName: background is null ? null : "synthetic.pdf", TemplatePdf: background);
            var filename = background is null ? "profile-default.pdf" : "profile-background.pdf";
            var path = folder.PathFor(filename);
            QuestPdfRenderer.RenderPrintProfilePreview(profile, path);
            AssertPdf(path);
            DocumentOperation.LoadFile(path).TakePages("1").Save(folder.PathFor("page1.pdf"));
            AssertPdf(folder.PathFor("page1.pdf"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
            CopyQa(path, filename);
        }
        var invalidPath = folder.PathFor("invalid.pdf");
        Assert.Throws<DomainValidationException>(() => QuestPdfRenderer.RenderPrintProfilePreview(new(LeftMm: 80, RightMm: 80), invalidPath));
        Assert.False(File.Exists(invalidPath));
    }
    [Theory]
    [InlineData(0, 15, 15, 12, 12, false)]
    [InlineData(8, 15, 15, 12, 12, false)]
    [InlineData(160, 15, 15, 12, 12, false)]
    [InlineData(8, 80, 60, 80, 20, false)]
    [InlineData(8, 80, 60, 80, 20, true)]
    [InlineData(8, 0, 0, 0, 0, false)]
    public void RefereeReportMatchesFormFieldsAndPreservesEverySelectedRunStatus(int count, int top, int bottom, int left, int right, bool longNotes)
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("race.ost"), count);
        var entries = source.Desk.Competitors.Select((competitor, index) => new StartListEntry(index + 1, 1001 + index,
            new(competitor.Id, competitor.Values, 0), "Synthetic")).ToArray();
        var plan = new StartListPlan(source.Competition!.Id, Competition, Gender.Male, 1, "synthetic/v1", "fixed",
            new(), new("synthetic", s_date, s_date), null, [], entries);
        var list = new StartListRevision(Guid.NewGuid(), 1, s_at, null, "Operator", "Synthetic PDF test", plan);
        var statuses = new[] { TimingStatus.DSQ, TimingStatus.DNS, TimingStatus.NPS, TimingStatus.DNF };
        var reason = "MISSED SYNTHETIC GATE" + (longNotes ? " " + string.Concat(Enumerable.Repeat("Synthetic detail ", 55)) + "END-NOTE" : "");
        var results = entries.Select((entry, index) => new TimingResult(entry, statuses[index % 4], null, null, null, null, "")
        { Disqualification = index % 4 == 0 ? new(17, reason, "SYNTHETIC JUDGE") : null }).ToArray();
        var second = list with { Id = Guid.NewGuid(), Plan = plan with { RunNumber = 2 } };
        var secondResults = results.Select(x => x with { Entry = x.Entry with { Bib = x.Bib + 1000 } }).ToArray();
        source = source with
        {
            Settings = new(new(TopMm: top, BottomMm: bottom, LeftMm: left, RightMm: right,
                TemplateName: "synthetic-background.pdf", TemplatePdf: BackgroundPdf()), []),
            Information = source.Information! with { Jury = [new("Referee", new("Test", "REFEREE", "FIN")), new("TechnicalDelegate", new("Other", "TD", "SWE"))] },
            Runs = [new(list, new(list.Id, 0, results, [], []), new(list, [], [], [])),
                new(second, new(second.Id, 0, secondResults, [], []), new(second, [], [], []))]
        };
        var descriptor = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.RefereeReport && x.RunNumber == 1);
        var path = folder.PathFor("referee-" + count + "-" + top + (longNotes ? "-long-notes" : "") + ".pdf");
        new QuestPdfRenderer().Render(new(descriptor, source, [], s_at), path);
        CopyQa(path, Path.GetFileName(path));
        using var pdf = PdfDocument.Open(path);
        var pages = pdf.GetPages().ToArray();
        if (count <= 8 && !longNotes) { Assert.Single(pages); }
        if (count == 160) { Assert.True(pages.Length > 1); }
        Assert.All(pages, page => { Assert.InRange(page.Width, 594, 596); Assert.InRange(page.Height, 841, 843); });
        var text = string.Join(" ", pages.Select(x => x.Text));
        static string WithoutWhitespace(string value) => string.Concat(value.Where(x => !char.IsWhiteSpace(x)));
        var compactText = WithoutWhitespace(text);
        Assert.DoesNotContain("SYNTHETICORGANIZERBACKGROUND", compactText, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETICSPONSORFOOTER", compactText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSkiTime", compactText, StringComparison.Ordinal);
        foreach (var label in new[] { "REPORT BY THE REFEREE", "Place", "Country", "Codex", "Name of the Event", "Date", "Category", "Gender", "Event",
            "Name - Surname", "Nat.", "Gate Number.", "Gate Judge", "Notes", "Did not start", "Not permitted to start", "Did not finish",
            "Time published", "Deadline", "The Referee", "Run 1", "Synthetic Slalom", "1234", "12.12.2026", "REFEREE Test FIN" })
        { Assert.Contains(WithoutWhitespace(label), compactText, StringComparison.Ordinal); }
        Assert.DoesNotContain("TD Other", text, StringComparison.Ordinal);
        var words = pages.SelectMany(x => x.GetWords()).ToArray();
        Assert.InRange(pages[0].GetWords().First(x => x.Text == "REPORT").BoundingBox.Left, 34, 35);
        foreach (var result in results)
        { Assert.Single(words, x => x.Text == result.Bib.ToString(CultureInfo.InvariantCulture)); }
        Assert.DoesNotContain(words, word => secondResults.Any(x => word.Text == x.Bib.ToString(CultureInfo.InvariantCulture)));
        if (count > 0)
        {
            Assert.Contains("MISSEDSYNTHETICGATE", compactText, StringComparison.Ordinal);
            if (longNotes)
            {
                var disqualifiedCount = results.Count(x => x.Status == TimingStatus.DSQ);
                Assert.Equal(disqualifiedCount * 55, words.Count(x => x.Text == "detail"));
                Assert.Equal(disqualifiedCount, System.Text.RegularExpressions.Regex.Count(compactText, "END-NOTE"));
            }
            Assert.Contains("SYNTHETICJUDGE", compactText, StringComparison.Ordinal);
            var firstWords = pages[0].GetWords().ToArray();
            var judge = firstWords.First(x => x.Text == "JUDGE");
            var note = firstWords.First(x => x.Text == "MISSED");
            Assert.True(judge.BoundingBox.Left < note.BoundingBox.Left, "Gate judge must precede Notes in the reference column order.");
        }
    }
    [Theory]
    [InlineData(TimingStatus.DSQ, 30)]
    [InlineData(TimingStatus.DNS, 120)]
    [InlineData(TimingStatus.DNF, 250)]
    [InlineData(TimingStatus.NPS, 50)]
    public void RefereeStatusTablesExpandWithoutLosingBibs(TimingStatus status, int count)
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("race.ost"), count);
        var entries = source.Desk.Competitors.Select((competitor, index) => new StartListEntry(index + 1, 3001 + index,
            new(competitor.Id, competitor.Values, 0), "Synthetic")).ToArray();
        var plan = new StartListPlan(source.Competition!.Id, Competition, Gender.Male, 1, "synthetic/v1", "fixed",
            new(), new("synthetic", s_date, s_date), null, [], entries);
        var list = new StartListRevision(Guid.NewGuid(), 1, s_at, null, "Operator", "Synthetic status expansion", plan);
        var results = entries.Select(entry => new TimingResult(entry, status, null, null, null, null, "")
        { Disqualification = status == TimingStatus.DSQ ? new(17, "MISSED SYNTHETIC GATE", "SYNTHETIC JUDGE") : null }).ToArray();
        source = source with { Runs = [new(list, new(list.Id, 0, results, [], []), new(list, [], [], []))] };
        var descriptor = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.RefereeReport && x.RunNumber == 1);
        var path = folder.PathFor("referee-expanded-" + status + ".pdf");
        new QuestPdfRenderer().Render(new(descriptor, source, [], s_at), path);
        CopyQa(path, Path.GetFileName(path));
        using var pdf = PdfDocument.Open(path);
        var pages = pdf.GetPages().ToArray();
        var words = pages.SelectMany(page => page.GetWords()).ToArray();
        Assert.All(results, result => Assert.Single(words, word => word.Text == result.Bib.ToString(CultureInfo.InvariantCulture)));
        if (status is TimingStatus.DSQ or TimingStatus.DNF) { Assert.True(pages.Length > 1); }
        Assert.Contains("TheReferee", string.Concat(pages.SelectMany(page => page.Text).Where(c => !char.IsWhiteSpace(c))), StringComparison.Ordinal);
    }
    [Fact]
    public void OtherReportsRetainSeriesBackgroundHeaderAndFooter()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("race.ost"), 6);
        source = source with { Settings = new(new(TemplateName: "synthetic-background.pdf", TemplatePdf: BackgroundPdf()), []) };
        var descriptor = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.CompetitionEntries);
        var path = folder.PathFor(descriptor.FileName);
        new QuestPdfRenderer().Render(new(descriptor, source, EntryReportBuilder.Build(source, descriptor), s_at), path);
        using var pdf = PdfDocument.Open(path);
        var text = string.Concat(pdf.GetPages().SelectMany(x => x.Text).Where(x => !char.IsWhiteSpace(x)));
        Assert.Contains("SYNTHETICORGANIZERBACKGROUND", text, StringComparison.Ordinal);
        Assert.Contains("SYNTHETICSPONSORFOOTER", text, StringComparison.Ordinal);
        Assert.Contains("OpenSkiTime", text, StringComparison.Ordinal);
    }
    [Fact]
    public async Task SeriesEntriesMarkEachCompetitionAndOmitColumnsNobodyUses()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("test.ost"), 6);
        var first = source.Competition! with { Values = Competition with { ShortLabel = "SL-A" } };
        var second = new CompetitionDetails(Guid.NewGuid(), Competition with { Name = "Synthetic GS", ShortLabel = "GS-B", Date = s_date.AddDays(1), Discipline = Discipline.GiantSlalom });
        var racers = source.Desk.Competitors;
        source = source with
        {
            // Declared out of date order: columns follow race order, not storage order.
            Series = source.Series with { Competitions = [second, first] }, Competition = first,
            Desk = source.Desk with
            {
                Categories = [],
                Participations = [.. source.Desk.Participations.Select(p => p with { CompetitionId = first.Id }),
                    new(racers[5].Id, second.Id, true, null, null), new(racers[0].Id, second.Id, false, null, null)]
            }
        };
        var descriptor = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.EventSeriesEntries);
        Assert.True((await new ReportGenerationService(new QuestPdfRenderer()).GenerateAsync(source, descriptor, _ => Task.CompletedTask, s_at)).Success);
        var path = folder.PathFor(descriptor.FileName);
        CopyQa(path, "series-entries.pdf");
        using var pdf = PdfDocument.Open(path);
        var words = pdf.GetPages().SelectMany(x => x.GetWords()).ToArray();
        // Nobody has a bib or a category in this series, so both columns are omitted.
        Assert.DoesNotContain(words, x => x.Text is "Bib" or "Category");
        var sl = words.Single(x => x.Text == "SL-A"); var gs = words.Single(x => x.Text == "GS-B");
        Assert.True(sl.BoundingBox.Left < gs.BoundingBox.Left);
        string Marks(int racer)
        {
            var row = words.Single(x => x.Text == "RACER" + racer.ToString("D4", CultureInfo.InvariantCulture)).BoundingBox.Bottom;
            var marks = words.Where(x => x.Text == "X" && Math.Abs(x.BoundingBox.Bottom - row) < 2).ToArray();
            return (marks.Any(x => Math.Abs(x.BoundingBox.Left - sl.BoundingBox.Left) < 30) ? "S" : "") + (marks.Any(x => Math.Abs(x.BoundingBox.Left - gs.BoundingBox.Left) < 30) ? "G" : "");
        }
        Assert.Equal(["S", "S", "S", "", "", "G"], Enumerable.Range(1, 6).Select(Marks));
        Assert.Contains("OpenSkiTime" + ProductInfo.Version, string.Concat(pdf.GetPages().SelectMany(x => x.Text).Where(c => !char.IsWhiteSpace(c))), StringComparison.Ordinal);
    }
    [Fact]
    public async Task CompetitionEntriesKeepBibAndCategoryWhenAnyEntrantHasOne()
    {
        using var folder = new TestFolder();
        var source = MemorySource(folder.PathFor("test.ost"), 4);
        source = source with { Desk = source.Desk with { Participations = [.. source.Desk.Participations.Select((p, i) => i == 0 ? p with { ImportedBib = 17 } : p)] } };
        var descriptor = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.CompetitionEntries);
        Assert.True((await new ReportGenerationService(new QuestPdfRenderer()).GenerateAsync(source, descriptor, _ => Task.CompletedTask, s_at)).Success);
        using var pdf = PdfDocument.Open(folder.PathFor(descriptor.FileName));
        var words = pdf.GetPages().SelectMany(x => x.GetWords()).Select(x => x.Text).ToArray();
        Assert.Contains("Bib", words); Assert.Contains("17", words); Assert.Contains("Category", words); Assert.Contains("Young", words);
        // Competition entries list a single race, so no per-competition participation columns are added.
        Assert.DoesNotContain("X", words);
    }
    [Fact]
    public async Task TimingReportUsesTheFisFormLayoutWithEveryDeclaredValue()
    {
        using var folder = new TestFolder();
        TimingReportStamp At(int h, int m, int s, int tenThousandths = 0, int precision = 4)
            => new(new TimeSpan(h, m, s).Ticks + tenThousandths * 1000L, precision);
        TimingReportBib Bib(int bib, TimingReportStamp start, TimingReportStamp finish, long net) => new()
        {
            Bib = bib, AStart = start, BStart = start with { Ticks = start.Ticks + 4000 }, HandStart = start with { Ticks = start.Ticks - 790000, Precision = 2 },
            AFinish = finish, BFinish = finish with { Ticks = finish.Ticks + 11000 }, HandFinish = finish with { Ticks = finish.Ticks - 258000, Precision = 2 }, NetHundredths = net
        };
        var draft = new TimingReportDraft
        {
            Header = new(2027, "0593", "FIN", "SL", "FIS", "M", "", "Synthetic Fell", s_date),
            TechnicalDelegate = new("Alex", "Example", "DEN", Number: "718"),
            Defaults = new TimingReportDefaults
            {
                TimerA = new("ALGE", "Timy3 WP", "SYN-A-001", "ALG.090.14"), TimerB = new("ALGE", "TdC 8001", "SYN-B-002", "ALG.003T.10"),
                StartDevice = new("ALGE", "STScM2S", "SYN-S-003", "ALG.S51.03"),
                FinishCellsA = new("ALGE", "PR1aW", "SYN-F-004", "ALG.L91.14"), FinishCellsB = new("ALGE", "PR1aW", "SYN-F-005", "ALG.L91.14"),
                ChiefOfTiming = new("Casey", "Chief", "FIN", "chief@example.invalid", "+000 0000001"),
                Timekeeper = new("Taylor", "Timer", "FIN", "timer@example.invalid", "+000 0000002", Company: "Synthetic Timing Co"),
                ConnectionA = "Cable", ConnectionB = "Radio", Voice = "Cable"
            },
            Sync = At(8, 44, 0, precision: 0), HandSync = At(8, 44, 0, precision: 0), SyncCheckA = At(8, 45, 0), SyncCheckB = At(8, 45, 0),
            Runs =
            [
                new() { Run = 1, First = Bib(31, At(10, 32, 47, 5075), At(10, 33, 34, 5104), 4700), Last = Bib(72, At(11, 0, 8, 1576), At(11, 1, 3, 1366), 5497), BestBib = 34, BestHundredths = 4606 },
                new() { Run = 2, First = Bib(72, At(12, 44, 31, 1600), At(12, 45, 25, 7370), 5457), Last = Bib(34, At(12, 57, 10, 1861), At(12, 57, 58, 988), 4791), BestBib = 39, BestHundredths = 4684,
                    AllResultsA = false, MissedA = [new(12, "Finish cell A failure", "B")], Comment = "Synthetic comment" }
            ],
            CertifyFis = true, Reviewed = true
        };
        var source = MemorySource(folder.PathFor("test.ost"), 0) with
        {
            // A configured organizer background must not appear on the FIS form.
            Settings = new(new(TemplateName: "synthetic-background.pdf", TemplatePdf: BackgroundPdf()), []),
            TimingReport = new(Guid.NewGuid(), 3, draft, s_at, "Operator", "Synthetic")
        };
        var descriptor = ReportCatalog.GetAvailableReports(source).Single(x => x.Type == PdfReportType.TimingReport);
        Assert.True((await new ReportGenerationService(new QuestPdfRenderer()).GenerateAsync(source, descriptor, _ => Task.CompletedTask, s_at)).Success);
        var path = folder.PathFor(descriptor.FileName);
        CopyQa(path, "timing-report.pdf");
        using var pdf = PdfDocument.Open(path);
        var page = Assert.Single(pdf.GetPages());
        Assert.InRange(page.Width, 594, 596); Assert.InRange(page.Height, 841, 843);
        var text = string.Concat(page.Text.Where(c => !char.IsWhiteSpace(c)));
        foreach (var expected in new[]
        {
            "Timing&DataTechnicalReportAlpine", "transmitimmediatelyonlyasXMLandNOTasPDF", "Season2027", "Codex0593", "Slalom", "Men", "12.12.26",
            "SystemATimer(atfinish)ALGETimy3WPSYN-A-001ALG.090.14", "TimerAStart(ifused)-", "PhotoFinishB-", "Videofinish-",
            "OpenSkiTimeOpenSkiTime" + ProductInfo.Version, "CableRadioCable",
            "Syncronizationtime08:44:0008:44:00", "Syncronizationconfirmation08:45:00.000008:45:00.0000",
            "1stRun", "2ndRun", "StartTODFirst10:32:47.507510:32:47.507910:32:47.42", "NetTimeSystemA/BIBFirst0:47.0031",
            "NetTimeSystemA/BIBBest0:46.06340:46.8439", "Bib12:FinishcellAfailure(timefromB)", "Syntheticcomment",
            "AlexExample(DEN)", "TDNumber718", "ChiefCasey(FIN)", "chief@example.invalid", "SyntheticTimingCo", "TimerTaylor(FIN)",
            "TimingReportversionusedOpenSkiTime" + ProductInfo.Version, "page1of1"
        })
        { Assert.Contains(expected, text, StringComparison.Ordinal); }
        // Not every run used system A, so "No" is checked rather than "Yes".
        Assert.Contains("WereallresultsfromsystemA?YesNoX", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETICORGANIZERBACKGROUND", text, StringComparison.Ordinal);
    }
    private static void AssertPdf(string path)
    {
        Assert.True(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }
    private static void CopyQa(string path, string filename)
    {
        var qa = Environment.GetEnvironmentVariable("OST_PDF_QA_DIRECTORY");
        if (string.IsNullOrWhiteSpace(qa)) { return; }
        Directory.CreateDirectory(qa);
        File.Copy(path, Path.Combine(qa, filename), overwrite: true);
    }
    private static PdfReportSource MemorySource(string path, int count)
    {
        var competition = new CompetitionDetails(Guid.NewGuid(), Competition);
        var series = new SeriesDetails(Guid.NewGuid(), Series, 1, [competition]);
        var entries = Enumerable.Range(1, count).Select(i => new CompetitorDetails(new Guid(i, 0, 0, new byte[8]),
            new("RACER" + i.ToString("D4", CultureInfo.InvariantCulture), "Synthetic", i % 2 == 0 ? 2000 : 2010, i.ToString(CultureInfo.InvariantCulture),
                i % 3 == 0 ? null : i % 3 == 1 ? "FIN" : "SWE", "Synthetic club", Gender.Male))).ToArray();
        var desk = new CompetitorDeskDetails(series.Id, 1, entries, entries.Take(count / 2).Select(x => new ParticipationDetails(x.Id, competition.Id, true, null, null)).ToArray(),
            [new(Guid.NewGuid(), new("Senior", 1900, 2005, null, 0)), new(Guid.NewGuid(), new("Young", 2006, 2026, null, 1))]);
        return new(path, series, competition, desk, new(new(), []), [], [], RaceInformation.Empty(Competition), null, "version-1");
    }
    private sealed class FailingRenderer(PdfReportType? failType = null) : IReportPdfRenderer
    {
        public void Render(PdfReportData data, string destination)
        {
            if (failType is null || data.Descriptor.Type == failType)
            { File.WriteAllText(destination, "partial file"); throw new IOException("Synthetic render failure"); }
            new QuestPdfRenderer().Render(data, destination);
        }
    }
    private sealed class ConcurrencyRenderer : IReportPdfRenderer
    {
        private int _concurrent;
        public int MaximumConcurrent { get; private set; }
        public void Render(PdfReportData data, string destination)
        {
            var active = Interlocked.Increment(ref _concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            try { new QuestPdfRenderer().Render(data, destination); }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
    }
    private sealed class BlockingFailureRenderer : IReportPdfRenderer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public void Render(PdfReportData data, string destination)
        {
            Started.SetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException("Test renderer was not released."); }
            File.WriteAllText(destination, "partial synthetic PDF");
            throw new IOException("Synthetic rendering failure during active capture");
        }
    }
    private sealed class TestFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "OpenSkiTime-PdfTests-" + Guid.NewGuid().ToString("N"));
        public TestFolder() { Directory.CreateDirectory(_root); }
        public string PathFor(string name) => Path.Combine(_root, name);
        public void Dispose() { Directory.Delete(_root, recursive: true); }
    }
}

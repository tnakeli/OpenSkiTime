using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

public enum PdfReportType
{
    EventSeriesEntries, EventSeriesEntriesByNation, EventSeriesEntriesByCategory,
    CompetitionEntries, CompetitionEntriesByNation, CompetitionEntriesByCategory,
    StartList, RefereeReport, PenaltyCalculation, OfficialResults, TimingReport
}
public enum PdfReportStatus { NotGenerated, Generated, Outdated }
public enum PrintPageSize { A4 }
public sealed record EventPrintProfile(PrintPageSize PageSize = PrintPageSize.A4,
    decimal TopMm = 15, decimal BottomMm = 15, decimal LeftMm = 12, decimal RightMm = 12,
    string? TemplateName = null, byte[]? TemplatePdf = null)
{
    public void Validate()
    {
        if (PageSize != PrintPageSize.A4 || new[] { TopMm, BottomMm, LeftMm, RightMm }.Any(x => x < 5 || x > 80)
            || TopMm + BottomMm > 140 || LeftMm + RightMm > 100)
        { throw new DomainValidationException("Use A4 margins between 5 and 80 mm, leaving at least 110 mm of width and 157 mm of height."); }
        if (TemplatePdf is { Length: > 20_000_000 }) { throw new DomainValidationException("The PDF background must be at most 20 MB."); }
        if (TemplateName is not null && (TemplateName.Contains('/') || TemplateName.Contains('\\')))
        { throw new DomainValidationException("Store only the template filename, not a machine path."); }
    }
}
public sealed record GeneratedPdf(string ReportId, string FileName, DateTimeOffset GeneratedAt,
    string SourceVersion, string PdfHash);
public sealed record PdfFactorySettings(EventPrintProfile Profile, IReadOnlyList<GeneratedPdf> Reports);
public interface IPdfFactoryStore
{
    Task<PdfFactorySettings> ReadPdfFactoryAsync(CancellationToken ct = default);
    Task SavePrintProfileAsync(EventPrintProfile profile, CancellationToken ct = default);
    Task SaveGeneratedPdfAsync(GeneratedPdf report, CancellationToken ct = default);
}
public sealed partial class SeriesWorkspace
{
    private static IPdfFactoryStore PdfStore(ISeriesFileSession s) => s as IPdfFactoryStore
        ?? throw new SeriesFileException("PDF Factory storage is unavailable.");
    public Task<PdfFactorySettings> ReadPdfFactoryAsync(CancellationToken ct = default)
        => WithSessionAsync(s => PdfStore(s).ReadPdfFactoryAsync(ct), ct);
    public Task SavePrintProfileAsync(EventPrintProfile profile, CancellationToken ct = default)
        => WithSessionAsync(async s => { await PdfStore(s).SavePrintProfileAsync(profile, ct); return true; }, ct);
    public Task SaveGeneratedPdfAsync(GeneratedPdf report, CancellationToken ct = default)
        => WithSessionAsync(async s => { await PdfStore(s).SaveGeneratedPdfAsync(report, ct); return true; }, ct);
    public Task SaveGeneratedPdfForFileAsync(string filePath, GeneratedPdf report, CancellationToken ct = default)
        => WithSessionAsync(async s =>
        {
            if (s.FilePath != filePath) { throw new SeriesConflictException(); }
            await PdfStore(s).SaveGeneratedPdfAsync(report, ct); return true;
        }, ct);
}
public sealed record PdfReportDescriptor(string Id, PdfReportType Type, string DisplayName,
    Guid? CompetitionId, int? RunNumber, string FileName, bool CanGenerate, string? UnavailableReason);
public sealed record EntryReportRow(Guid Id, CompetitorValues Athlete, string Category, int? Bib = null,
    int? Position = null, decimal? Points = null);
public sealed record ReportGroup(string Label, IReadOnlyList<EntryReportRow> Entries);
public sealed record RunReportData(StartListRevision List, TimingSnapshot Timing, TimingReplayData Source);
public sealed record FinalReportData(FisRaceResult Race, FisPenaltyResult? Penalty, ApprovedResult? Approval, string? PenaltyUnavailableReason)
{
    public bool Approved => Approval is not null;
}
public sealed record PdfReportSource(string FilePath, SeriesDetails Series, CompetitionDetails? Competition,
    CompetitorDeskDetails Desk, PdfFactorySettings Settings, IReadOnlyList<RunReportData> Runs,
    IReadOnlyList<FinalReportData> Finals, RaceInformation? Information, SavedTimingReport? TimingReport,
    string SourceVersion);
public sealed record PdfReportData(PdfReportDescriptor Descriptor, PdfReportSource Source,
    IReadOnlyList<ReportGroup> Groups, DateTimeOffset GeneratedAt);

public static class ReportFileNameBuilder
{
    public static string Build(string shortName, DateOnly date, PdfReportType type, int? run = null)
    {
        ArgumentNullException.ThrowIfNull(shortName);
        var suffix = type switch
        {
            PdfReportType.EventSeriesEntriesByNation => "EventSeriesEntries_ByNation",
            PdfReportType.EventSeriesEntriesByCategory => "EventSeriesEntries_ByCategory",
            PdfReportType.CompetitionEntries => "Entries",
            PdfReportType.CompetitionEntriesByNation => "Entries_ByNation",
            PdfReportType.CompetitionEntriesByCategory => "Entries_ByCategory",
            PdfReportType.StartList or PdfReportType.RefereeReport => type + "_Run" + (run ?? throw new ArgumentException("Run is required.", nameof(run))).ToString(CultureInfo.InvariantCulture),
            _ => type.ToString()
        };
        var clean = new StringBuilder();
        foreach (var c in shortName.Normalize(NormalizationForm.FormC))
        {
            var value = char.IsWhiteSpace(c) || char.IsControl(c) || "\\/:*?\"<>|".Contains(c) ? '_' : c;
            if (value != '_' || clean.Length == 0 || clean[^1] != '_') { clean.Append(value); }
        }
        var name = clean.ToString().Trim('_', '.', ' ');
        if (name.Length == 0) { name = "Event"; }
        if (name.Length > 100) { name = name[..100].TrimEnd('.', ' '); }
        return name + "_" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "_" + suffix + ".pdf";
    }
}

public static class ReportCatalog
{
    public static IReadOnlyList<PdfReportDescriptor> GetAvailableReports(PdfReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new List<PdfReportDescriptor>();
        void Add(PdfReportType type, string name, int? run = null, string? reason = null)
        {
            var seriesScope = type <= PdfReportType.EventSeriesEntriesByCategory;
            var competition = seriesScope ? null : source.Competition;
            reason ??= string.IsNullOrWhiteSpace(source.FilePath) ? "Save the event series as an .ost file first." : null;
            var shortName = competition?.Values.ShortLabel ?? source.Series.Values.Name;
            if (competition is not null && source.Series.Competitions.Count(c => c.Values.Date == competition.Values.Date
                && StringComparer.OrdinalIgnoreCase.Equals(ReportFileNameBuilder.Build(c.Values.ShortLabel, c.Values.Date, type, run), ReportFileNameBuilder.Build(shortName, competition.Values.Date, type, run))) > 1)
            { shortName += "_" + competition.Id.ToString("N"); }
            result.Add(new($"{(seriesScope ? source.Series.Id : competition!.Id):N}/{type}/{run ?? 0}", type, name,
                competition?.Id, run, ReportFileNameBuilder.Build(shortName,
                    competition?.Values.Date ?? source.Series.Values.StartDate, type, run), reason is null, reason));
        }
        Add(PdfReportType.EventSeriesEntries, "Event Series Entries");
        Add(PdfReportType.EventSeriesEntriesByNation, "Event Series Entries by Nation");
        Add(PdfReportType.EventSeriesEntriesByCategory, "Event Series Entries by Category");
        if (source.Competition is not { } race) { return result; }
        Add(PdfReportType.CompetitionEntries, "Competition Entries");
        Add(PdfReportType.CompetitionEntriesByNation, "Competition Entries by Nation");
        Add(PdfReportType.CompetitionEntriesByCategory, "Competition Entries by Category");
        foreach (var run in Enumerable.Range(1, race.Values.RunCount))
        {
            Add(PdfReportType.StartList, $"Start List - Run {run}", run,
                source.Runs.Any(x => x.List.Plan.RunNumber == run && x.List.Plan.Entries.Count > 0) ? null : "Save a start list for this run first.");
            Add(PdfReportType.RefereeReport, $"Referee Report - Run {run}", run);
        }
        var finalCount = source.Runs.Count(x => x.List.Plan.RunNumber == 1);
        var completeFinals = finalCount > 0 && source.Finals.Count == finalCount;
        Add(PdfReportType.PenaltyCalculation, "Penalty Calculation", reason: completeFinals && source.Finals.All(x => x.Penalty is not null)
            ? null : (source.Finals.Count > 0 ? source.Finals[0].PenaltyUnavailableReason : null) ?? "Complete supported FIS results and review the penalty rule profile first.");
        Add(PdfReportType.OfficialResults, "Official Results", reason: completeFinals && source.Finals.All(x => x.Approved)
            ? null : "Approve current complete FIS results in Results first. The existing result engine supports one or two runs.");
        Add(PdfReportType.TimingReport, "Timing Report", reason: source.TimingReport is not null || source.Runs.Any(x => x.Source.Sessions.Count > 0)
            ? null : "Save timing report metadata or capture timing first.");
        return result;
    }
}

public static class EntryReportBuilder
{
    public static IReadOnlyList<ReportGroup> Build(PdfReportSource source, PdfReportDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(descriptor);
        var entries = descriptor.Type == PdfReportType.StartList
            ? source.Runs.Where(x => x.List.Plan.RunNumber == descriptor.RunNumber).OrderBy(x => x.List.Plan.Gender)
                .SelectMany(x => x.List.Plan.Entries.OrderBy(e => e.Position).Select(e => new EntryReportRow(e.Entrant.CompetitorId,
                    e.Entrant.Athlete, CategoryResolver.Resolve(e.Entrant.Athlete, source.Desk.Categories), e.Bib, e.Position, e.Entrant.Points))).ToArray()
            : source.Desk.Competitors.Where(x => descriptor.CompetitionId is null || source.Desk.Participations.Any(p => p.CompetitorId == x.Id
                && p.CompetitionId == descriptor.CompetitionId && p.Participates))
                .OrderBy(x => x.Values.Surname, StringComparer.Ordinal).ThenBy(x => x.Values.FirstName, StringComparer.Ordinal).ThenBy(x => x.Id)
                .Select(x => new EntryReportRow(x.Id, x.Values, CategoryResolver.Resolve(x.Values, source.Desk.Categories),
                    source.Desk.Participations.FirstOrDefault(p => p.CompetitorId == x.Id && p.CompetitionId == descriptor.CompetitionId)?.ImportedBib)).ToArray();
        Func<EntryReportRow, string>? group = descriptor.Type switch
        {
            PdfReportType.EventSeriesEntriesByNation or PdfReportType.CompetitionEntriesByNation => x => string.IsNullOrWhiteSpace(x.Athlete.Nation) ? "Nation not supplied" : x.Athlete.Nation,
            PdfReportType.EventSeriesEntriesByCategory or PdfReportType.CompetitionEntriesByCategory => x => x.Category,
            PdfReportType.StartList => x => GenderLabels.Format(x.Athlete.Gender),
            _ => null
        };
        return group is null ? [new("", entries)] : entries.GroupBy(group).OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new ReportGroup(x.Key, x.ToArray())).ToArray();
    }
}

public sealed class PdfReportSourceBuilder(ITimingDecoderFactory decoders)
{
    public async Task<PdfReportSource> BuildAsync(SeriesWorkspace workspace, Guid? competitionId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var path = workspace.FilePath ?? "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var series = await workspace.ReadAsync(ct);
            var competition = series.Competitions.FirstOrDefault(x => x.Id == competitionId);
            var desk = await workspace.ReadCompetitorDeskAsync(ct);
            var settings = await workspace.ReadPdfFactoryAsync(ct);
            var runs = new List<RunReportData>();
            var finals = new List<FinalReportData>();
            RaceInformation? information = null;
            SavedTimingReport? timingReport = null;
            if (competition is not null)
            {
                information = (await workspace.ReadRaceInformationAsync(competition.Id, ct))?.Values.WithCompetitionCourse(competition.Values)
                    ?? RaceInformation.Empty(competition.Values);
                timingReport = await workspace.ReadTimingReportAsync(competition.Id, ct);
                var lists = await workspace.ReadStartListsAsync(competition.Id, ct);
                foreach (var list in lists.Revisions.GroupBy(x => (x.Plan.Gender, x.Plan.RunNumber))
                    .Select(x => x.OrderByDescending(y => y.Revision).First()).OrderBy(x => x.Plan.Gender).ThenBy(x => x.Plan.RunNumber))
                {
                    var replay = await workspace.ReadTimingAsync(list.Id, ct);
                    runs.Add(new(list, TimingReplay.Restore(replay, decoders), replay));
                }
                var approvals = await workspace.ReadApprovedResultsAsync(competition.Id, ct);
                if (competition.Values.RaceType == RaceType.Fis)
                {
                    foreach (var first in runs.Where(x => x.List.Plan.RunNumber == 1))
                    {
                        var second = runs.FirstOrDefault(x => x.List.Plan.RunNumber == 2 && x.List.Plan.Gender == first.List.Plan.Gender);
                        try
                        {
                            var race = FisRaceResults.Assemble(first.List, first.Timing, second?.List, second?.Timing);
                            FisPenaltyResult? penalty = null;
                            string? penaltyReason = null;
                            try
                            {
                                var rules = first.List.Plan.PointsList.PenaltyRules ?? throw new DomainValidationException("Penalty rules are missing from the drawn points list.");
                                if (rules.Season != 2027) { throw new DomainValidationException("Only the reviewed 2026/27 penalty rules are supported."); }
                                var points = first.List.Plan.PointsList;
                                if (competition.Values.Date < points.ValidFrom || competition.Values.Date > points.ValidTo)
                                { throw new DomainValidationException("The drawn points list is not valid on race day."); }
                                var profile = rules.Resolve(competition.Values.Calendar?.Category ?? information.Category,
                                    competition.Values.Discipline, first.List.Plan.Gender);
                                penalty = FisPenalty.Calculate(profile, race.PenaltyCompetitors);
                            }
                            catch (DomainValidationException ex) { penaltyReason = ex.Message; }
                            var fingerprint = ResultSourceFingerprint.Create(first.Source, second?.Source);
                            var approval = approvals.Where(x => x.FirstListId == first.List.Id && x.SecondListId == second?.List.Id
                                && x.SourceFingerprint == fingerprint).OrderByDescending(x => x.Revision).FirstOrDefault();
                            // Timing approval does not approve later jury/course/category edits or a different penalty.
                            if (approval is not null && (approval.Information is null
                                || JsonSerializer.Serialize(approval.Information) != JsonSerializer.Serialize(information)
                                || approval.Information.Category != (competition.Values.Calendar?.Category ?? information.Category)
                                || penalty is not null && (approval.CalculatedPenalty != penalty.Calculated || approval.AppliedPenalty != penalty.Applied)))
                            { approval = null; }
                            finals.Add(new(race, penalty, approval, penaltyReason));
                        }
                        catch (DomainValidationException) { /* Incomplete results stay unavailable; no competing calculation. */ }
                    }
                }
            }
            var after = await workspace.ReadAsync(ct);
            if (after.Revision != series.Revision || workspace.FilePath != path) { continue; }
            var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                RendererVersion = 5, series.Revision, settings.Profile,
                Timing = runs.Select(x => ResultSourceFingerprint.Create(x.Source)).ToArray(),
                timingReport, information, Approvals = finals.Select(x => x.Approval is { } a
                    ? new { a.Id, a.Revision, a.CalculatedPenalty, a.AppliedPenalty } : null).ToArray()
            })));
            return new(path, series, competition, desk, settings, runs.ToArray(), finals.ToArray(), information, timingReport, version);
        }
        throw new SeriesConflictException();
    }
}

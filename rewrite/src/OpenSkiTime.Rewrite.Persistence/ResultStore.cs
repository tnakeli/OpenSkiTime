using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;
using System.Text.Json;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed class ApprovedResultRow
{
    public Guid Id { get; set; }
    public Guid CompetitionId { get; set; }
    public int Revision { get; set; }
    public Guid FirstListId { get; set; }
    public Guid? SecondListId { get; set; }
    public string SourceFingerprint { get; set; } = string.Empty;
    public DateTimeOffset ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
    public string XmlFileName { get; set; } = string.Empty;
    public byte[] Xml { get; set; } = [];
    public decimal CalculatedPenalty { get; set; }
    public decimal AppliedPenalty { get; set; }
    public string? InformationJson { get; set; }
}

internal sealed partial class SqliteSeriesFileSession : IResultStore
{
    public async Task<IReadOnlyList<ApprovedResult>> ReadApprovedResultsAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
        return (await db.ApprovedResults.AsNoTracking().Where(x => x.CompetitionId == competitionId)
            .OrderBy(x => x.Revision).ToArrayAsync(ct)).Select(ToApproval).ToArray();
    }

    public async Task<ApprovedResult> ApproveResultAsync(ApproveResultRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ApprovedBy))
        { throw new DomainValidationException("Name the TD or approving operator before creating official XML."); }
        if (request.Xml.Length == 0 || !request.XmlFileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        { throw new DomainValidationException("Create and review the FIS XML before approving results."); }
        try
        {
            var document = XDocument.Load(new MemoryStream(request.Xml));
            if (document.Root?.Name != "Fisresults" || document.Root.Element("AL_race")?.Element("AL_classified") is null
                || document.Descendants("Appliedpenalty").SingleOrDefault()?.Value
                    != request.AppliedPenalty.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            { throw new DomainValidationException("The FIS XML does not match the approved penalty and result structure."); }
        }
        catch (System.Xml.XmlException ex) { throw new DomainValidationException("The generated FIS XML is invalid: " + ex.Message); }
        var result = await WriteDeskAsync(async (db, series) =>
        {
            var competition = await db.Competitions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.CompetitionId, ct)
                ?? throw new DomainValidationException("The competition no longer exists.");
            if (competition.RunCount is not (1 or 2)
                || (competition.RunCount == 2) != (request.SecondListId is not null))
            { throw new DomainValidationException("Include the final run before approving results."); }
            request.Information?.Validate(competition.RunCount);
            var first = await ReadTimingAsync(request.FirstListId, ct);
            if (first.List.Plan.CompetitionId != request.CompetitionId || first.List.Plan.RunNumber != 1)
            { throw new DomainValidationException("Choose this competition's Run 1 start list."); }
            TimingReplayData? second = request.SecondListId is { } id ? await ReadTimingAsync(id, ct) : null;
            if (second is not null && (second.List.Plan.CompetitionId != request.CompetitionId || second.List.Plan.RunNumber != 2))
            { throw new DomainValidationException("Choose this competition's Run 2 start list."); }
            if (first.Sessions.Count == 0 || first.Sessions.Any(x => x.StoppedAt is null)
                || second is { Sessions.Count: 0 } || second?.Sessions.Any(x => x.StoppedAt is null) == true)
            { throw new DomainValidationException("Stop and complete all timing capture before approving results."); }
            var listIds = second is null ? new[] { request.FirstListId } : new[] { request.FirstListId, second.List.Id };
            if (await db.Captures.AnyAsync(x => listIds.Contains(x.ListId) && x.StoppedAt == null, ct))
            { throw new DomainValidationException("Stop timing capture before approving results."); }
            if (await db.StartLists.AnyAsync(x => listIds.Contains(x.Id) && db.StartLists.Any(y => y.RunId == x.RunId && y.Revision > x.Revision), ct))
            { throw new DomainValidationException("A newer start-list revision exists. Review its results first."); }
            if (request.SourceFingerprint != ResultSourceFingerprint.Create(first, second))
            { throw new DomainValidationException("Timing or start-list data changed since review. Reload results before approving."); }
            if (request.Information is { } information)
            {
                var informationRevision = (await db.Set<RaceInformationRow>().Where(x => x.CompetitionId == request.CompetitionId)
                    .MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1;
                db.Add(new RaceInformationRow { CompetitionId = request.CompetitionId, Revision = informationRevision,
                    ValuesJson = JsonSerializer.Serialize(information), SavedAt = DateTimeOffset.UtcNow });
            }
            var row = new ApprovedResultRow
            {
                Id = Guid.NewGuid(), CompetitionId = request.CompetitionId,
                Revision = (await db.ApprovedResults.Where(x => x.CompetitionId == request.CompetitionId)
                    .MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1,
                FirstListId = first.List.Id, SecondListId = second?.List.Id,
                SourceFingerprint = request.SourceFingerprint, ApprovedAt = DateTimeOffset.UtcNow,
                ApprovedBy = request.ApprovedBy.Trim(), XmlFileName = request.XmlFileName,
                Xml = request.Xml, CalculatedPenalty = request.CalculatedPenalty, AppliedPenalty = request.AppliedPenalty,
                InformationJson = request.Information is null ? null : JsonSerializer.Serialize(request.Information)
            };
            db.ApprovedResults.Add(row);
            return ToApproval(row);
        }, request.ExpectedSeriesRevision, ct);
        return result.Value;
    }

    private static ApprovedResult ToApproval(ApprovedResultRow x) => new(x.Id, x.CompetitionId, x.Revision,
        x.FirstListId, x.SecondListId, x.SourceFingerprint, x.ApprovedAt, x.ApprovedBy,
        x.XmlFileName, x.Xml, x.CalculatedPenalty, x.AppliedPenalty,
        x.InformationJson is null ? null : JsonSerializer.Deserialize<RaceInformation>(x.InformationJson));
}

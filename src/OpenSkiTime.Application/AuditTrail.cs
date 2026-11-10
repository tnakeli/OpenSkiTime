namespace OpenSkiTime.Application;

public enum AuditArea { Events, RawTiming, CurrentState }

public sealed record AuditReference(string Source, long RowId, string? StoredKey = null);
public sealed record AuditCursor(long TimeTicks, string Source, long RowId);
public sealed record AuditQuery(AuditArea Area = AuditArea.Events, string? Source = null,
    Guid? CompetitionId = null, int? Run = null, string Search = "", string Operator = "", int? Bib = null,
    DateTimeOffset? From = null, DateTimeOffset? Until = null, AuditCursor? After = null, int PageSize = 100);
public sealed record AuditRecord(AuditReference Reference, DateTimeOffset? At, string Competition,
    int? Run, string Operator, int? Bib, string Summary, string Provenance, long TimeTicks,
    string Surname = "", string FirstName = "")
{
    public string Name => (Surname + " " + FirstName).Trim();
}
public sealed record AuditPage(IReadOnlyList<AuditRecord> Records, AuditCursor? Next);
public sealed record AuditLink(string Label, AuditReference Reference);
public sealed record AuditDetail(string Text, IReadOnlyList<AuditLink> Links);
public sealed record AuditTableCount(string Source, long Count);
public sealed record AuditSeriesSummary(string Format, long Revision, IReadOnlyList<AuditTableCount> Tables,
    [property: System.Text.Json.Serialization.JsonIgnore] Guid SeriesId = default);

/// <summary>Inspection reads never acquire the race write gate or change a series file.</summary>
public interface IAuditTrailReader
{
    IReadOnlyList<string> Sources(AuditArea area);
    Task<AuditPage> ReadAsync(string path, AuditQuery query, CancellationToken ct = default);
    Task<AuditDetail> ReadDetailAsync(string path, AuditReference reference, CancellationToken ct = default);
    Task<AuditSeriesSummary> ReadSummaryAsync(string path, CancellationToken ct = default);
    // The implementation must exclude active capture, including capture in another process.
    Task CreateSupportSnapshotAsync(string path, string destination, CancellationToken ct = default);
}

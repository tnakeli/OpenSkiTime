using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenSkiTime.LiveTiming;

public enum LiveStatus { Ready, OnCourse, Finished, DNS, DNF, DSQ, NPS, Review }
public enum PublisherState { Stopped, Starting, Running, Reconnecting, Error }
public enum PublisherKind { Local, Cloud, FisTcp, FisHttps }
public enum LiveEventKind { CompetitorStarted, IntermediateTime, CompetitorFinished, DNS, DNF, DSQ, ResultUpdated }
public sealed record LiveCompetitor(int Bib, string LastName, string FirstName, string Nation, string Club, string FisCode);
// Source timestamps remain date-bearing .NET ticks on the authoritative integer 100 ns scale.
// Elapsed hundredths are already calculated by timing. At/StartedAt explicitly carry the clock's UTC offset.
public sealed record LiveSplit(int Number, long Hundredths, DateTimeOffset At,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long? SourceTicks = null);
public sealed record LiveResult(int Bib, LiveStatus Status, long? Hundredths, int? Rank,
    long? Difference, DateTimeOffset At, DateTimeOffset? StartedAt = null,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long? StartSourceTicks = null,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long? FinishSourceTicks = null, LiveSplit[]? Intermediates = null)
{
    public LiveSplit[] Splits => Intermediates ?? [];
    // From Run 2 on: the combined time of all runs so far, its rank (equal totals share a rank) and the gap to the best
    // total, as calculated by timing. Null in Run 1 and for a racer without a finished time in every run so far.
    public long? TotalHundredths { get; init; }
    public int? TotalRank { get; init; }
    public long? TotalDifference { get; init; }
}
public sealed record LiveRun(int Number, DateTimeOffset ListCreatedAt, int[] StartOrder, LiveResult[] Results);
public sealed record LiveCompetition(string Name, string Place, string Discipline, DateOnly Date,
    bool IsFis, string Codex, string Gender, string Category, int IntermediateCount, string Slope = "");
public sealed record LiveSnapshot(long Version, LiveCompetition Competition, LiveCompetitor[] Competitors,
    int CurrentRun, LiveRun[] Runs, DateTimeOffset UpdatedAt, bool Paused = false)
{
    public void Validate(int maxCompetitors = 2000)
    {
        if (Version < 0 || Competition is null || Competitors is null || Runs is null
            || string.IsNullOrWhiteSpace(Competition.Name) || Competition.IntermediateCount is < 0 or > 20
            || Competitors.Length > maxCompetitors || Runs.Length is < 1 or > 9 || CurrentRun is < 1 or > 9
            || Runs.Any(x => x is null) || UpdatedAt == default
            || !Runs.Any(x => x.Number == CurrentRun) || Runs.Select(x => x.Number).Distinct().Count() != Runs.Length)
        { throw new LiveValidationException("Invalid live competition snapshot."); }
        foreach (var text in new[] { Competition.Name, Competition.Place, Competition.Discipline, Competition.Codex,
                     Competition.Gender, Competition.Category, Competition.Slope }) { ValidateText(text); }
        if (Competitors.Any(x => x is null || x.Bib is < 1 or > 99999)
            || Competitors.Select(x => x.Bib).Distinct().Count() != Competitors.Length)
        { throw new LiveValidationException("Competitor bibs must be unique and positive."); }
        var bibs = Competitors.Select(x => x.Bib).ToHashSet();
        foreach (var c in Competitors)
        { foreach (var text in new[] { c.LastName, c.FirstName, c.Nation, c.Club, c.FisCode }) { ValidateText(text); } }
        foreach (var run in Runs)
        {
            if (run.Number is < 1 or > 9 || run.StartOrder is null || run.Results is null
                || run.ListCreatedAt == default || run.Results.Any(x => x is null)
                || run.StartOrder.Length > maxCompetitors || run.Results.Length > maxCompetitors
                || run.StartOrder.Distinct().Count() != run.StartOrder.Length || run.StartOrder.Any(x => !bibs.Contains(x))
                || run.Results.Select(x => x.Bib).Distinct().Count() != run.Results.Length
                || run.Results.Any(x => !run.StartOrder.Contains(x.Bib)))
            { throw new LiveValidationException("Invalid run or start order."); }
            foreach (var result in run.Results) { ValidateResult(result, Competition.IntermediateCount); }
        }
    }
    public static void ValidateResult(LiveResult r, int intermediateCount)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!Enum.IsDefined(r.Status) || r.Bib is < 1 or > 99999 || r.Hundredths < 0 || r.Difference < 0 || r.At == default
            || r.Rank < 1 || (r.Status == LiveStatus.Finished && r.Hundredths is null)
            || r.TotalHundredths < 0 || r.TotalDifference < 0 || r.TotalRank < 1
            || ((r.TotalHundredths is null) != (r.TotalRank is null) || (r.TotalHundredths is null) != (r.TotalDifference is null))
            || (r.TotalHundredths is not null && (r.Status != LiveStatus.Finished || r.TotalHundredths < r.Hundredths))
            || r.StartSourceTicks < 0 || r.FinishSourceTicks < 0 || r.StartSourceTicks > DateTime.MaxValue.Ticks || r.FinishSourceTicks > DateTime.MaxValue.Ticks || r.Splits.Length > intermediateCount
            || r.Splits.Any(x => x is null || x.Number < 1 || x.Number > intermediateCount || x.Hundredths < 0 || x.SourceTicks < 0 || x.SourceTicks > DateTime.MaxValue.Ticks || x.At == default)
            || r.Splits.Select(x => x.Number).Distinct().Count() != r.Splits.Length)
        { throw new LiveValidationException("Invalid live result."); }
    }
    private static void ValidateText(string text)
    { if (text is null || text.Length > 320 || text.Any(char.IsControl)) { throw new LiveValidationException("Invalid live text."); } }
}
public sealed class LiveValidationException(string message) : Exception(message);
public sealed record LiveEvent(long Version, int Run, LiveEventKind Kind, LiveResult Result, DateTimeOffset At);
public sealed record LiveSession(Guid SessionId, string PublisherToken, DateTimeOffset ExpiresAt, string PublicUrl);
/// <summary>Public listing entry for an active session that has published state.</summary>
public sealed record LiveSessionSummary(Guid SessionId, string Name, string Place, string Discipline, DateOnly Date,
    string Gender, string Category, bool IsFis, string Codex, DateTimeOffset UpdatedAt, bool Paused);
public sealed record PublisherHealth(PublisherState State, string Endpoint = "", DateTimeOffset? LastConnected = null,
    DateTimeOffset? LastSuccessfulPublish = null, DateTimeOffset? LastEvent = null, string? Error = null,
    string? PublicUrl = null, DateTimeOffset? ExpiresAt = null);
public sealed record PublisherOptions(PublisherKind Kind, string Endpoint, string FisPassword = "", int TcpPort = 1550,
    string? LocalServerAssembly = null, LiveSession? ResumeSession = null, string? LocalSigningKey = null, string? PublisherKey = null);
public sealed record WorkerInput(string Command, PublisherOptions? Options = null, LiveSnapshot? Snapshot = null);
// Private current-user IPC only. Never serialize this envelope into diagnostic logs.
public sealed record WorkerOutput(PublisherHealth Health, LiveSession? Session);
/// <summary>
/// Publisher wire protocol version, independent of application and server release versions.
/// Increment only for an incompatible change to the publishing API or its payloads.
/// </summary>
public static class LiveProtocol
{
    public const int Version = 1;
    public const string Header = "X-OpenSkiTime-Live-Protocol";
}
public static class LiveJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32 };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
/// <summary>Publisher keys authorize live session creation. Servers store only the SHA-256 hash.</summary>
public static class LivePublisherKey
{
    public const string Prefix = "ost_pk_";
    public static string Generate() => Prefix + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
    }
}

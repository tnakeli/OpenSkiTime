using System.Globalization;
using System.Text.Json;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// Read model of one Vola Ski-Alp-Pro event (EventNNN.scdb and, for start lists and times, EventNNNEx.scdb). Values are the
// source's own; interpretation beyond the file format happens in SkiAlpProImport. See docs/ski-alp-pro-import.md.
public sealed record SkiAlpProCompetitor(int Bib, string Surname, string FirstName, string Code, int? BirthYear, string Nation,
    string Club, Gender? Gender, string PointsList, decimal? Dh, decimal? Sg, decimal? Gs, decimal? Sl, decimal? Ac);

/// <summary>A classified result of one run. Status 0 is a time (or no result yet), 1 DNS, 2 DNF, 3 DSQ.</summary>
public sealed record SkiAlpProResult(int Bib, int Status, long TimeMilliseconds, long Penalty, int Gate, string Reason)
{
    public bool HasTime => Status == 0 && TimeMilliseconds > 0;
    public TimingStatus? Classification => Status switch { 1 => TimingStatus.DNS, 2 => TimingStatus.DNF, 3 => TimingStatus.DSQ, _ => null };
    public bool IsUnknown => Status is < 0 or > 3;
}

/// <summary>A recorded impulse. Position: 0 start, 1 finish, 2 and up intermediates. Only status 0 rows count for a result.</summary>
public sealed record SkiAlpProRecord(int Position, int Line, int Bib, long Status, string Hour1, string Hour2);

public sealed record SkiAlpProRun(int Number, IReadOnlyList<int> StartBibs, IReadOnlyList<SkiAlpProResult> Results,
    IReadOnlyList<SkiAlpProRecord> Records)
{
    public static SkiAlpProRun Empty(int number) => new(number, [], [], []);
}

public sealed record SkiAlpProCourse(string Name, int? Start, int? Finish, int? Drop, int? Length, string Homologation);

public sealed record SkiAlpProRace(string FileName, string FileHash, string? TimesFileName, string? TimesFileHash,
    string SoftwareVersion, string CodexText, string Title, string Subtitle, DateOnly Date, Discipline Discipline, string Location,
    string Nation, string Category, int RunCount, int IntermediateCount, int TimeAccuracy, string PointsList, SkiAlpProCourse Course,
    CompetitionTechnicalDelegateInfo? TechnicalDelegate, IReadOnlyList<SkiAlpProCompetitor> Competitors,
    IReadOnlyList<SkiAlpProRun> Runs, IReadOnlyList<string> Notices)
{
    /// <summary>The four-digit FIS codex of "FIN0592.718", or the text as it is when it is not a FIS codex.</summary>
    public string Codex => SkiAlpProCodex.Parse(CodexText)?.Codex ?? CodexText.Trim();
    public SkiAlpProRun Run(int number) => Runs.FirstOrDefault(x => x.Number == number) ?? SkiAlpProRun.Empty(number);
    public string SourceText => $"{FileName} SHA256 {FileHash}" + (TimesFileName is null ? "" : $", {TimesFileName} SHA256 {TimesFileHash}");
}

public sealed record SkiAlpProCodex(string Nation, string Codex, string TdNumber)
{
    public static SkiAlpProCodex? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Trim().ToUpperInvariant().Split('.', 2);
        var body = parts[0];
        if (body.Length != 7 || !body[..3].All(char.IsAsciiLetterUpper) || !body[3..].All(char.IsAsciiDigit)) { return null; }
        var td = parts.Length == 2 && parts[1].All(char.IsAsciiDigit) ? parts[1] : "";
        return new(body[..3], body[3..], td);
    }

    /// <summary>Two codices are the same when they match without leading zeros and letter case.</summary>
    public static bool Same(string? a, string? b)
    {
        static string Key(string? value) => (value ?? "").Trim().TrimStart('0').ToUpperInvariant();
        return Key(a).Length > 0 && Key(a) == Key(b);
    }
}

/// <summary>What one run of the file holds. Everything is zero when the file has no data for the run.</summary>
public sealed record SkiAlpProRunStatistics(int Run, int Starters, int Finished, int Dns, int Dnf, int Dsq, int Other, int NoResult, int Impulses)
{
    public string Label => "Run " + Run.ToString(CultureInfo.InvariantCulture);

    public static SkiAlpProRunStatistics Of(SkiAlpProRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var starters = run.StartBibs.ToHashSet();
        var results = run.Results.Where(x => starters.Contains(x.Bib)).GroupBy(x => x.Bib).Select(x => x.Last()).ToArray();
        int Count(int status) => results.Count(x => x.Status == status);
        var finished = results.Count(x => x.HasTime);
        var other = results.Count(x => x.IsUnknown);
        return new(run.Number, starters.Count, finished, Count(1), Count(2), Count(3), other,
            starters.Count - finished - Count(1) - Count(2) - Count(3) - other, run.Records.Count(x => x.Status == 0 && x.Bib > 0));
    }
}

/// <summary>
/// The raw journal form of one Ski-Alp-Pro row: the row's own values as JSON. It is stored as a packet of the imported
/// run, so the original values stay beside the decisions made from them.
/// </summary>
public sealed record SkiAlpProPacket(string Table, int Row, int Bib, long Status, int? Position, string Hour1, string Hour2,
    int Accuracy, long? TimeMilliseconds = null, long? Penalty = null, int? Gate = null, string? Reason = null)
{
    public const string Protocol = "ski-alp-pro/v1";
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this);
    public static SkiAlpProPacket? Read(byte[] bytes) => JsonSerializer.Deserialize<SkiAlpProPacket>(bytes);
}

public sealed record SkiAlpProAthlete(Guid Id, bool IsNew, CompetitorValues Values, CompetitorFisPoints? Points, bool Entered, int? Bib);

public sealed record SkiAlpProRunCommit(Guid ListId, StartListPlan Plan, CaptureSession? Session,
    IReadOnlyList<RawTimingPacket> Packets, IReadOnlyList<TimingDecision> Decisions);

/// <summary>The complete validated import. It is applied exactly as built or not at all.</summary>
public sealed record SkiAlpProCommit(Guid SeriesId, long ExpectedRevision, Guid CompetitionId, bool IsNewCompetition,
    CompetitionValues Competition, IReadOnlyList<SkiAlpProAthlete> Athletes, FisPointsListInfo? PointsList,
    IReadOnlyList<SkiAlpProRunCommit> Runs, IReadOnlyList<string> Notices, string Operator, string Reason, DateTimeOffset At);

/// <summary>What an import into an existing competition replaces. DeviceSessions: timing captured from real devices.</summary>
public sealed record SkiAlpProReplacement(int Entries, int StartLists, int TimingSessions, int DeviceSessions, int RawPackets,
    int TimingChanges, int ApprovedResults, int TimingReports)
{
    public bool Any => Entries + StartLists + TimingSessions + RawPackets + TimingChanges + ApprovedResults + TimingReports > 0;
}

public sealed record SkiAlpProImportResult(SeriesDetails Series, string? BackupPath);

public interface ISkiAlpProImportStore
{
    Task<SkiAlpProReplacement> ReadSkiAlpProReplacementAsync(Guid competitionId, CancellationToken ct = default);
    Task<SkiAlpProImportResult> ApplySkiAlpProImportAsync(SkiAlpProCommit commit, CancellationToken ct = default);
}

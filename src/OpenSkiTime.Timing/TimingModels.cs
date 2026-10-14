using System.Globalization;
using System.Text.Json.Serialization;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Timing;

public enum ObservationKind { Impulse, DeviceCorrection, Invalid, Information }
public enum TimingStatus { Ready, OnCourse, Finished, DNS, DNF, DSQ, NPS, Review }
// ManualTime is appended so the numeric values stored in existing audit rows keep their meaning.
public enum DecisionKind { Assignment, Status, Time, StartOrder, ManualTime }

// Ticks are integer 100 ns units. Device precision is retained separately; receive time never determines race time.
public sealed record TimingObservation(string Key, Guid SessionId, long PacketSequence, string Source,
    string Fingerprint, ObservationKind Kind, int? Channel, long? DeviceTicks, int Precision,
    int? SuggestedBib, bool Manual, string ClockId, string Message)
{
    // The device channel the impulse arrived on (C0–C8), before routing to a timing position. Diagnostics only.
    public int? PhysicalChannel { get; init; }
    // The device's calendar date when the source reports one (ALGE Results). Timing never uses it; reading a race day's
    // device memory for the timing report does.
    public DateOnly? CalendarDate { get; init; }
    // A time of day typed by the operator for a missing impulse. It comes from an audited ManualTime decision, never
    // from a device; Manual is also set so the hand-clock precision rule (hundredths) applies.
    public bool ManualEntry { get; init; }
}

public sealed record TimingDecision(DecisionKind Kind, string? ObservationKey = null, Guid? CompetitorId = null,
    int? Bib = null, bool Ignored = false, TimingStatus? Status = null, long? Hundredths = null,
    string? StartOrder = null, DisqualificationDetails? Disqualification = null,
    // Omitted when null, so audit rows and approved-result fingerprints of every other decision are unchanged.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ManualTimestamp? Timestamp = null);

// An operator-entered timestamp for one timing position. DeviceTicks is the typed time of day placed on the clock of
// ReferenceKey (the newest device impulse when it was entered) so it can be combined with that clock's impulses;
// without device input it uses its own run clock. Everything is resolved once at entry and replayed verbatim.
public sealed record ManualTimestamp(int Channel, long DeviceTicks, int Precision, string ClockId, string? ReferenceKey = null)
{
    public const string KeyPrefix = "manual:";

    public static bool IsKey(string? key) => key is not null && key.Length == KeyPrefix.Length + 32
        && key.StartsWith(KeyPrefix, StringComparison.Ordinal) && Guid.TryParseExact(key[KeyPrefix.Length..], "N", out _);

    public bool IsValid => Channel is >= 0 and <= 21 && DeviceTicks > 0 && DeviceTicks < DateTime.MaxValue.Ticks
        && Precision is >= 2 and <= 7 && !string.IsNullOrWhiteSpace(ClockId) && ClockId.Length <= 200
        && ReferenceKey is null or { Length: > 0 and <= 200 };
}

public sealed record DisqualificationDetails(int? Gate = null, string Reason = "", string Judge = "")
{
    public void Validate()
    {
        if (Gate is <= 0 or > 1000 || Reason is null || Judge is null || Reason.Length > 1000 || Judge.Length > 160)
        { throw new DomainValidationException("DSQ gate must be 1–1000; reason at most 1000 characters and judge at most 160."); }
        // The reason is exported in result XML, which cannot carry most control characters (a pasted Word line break is U+000B).
        if (!TextRules.IsPortable(Reason) || !TextRules.IsPortable(Judge))
        { throw new DomainValidationException("The DSQ reason or judge contains a control character that cannot be exported (for example a pasted Word line break)."); }
    }
}

public sealed record TimingAudit(long Id, Guid ListId, DateTimeOffset At, string Operator, string Reason,
    TimingDecision Before, TimingDecision After, long? ReversesId = null);

/// <summary>
/// A forerunner of a run. Forerunners go through timing (at start, on course, finished) before the competitors but are
/// never ranked and never part of competitor results. Impulses are assigned to <see cref="Key"/>, a number above every
/// bib that is derived from the forerunner's identifier, so editing names or order never moves an assignment.
/// </summary>
public sealed record TimingForerunner(string Letter, string FirstName, string LastName, string Nation)
{
    public const int FirstKey = 100_000;
    public int Key => KeyOf(Letter);

    // Deterministic FNV-1a of the upper-case identifier mapped above the bib range (bibs are 1–99999).
    public static int KeyOf(string letter)
    {
        ArgumentNullException.ThrowIfNull(letter);
        var hash = 2166136261u;
        foreach (var c in letter.Trim().ToUpperInvariant()) { hash = unchecked((hash ^ c) * 16777619u); }
        return FirstKey + (int)(hash % 899_999u);
    }

    public static bool IsKey(int bib) => bib >= FirstKey;

    private static readonly byte[] s_idTail = [0x46, 0x4F, 0x52, 0x45, 0x52, 0x55, 0x4E, 0x00]; // ASCII "FORERUN" + 0

    public static Guid IdOf(int key) => new(key, 0, 0, s_idTail);

    // The forerunner key of the competitor id timing gives a forerunner; null for every other id.
    public static int? KeyOfId(Guid id)
    {
        var bytes = id.ToByteArray();
        var key = BitConverter.ToInt32(bytes, 0);
        return bytes.AsSpan(8).SequenceEqual(s_idTail) && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 && IsKey(key) ? key : null;
    }

    public static void Validate(IReadOnlyList<TimingForerunner> forerunners)
    {
        ArgumentNullException.ThrowIfNull(forerunners);
        if (forerunners.Any(x => x.Letter.Length is < 1 or > 16 || !x.Letter.All(char.IsAsciiLetterOrDigit))
            || forerunners.Select(x => x.Letter.ToUpperInvariant()).Distinct().Count() != forerunners.Count)
        { throw new DomainValidationException("Forerunners need a unique identifier of 1–16 letters or digits."); }
        if (forerunners.Select(x => x.Key).Distinct().Count() != forerunners.Count)
        { throw new DomainValidationException("Two forerunner identifiers clash in timing. Choose a different identifier for one of them."); }
    }

    internal StartListEntry Entry(int position) => new(position, Key,
        new DrawEntrant(IdOf(Key),
            new CompetitorValues(LastName, FirstName, null, null, Nation, null, null), null), "Forerunner");
}

public sealed record TimingResult(StartListEntry Entry, TimingStatus Status, long? Hundredths,
    int? Rank, string? StartKey, string? FinishKey, string Detail)
{
    // A forerunner row: timed through the queues, never ranked, never in competitor results; its time is not shown.
    public bool IsForerunner { get; init; }
    public string? ForerunnerLetter { get; init; }
    public IReadOnlyList<TimingSplit> Splits { get; init; } = [];
    public DisqualificationDetails? Disqualification { get; init; }
    // The assigned start/finish is an operator-entered time (shown as "m" next to the time).
    public bool StartManual { get; init; }
    public bool FinishManual { get; init; }
    public int Bib => Entry.Bib;
    public Guid CompetitorId => Entry.Entrant.CompetitorId;
    public string Name => Entry.Entrant.Athlete.Surname + " " + Entry.Entrant.Athlete.FirstName;
    public string Time => TimingTime.Format(Hundredths);
}

public sealed record TimingSplit(int Number, string? ObservationKey, long? Hundredths, string Detail)
{
    // The intermediate impulse, or the start it is measured from, is an operator-entered time.
    public bool Manual { get; init; }
    public string Time => TimingTime.Format(Hundredths);
}

public enum ElapsedProblem { None, DifferentClock, NotAfterStart, OverTwoHours, LowPrecision }

public readonly record struct TimingElapsed(long? Hundredths, ElapsedProblem Problem);

// Hundredths is null whenever Problem is set; a preview never invents a time.
public sealed record AssignmentPreview(int Bib, int? Channel, long? Hundredths, string? Problem);

public sealed record ObservationReview(TimingObservation Observation, int? Bib, bool Ignored,
    string? DuplicateOf, string State);

public sealed record TimingSnapshot(Guid ListId, long AuditVersion, IReadOnlyList<TimingResult> Results,
    IReadOnlyList<ObservationReview> Observations, IReadOnlyList<TimingAudit> Audit)
{
    public IReadOnlyList<int> StartOrder { get; init; } = [];
    // Forerunner rows of this run, in their running order. Not part of Results, ranks, completion or exports.
    public IReadOnlyList<TimingResult> Forerunners { get; init; } = [];
    // Everyone timed in this run: forerunners and competitors.
    public IEnumerable<TimingResult> Participants => Forerunners.Concat(Results);
    public int Unresolved => Observations.Count(x => x.State == "Unassigned" || x.State == "Review");
    public bool Complete => Results.Count > 0 && Results.All(x => x.Status is TimingStatus.Finished
        or TimingStatus.DNS or TimingStatus.DNF or TimingStatus.DSQ or TimingStatus.NPS);

    public IReadOnlyList<RunFinish> ToRunFinishes()
    {
        if (!Complete) { throw new DomainValidationException("Finish or classify every starter before preparing the next run."); }
        return Results.Select(x => new RunFinish(x.CompetitorId, Enum.Parse<FinishStatus>(x.Status.ToString()), x.Hundredths)).ToArray();
    }
}

public static class TimingTime
{
    // One lossless representation for all sources, including hundredth-resolution hand clocks.
    // Trailing zeroes do not imply better device accuracy; TimingObservation.Precision retains the source resolution.
    public const string TimeOfDayFormat = "HH:mm:ss.fffffff";
    public const long TicksPerHundredth = TimeSpan.TicksPerSecond / 100;
    public static string FormatTimeOfDay(long? ticks) => ticks is { } value
        ? new DateTime(value).ToString(TimeOfDayFormat, CultureInfo.InvariantCulture) : "—";

    public static string Format(long? hundredths) => hundredths is not { } value ? "—"
        : string.Create(CultureInfo.InvariantCulture, $"{value / 6000}:{value / 100 % 60:00}.{value % 100:00}");

    public static bool TryTimeOfDay(string text, out long ticks, out int precision)
    {
        ArgumentNullException.ThrowIfNull(text);
        ticks = 0; precision = 0;
        var parts = text.Split([':', '.']);
        if (parts.Length != 4 || parts[0].Length != 2 || parts[1].Length != 2 || parts[2].Length != 2
            || parts[3].Length is < 1 or > 7 || text.Any(c => !char.IsAsciiDigit(c) && c != ':' && c != '.')) { return false; }
        if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var h) || h > 23
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var m) || m > 59
            || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var s) || s > 59) { return false; }
        precision = parts[3].Length;
        ticks = (h * 3600L + m * 60L + s) * TimeSpan.TicksPerSecond
            + long.Parse(parts[3].PadRight(7, '0'), CultureInfo.InvariantCulture);
        return true;
    }
}

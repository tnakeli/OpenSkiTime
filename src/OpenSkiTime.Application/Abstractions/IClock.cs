namespace OpenSkiTime.Application.Abstractions;

/// <summary>
/// Abstraction over the system clock. Centralizing this lets domain logic
/// (year-of-birth plausibility, audit timestamps) be deterministic in tests.
/// Per Constitution FR-003: <see cref="Today"/> returns a calendar day in the
/// computer's local timezone — Open Ski Time has no timezone field.
/// </summary>
public interface IClock
{
    DateOnly Today();

    DateTime UtcNow();
}

/// <summary>Production system clock.</summary>
public sealed class SystemClock : IClock
{
    public DateOnly Today() => DateOnly.FromDateTime(DateTime.Now);

    public DateTime UtcNow() => DateTime.UtcNow;
}

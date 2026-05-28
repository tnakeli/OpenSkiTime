namespace OpenSkiTime.Domain.Common;

/// <summary>
/// Alpine racing disciplines recognized by Open Ski Time. <c>OTHER</c> is a
/// deliberate escape hatch for non-standard events (e.g., club fun runs).
/// </summary>
public enum Discipline
{
    SL = 0,
    GS = 1,
    SG = 2,
    DH = 3,
    AC = 4,
    KOMBI = 5,
    OTHER = 6,
}

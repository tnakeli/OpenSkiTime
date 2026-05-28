using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Domain.Competitions;

/// <summary>
/// A single race within an Event Series. See spec FR-010..FR-014.
/// </summary>
public class Competition
{
    /// <summary>EF Core constructor. Do not call from production code.</summary>
    private Competition() { }

    private Competition(
        Guid id,
        Guid eventSeriesId,
        string name,
        string shortLabel,
        DateOnly date,
        Discipline discipline,
        RaceType raceType,
        string? fisCode,
        string? localRaceCode,
        Gender? gender,
        string? courseName,
        int? startAltitudeMeters,
        int? finishAltitudeMeters,
        int? verticalDropMeters,
        string? homologationNumber,
        int numberOfRuns,
        int numberOfIntermediateTimes)
    {
        Id = id;
        EventSeriesId = eventSeriesId;
        Name = name;
        ShortLabel = shortLabel;
        Date = date;
        Discipline = discipline;
        RaceType = raceType;
        FisCode = fisCode;
        LocalRaceCode = localRaceCode;
        Gender = gender;
        CourseName = courseName;
        StartAltitudeMeters = startAltitudeMeters;
        FinishAltitudeMeters = finishAltitudeMeters;
        VerticalDropMeters = verticalDropMeters;
        HomologationNumber = homologationNumber;
        NumberOfRuns = numberOfRuns;
        NumberOfIntermediateTimes = numberOfIntermediateTimes;
    }

    public Guid Id { get; private set; }

    public Guid EventSeriesId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>
    /// Short label used as a participation column header in the competitor
    /// grid (e.g., <c>3.1 SL</c>). Unique within an Event Series.
    /// </summary>
    public string ShortLabel { get; private set; } = null!;

    public DateOnly Date { get; private set; }

    public Discipline Discipline { get; private set; }

    public RaceType RaceType { get; private set; }

    /// <summary>Required when <see cref="RaceType"/> is <see cref="RaceType.FIS"/> (FR-013).</summary>
    public string? FisCode { get; private set; }

    public string? LocalRaceCode { get; private set; }

    public Gender? Gender { get; private set; }

    public string? CourseName { get; private set; }

    public int? StartAltitudeMeters { get; private set; }

    public int? FinishAltitudeMeters { get; private set; }

    public int? VerticalDropMeters { get; private set; }

    public string? HomologationNumber { get; private set; }

    public int NumberOfRuns { get; private set; }

    public int NumberOfIntermediateTimes { get; private set; }

    /// <summary>
    /// Factory enforcing the spec's "required to save" rules (FR-012, FR-013)
    /// and basic numeric invariants. Optional fields may be null.
    /// </summary>
    public static Competition Create(
        Guid id,
        Guid eventSeriesId,
        string name,
        string shortLabel,
        DateOnly date,
        Discipline discipline,
        RaceType raceType,
        int numberOfRuns,
        int numberOfIntermediateTimes,
        string? fisCode = null,
        string? localRaceCode = null,
        Gender? gender = null,
        string? courseName = null,
        int? startAltitudeMeters = null,
        int? finishAltitudeMeters = null,
        int? verticalDropMeters = null,
        string? homologationNumber = null)
    {
        if (eventSeriesId == Guid.Empty)
        {
            throw new ArgumentException("EventSeriesId is required.", nameof(eventSeriesId));
        }

        ValidateName(name);
        ValidateShortLabel(shortLabel);
        ValidateRuns(numberOfRuns);
        ValidateIntermediates(numberOfIntermediateTimes);
        ValidateFisCodeForRaceType(raceType, fisCode);

        return new Competition(
            id == Guid.Empty ? Guid.NewGuid() : id,
            eventSeriesId,
            name.Trim(),
            shortLabel.Trim(),
            date,
            discipline,
            raceType,
            NormalizeOptional(fisCode),
            NormalizeOptional(localRaceCode),
            gender,
            NormalizeOptional(courseName),
            startAltitudeMeters,
            finishAltitudeMeters,
            verticalDropMeters,
            NormalizeOptional(homologationNumber),
            numberOfRuns,
            numberOfIntermediateTimes);
    }

    public void UpdateBasicData(
        string name,
        string shortLabel,
        DateOnly date,
        Discipline discipline,
        RaceType raceType,
        int numberOfRuns,
        int numberOfIntermediateTimes,
        string? fisCode,
        string? localRaceCode,
        Gender? gender,
        string? courseName,
        int? startAltitudeMeters,
        int? finishAltitudeMeters,
        int? verticalDropMeters,
        string? homologationNumber)
    {
        ValidateName(name);
        ValidateShortLabel(shortLabel);
        ValidateRuns(numberOfRuns);
        ValidateIntermediates(numberOfIntermediateTimes);
        ValidateFisCodeForRaceType(raceType, fisCode);

        Name = name.Trim();
        ShortLabel = shortLabel.Trim();
        Date = date;
        Discipline = discipline;
        RaceType = raceType;
        FisCode = NormalizeOptional(fisCode);
        LocalRaceCode = NormalizeOptional(localRaceCode);
        Gender = gender;
        CourseName = NormalizeOptional(courseName);
        StartAltitudeMeters = startAltitudeMeters;
        FinishAltitudeMeters = finishAltitudeMeters;
        VerticalDropMeters = verticalDropMeters;
        HomologationNumber = NormalizeOptional(homologationNumber);
        NumberOfRuns = numberOfRuns;
        NumberOfIntermediateTimes = numberOfIntermediateTimes;
    }

    /// <summary>True when the competition has all required fields set per FR-012/FR-013.</summary>
    public bool HasAllRequiredData()
    {
        try
        {
            ValidateName(Name);
            ValidateShortLabel(ShortLabel);
            ValidateRuns(NumberOfRuns);
            ValidateIntermediates(NumberOfIntermediateTimes);
            ValidateFisCodeForRaceType(RaceType, FisCode);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Competition name is required.", nameof(name));
        }
    }

    private static void ValidateShortLabel(string shortLabel)
    {
        if (string.IsNullOrWhiteSpace(shortLabel))
        {
            throw new ArgumentException(
                "Competition short label is required (used as participation column header).",
                nameof(shortLabel));
        }
    }

    private static void ValidateRuns(int numberOfRuns)
    {
        if (numberOfRuns < 1)
        {
            throw new ArgumentException("NumberOfRuns must be at least 1.", nameof(numberOfRuns));
        }
    }

    private static void ValidateIntermediates(int numberOfIntermediateTimes)
    {
        if (numberOfIntermediateTimes < 0)
        {
            throw new ArgumentException(
                "NumberOfIntermediateTimes must be zero or positive.",
                nameof(numberOfIntermediateTimes));
        }
    }

    private static void ValidateFisCodeForRaceType(RaceType raceType, string? fisCode)
    {
        if (raceType == RaceType.FIS && string.IsNullOrWhiteSpace(fisCode))
        {
            throw new ArgumentException(
                "FIS code is required for FIS-type races (FR-013).",
                nameof(fisCode));
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

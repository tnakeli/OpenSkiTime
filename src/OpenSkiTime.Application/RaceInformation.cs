using System.Globalization;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public sealed record RaceOfficial(string Function, FisPerson Person);
public sealed record RaceForerunner(string Letter, FisPerson Person);
public sealed record RaceWeather(string Conditions = "", string Snow = "", decimal? StartTemperature = null,
    decimal? FinishTemperature = null, string Source = "", decimal? UnlocatedTemperature = null);
public sealed record RaceRunInformation(int Number, FisPerson CourseSetter, int? Gates = null,
    int? TurningGates = null, string StartTime = "", string Course = "", int? StartAltitude = null,
    int? FinishAltitude = null, int? Drop = null, int? Length = null, string Homologation = "",
    IReadOnlyList<RaceForerunner>? Forerunners = null, RaceWeather? Weather = null);
public sealed record RaceCourseDefaults(string Course, int? StartAltitude, int? FinishAltitude, int? Drop, int? Length, string Homologation)
{
    public static RaceCourseDefaults From(CompetitionValues competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        return new(competition.CourseName ?? "", competition.StartAltitudeMeters, competition.FinishAltitudeMeters,
            competition.VerticalDropMeters, competition.CourseLengthMeters, competition.HomologationNumber ?? "");
    }
}
public sealed record RaceInformation(string Category, IReadOnlyList<RaceOfficial> Jury,
    IReadOnlyList<RaceRunInformation> Runs, string Source = "", RaceCourseDefaults? CourseDefaults = null)
{
    public static readonly string[] JuryFunctions = ["TechnicalDelegate", "ChiefRace", "Referee", "ChiefCourse", "StartReferee", "FinishReferee"];

    public static RaceInformation Empty(CompetitionValues competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        return new(competition.Calendar?.Category ?? "", JuryFunctions.Select(x => new RaceOfficial(x,
            x == "TechnicalDelegate" && competition.Calendar?.TechnicalDelegate is { } td
                ? new(td.FirstName, td.LastName, td.Nation, td.Number) : new("", "", ""))).ToArray(),
            Enumerable.Range(1, competition.RunCount).Select(n => new RaceRunInformation(n, new("", "", ""),
                Course: competition.CourseName ?? "", StartAltitude: competition.StartAltitudeMeters,
                FinishAltitude: competition.FinishAltitudeMeters, Drop: competition.VerticalDropMeters,
                Length: competition.CourseLengthMeters, Homologation: competition.HomologationNumber ?? "", Forerunners: [], Weather: new())).ToArray(),
            CourseDefaults: RaceCourseDefaults.From(competition));
    }

    public RaceInformation WithCompetitionCourse(CompetitionValues competition)
    {
        ArgumentNullException.ThrowIfNull(competition);
        var previous = CourseDefaults ?? new("", null, null, null, null, "");
        var current = RaceCourseDefaults.From(competition);
        // Fields that follow the competition defaults move together; per-run differences stay local.
        return this with { CourseDefaults = current, Runs = Runs.Select(run => run with {
            Course = run.Course == previous.Course ? current.Course : run.Course,
            StartAltitude = run.StartAltitude == previous.StartAltitude ? current.StartAltitude : run.StartAltitude,
            FinishAltitude = run.FinishAltitude == previous.FinishAltitude ? current.FinishAltitude : run.FinishAltitude,
            Drop = run.Drop == previous.Drop ? current.Drop : run.Drop,
            Length = run.Length == previous.Length ? current.Length : run.Length,
            Homologation = run.Homologation == previous.Homologation ? current.Homologation : run.Homologation
        }).ToArray() };
    }

    // Drafts may be incomplete; entered values must still be valid before durable mutation.
    public void Validate(int runCount)
    {
        if (Category.Length > 32) { throw new DomainValidationException("FIS category may contain at most 32 characters."); }
        if (Runs.Count != runCount || !Runs.Select(x => x.Number).SequenceEqual(Enumerable.Range(1, runCount)))
        { throw new DomainValidationException("Provide information for every run in order."); }
        if (Jury.Select(x => x.Function).Distinct(StringComparer.Ordinal).Count() != Jury.Count
            || Jury.Any(x => !JuryFunctions.Contains(x.Function, StringComparer.Ordinal)))
        { throw new DomainValidationException("Invalid or duplicate jury function."); }
        foreach (var person in Jury.Select(x => x.Person).Concat(Runs.Select(x => x.CourseSetter))
            .Concat(Runs.SelectMany(x => x.Forerunners ?? []).Select(x => x.Person)))
        {
            if (person.Nation.Length != 0 && (person.Nation.Length != 3 || !person.Nation.All(char.IsAsciiLetter)))
            { throw new DomainValidationException("Use three-letter nation codes."); }
            if (person.FirstName.Length > 160 || person.LastName.Length > 160)
            { throw new DomainValidationException("Names may contain at most 160 characters."); }
        }
        // Result XML cannot carry most control characters, and pasted text can contain them (a Word line break is U+000B).
        var texts = Jury.Select(x => x.Person).Concat(Runs.Select(x => x.CourseSetter))
            .Concat(Runs.SelectMany(x => x.Forerunners ?? []).Select(x => x.Person))
            .SelectMany(x => new[] { x.FirstName, x.LastName, x.Nation, x.Number }).Append(Category)
            .Concat(Runs.SelectMany(x => new[] { x.Course, x.Homologation, x.StartTime, x.Weather?.Conditions, x.Weather?.Snow }));
        if (!texts.All(TextRules.IsPortable))
        { throw new DomainValidationException("Race information contains a control character that cannot be exported (for example a pasted Word line break)."); }
        foreach (var run in Runs)
        {
            if (run.Course.Length > 160 || run.Homologation.Length > 50
                || run.Weather?.Conditions.Length > 160 || run.Weather?.Snow.Length > 160)
            { throw new DomainValidationException("Course and weather descriptions may contain at most 160 characters; homologation at most 50."); }
            if (run.Gates is <= 0 or > 1000 || run.TurningGates is <= 0 or > 1000 || run.TurningGates > run.Gates)
            { throw new DomainValidationException("Gate counts must be positive; turns cannot exceed gates."); }
            if (run.StartTime.Length > 0 && !TimeOnly.TryParseExact(run.StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            { throw new DomainValidationException("Use HH:mm for run start times."); }
            if (new[] { run.StartAltitude, run.FinishAltitude, run.Drop, run.Length }.Any(x => x is < 0 or > 9000))
            { throw new DomainValidationException("Course dimensions must be between 0 and 9000 metres."); }
            if ((run.Weather?.StartTemperature is < -100 or > 100) || (run.Weather?.FinishTemperature is < -100 or > 100)
                || (run.Weather?.UnlocatedTemperature is < -100 or > 100))
            { throw new DomainValidationException("Temperatures must be between -100 and 100 °C."); }
            var runners = run.Forerunners ?? [];
            if (runners.Any(x => x.Letter.Length is < 1 or > 16 || !x.Letter.All(char.IsAsciiLetterOrDigit))
                || runners.Select(x => x.Letter).Distinct().Count() != runners.Count)
            { throw new DomainValidationException("Forerunners need a unique identifier."); }
        }
    }
}

public sealed record SavedRaceInformation(Guid CompetitionId, int Revision, RaceInformation Values, DateTimeOffset SavedAt);
public interface IRaceInformationStore
{
    Task<SavedRaceInformation?> ReadRaceInformationAsync(Guid competitionId, CancellationToken ct = default);
    Task<long> SaveRaceInformationAsync(Guid competitionId, RaceInformation values, long expectedSeriesRevision,
        DateTimeOffset at, CancellationToken ct = default);
}

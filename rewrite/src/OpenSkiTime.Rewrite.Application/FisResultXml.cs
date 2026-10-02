using System.Globalization;
using System.Text;
using System.Xml;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Application;

public sealed record FisPerson(string FirstName, string LastName, string Nation, string Number = "");
public sealed record FisRunXmlDetails(int Gates, int TurningGates, string StartTime, FisPerson CourseSetter);
public sealed record FisXmlDetails(string Category, FisPerson TechnicalDelegate, FisPerson ChiefOfRace,
    IReadOnlyList<FisRunXmlDetails> Runs, RaceInformation? Information = null);

public static class FisResultXml
{
    public static string FileName(FisRaceResult race, SeriesValues series)
    {
        ArgumentNullException.ThrowIfNull(race); ArgumentNullException.ThrowIfNull(series);
        var nation = race.FirstList.Plan.Competition.Calendar?.Nation;
        return (string.IsNullOrWhiteSpace(nation) ? series.Nation : nation).ToUpperInvariant()
            + ValidCodex(race.FirstList.Plan.Competition.FisCode) + ".xml";
    }

    public static byte[] Create(FisRaceResult race, SeriesValues series, FisPenaltyResult penalty, FisXmlDetails details)
    {
        ArgumentNullException.ThrowIfNull(race); ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(penalty); ArgumentNullException.ThrowIfNull(details);
        var plan = race.FirstList.Plan;
        if (plan.Competition.RaceType != RaceType.Fis) { throw new DomainValidationException("FIS XML is for a FIS competition."); }
        var codex = ValidCodex(plan.Competition.FisCode);
        var competition = plan.Competition;
        details = details with { Information = details.Information?.WithCompetitionCourse(competition) };
        var calendar = competition.Calendar?.Validated();
        var category = Required(string.IsNullOrWhiteSpace(calendar?.Category) ? details.Category : calendar.Category, "FIS calendar category in Competitions");
        var technicalDelegate = calendar?.TechnicalDelegate is { } sharedTd
            ? new FisPerson(sharedTd.FirstName, sharedTd.LastName, sharedTd.Nation, sharedTd.Number) : details.TechnicalDelegate;
        var nation = Required(string.IsNullOrWhiteSpace(calendar?.Nation) ? series.Nation : calendar.Nation, "organizing nation");
        var gender = plan.Gender == Gender.Male ? "M" : "W";
        if (!string.IsNullOrWhiteSpace(calendar?.Gender) && calendar.Gender != gender)
        { throw new DomainValidationException("Competition calendar gender differs from the drawn starter group. Review Competitions and the start list."); }
        if (nation.Length != 3) { throw new DomainValidationException("Use a three-letter organizing nation."); }
        if (race.Rows.Count == 0 || race.Rows.Any(x => x.Entry.Entrant.Athlete.FederationCode is null
            || string.IsNullOrWhiteSpace(x.Entry.Entrant.Athlete.Surname)
            || string.IsNullOrWhiteSpace(x.Entry.Entrant.Athlete.FirstName)
            || string.IsNullOrWhiteSpace(x.Entry.Entrant.Athlete.Nation)))
        { throw new DomainValidationException("All starters need FIS code, name and nation before FIS XML can be created."); }
        details.Information?.Validate(competition.RunCount);
        if (details.Runs.Count != competition.RunCount)
        { throw new DomainValidationException("Enter homologation, start/finish elevations and information for every run."); }
        for (var i = 0; i < details.Runs.Count; i++)
        {
            var run = details.Runs[i];
            var report = details.Information?.Runs[i];
            if (string.IsNullOrWhiteSpace(report?.Homologation ?? competition.HomologationNumber)
                || (report?.StartAltitude ?? competition.StartAltitudeMeters) is null
                || (report?.FinishAltitude ?? competition.FinishAltitudeMeters) is null)
            { throw new DomainValidationException("Every run needs homologation and start/finish elevations."); }
            if (run.Gates <= 0 || run.TurningGates <= 0 || run.TurningGates > run.Gates
                || !TimeOnly.TryParseExact(run.StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            { throw new DomainValidationException("Each run needs gate counts and a 24-hour HH:mm start time."); }
            Validate(run.CourseSetter);
            foreach (var runner in report?.Forerunners ?? []) { Validate(runner.Person); }
        }
        Validate(technicalDelegate); Validate(details.ChiefOfRace);
        foreach (var member in details.Information?.Jury ?? [])
        {
            if (member.Person.FirstName.Length + member.Person.LastName.Length + member.Person.Nation.Length > 0)
            { Validate(member.Person); }
        }
        if (race.Rows.Count(x => x.Status == TimingStatus.Finished) != penalty.RacePoints.Count)
        { throw new DomainValidationException("Penalty and classified results do not match."); }

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true,
            CloseOutput = false, NewLineChars = "\n" }))
        {
            writer.WriteStartDocument(); writer.WriteStartElement("Fisresults");
            writer.WriteStartElement("Raceheader"); writer.WriteAttributeString("Sector", "AL");
            writer.WriteAttributeString("Gender", gender);
            E(writer, "Season", (calendar?.Season ?? (competition.Date.Month >= 6 ? competition.Date.Year + 1 : competition.Date.Year)).ToString(CultureInfo.InvariantCulture));
            E(writer, "Codex", codex); E(writer, "Nation", nation); E(writer, "Discipline", DisciplineCode(competition.Discipline));
            E(writer, "Category", category); E(writer, "Type", "Official");
            E(writer, "Eventname", competition.Name); E(writer, "Place", string.IsNullOrWhiteSpace(calendar?.Location) ? series.Location : calendar.Location);
            writer.WriteStartElement("Racedate"); E(writer, "Day", competition.Date.Day.ToString(CultureInfo.InvariantCulture));
            E(writer, "Month", competition.Date.Month.ToString(CultureInfo.InvariantCulture));
            E(writer, "Year", competition.Date.Year.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
            E(writer, "Tempunit", "C"); E(writer, "Longunit", "m");
            writer.WriteEndElement();
            writer.WriteStartElement("AL_race"); writer.WriteStartElement("AL_raceinfo");
            E(writer, "Usedfislist", FisListNumber(plan.PointsList.Code));
            E(writer, "Appliedpenalty", D(penalty.Applied)); E(writer, "Calculatedpenalty", D(penalty.Calculated));
            E(writer, "Fvalue", penalty.FValue.ToString(CultureInfo.InvariantCulture));
            Person(writer, "Jury", "TechnicalDelegate", technicalDelegate);
            Person(writer, "Jury", "ChiefRace", details.ChiefOfRace);
            foreach (var member in details.Information?.Jury ?? [])
            {
                if (member.Function is "TechnicalDelegate" or "ChiefRace" || string.IsNullOrWhiteSpace(member.Person.LastName)) { continue; }
                var function = member.Function switch { "StartReferee" => "Startreferee", "FinishReferee" => "Finishreferee", _ => member.Function };
                Person(writer, "Jury", function, member.Person);
            }
            for (var i = 0; i < details.Runs.Count; i++)
            {
                var run = details.Runs[i]; writer.WriteStartElement("Runinfo");
                var report = details.Information?.Runs[i];
                writer.WriteAttributeString("No", (i + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteStartElement("Course");
                var courseName = report?.Course ?? competition.CourseName;
                if (!string.IsNullOrWhiteSpace(courseName)) { E(writer, "Name", courseName); }
                E(writer, "Homologation", report?.Homologation ?? competition.HomologationNumber!);
                if ((report?.Length ?? competition.CourseLengthMeters) is { } length) { E(writer, "Length", length.ToString(CultureInfo.InvariantCulture)); }
                E(writer, "Gates", run.Gates.ToString(CultureInfo.InvariantCulture));
                E(writer, "Turninggates", run.TurningGates.ToString(CultureInfo.InvariantCulture));
                E(writer, "Startelev", (report?.StartAltitude ?? competition.StartAltitudeMeters)!.Value.ToString(CultureInfo.InvariantCulture));
                E(writer, "Finishelev", (report?.FinishAltitude ?? competition.FinishAltitudeMeters)!.Value.ToString(CultureInfo.InvariantCulture));
                Person(writer, "Coursesetter", null, run.CourseSetter);
                var runners = report?.Forerunners ?? [];
                for (var n = 0; n < runners.Count; n++)
                {
                    writer.WriteStartElement("Forerunner"); writer.WriteAttributeString("Order", (n + 1).ToString(CultureInfo.InvariantCulture));
                    E(writer, "Lastname", runners[n].Person.LastName); E(writer, "Firstname", runners[n].Person.FirstName);
                    E(writer, "Nation", runners[n].Person.Nation); writer.WriteEndElement();
                }
                writer.WriteEndElement(); E(writer, "Starttime", run.StartTime);
                if (report?.Weather is { } weather)
                {
                    // Two weather records preserve the reporting location of each measured air temperature.
                    WriteWeather(writer, weather, "Start", weather.StartTemperature, true);
                    WriteWeather(writer, weather, "Finish", weather.FinishTemperature, false);
                }
                writer.WriteEndElement();
            }
            E(writer, "Softwarename", "OpenSkiTime"); writer.WriteEndElement();
            writer.WriteStartElement("AL_classified");
            var winner = race.Rows.Where(x => x.TotalHundredths is not null).Min(x => x.TotalHundredths)!.Value;
            foreach (var row in race.Rows.Where(x => x.Status == TimingStatus.Finished).OrderBy(x => x.Rank).ThenByDescending(x => x.Entry.Bib))
            {
                writer.WriteStartElement("AL_ranked"); writer.WriteAttributeString("Status", "QLF");
                E(writer, "Rank", row.Rank!.Value.ToString(CultureInfo.InvariantCulture));
                E(writer, "Order", row.Entry.Position.ToString(CultureInfo.InvariantCulture));
                E(writer, "Bib", row.Entry.Bib.ToString(CultureInfo.InvariantCulture)); Competitor(writer, row.Entry);
                writer.WriteStartElement("AL_result"); E(writer, "Timerun1", T(row.Run1Hundredths!.Value));
                if (row.Run2Hundredths is { } second) { E(writer, "Timerun2", T(second)); }
                E(writer, "Totaltime", T(row.TotalHundredths!.Value));
                E(writer, "Diff", T(row.TotalHundredths.Value - winner));
                E(writer, "Racepoints", D(penalty.RacePoints[row.Entry.Bib])); writer.WriteEndElement(); writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteStartElement("AL_notclassified");
            foreach (var row in race.Rows.Where(x => x.Status != TimingStatus.Finished).OrderBy(x => x.StatusRun).ThenBy(x => x.Entry.Position))
            {
                writer.WriteStartElement("AL_notranked");
                writer.WriteAttributeString("Status", row.Status == TimingStatus.NPS ? "NPS"
                    : row.Status.ToString() + (competition.RunCount == 1 ? "" : row.StatusRun.ToString(CultureInfo.InvariantCulture)));
                E(writer, "Run", row.StatusRun.ToString(CultureInfo.InvariantCulture));
                E(writer, "Bib", row.Entry.Bib.ToString(CultureInfo.InvariantCulture)); Competitor(writer, row.Entry);
                if (row.StatusRun > 1 && row.Run1Hundredths is { } firstTime)
                {
                    writer.WriteStartElement("AL_result"); E(writer, "Timerun1", T(firstTime)); writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
        }
        return stream.ToArray();
    }

    private static void E(XmlWriter w, string name, string value) => w.WriteElementString(name, value);
    private static void WriteWeather(XmlWriter writer, RaceWeather weather, string place, decimal? temperature, bool includeConditions)
    {
        if (temperature is null && (!includeConditions || (weather.Conditions.Length == 0 && weather.Snow.Length == 0))) { return; }
        writer.WriteStartElement("Weather"); E(writer, "Place", place);
        if (includeConditions && weather.Conditions.Length > 0) { E(writer, "Weather", weather.Conditions); }
        if (includeConditions && weather.Snow.Length > 0) { E(writer, "Snow", weather.Snow); }
        if (temperature is { } air) { E(writer, "Temperatureair", air.ToString(CultureInfo.InvariantCulture)); }
        writer.WriteEndElement();
    }
    private static string D(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
    private static string T(long value) => $"{value / 6000:00}:{value / 100 % 60:00}:{value % 100:00}";
    private static string Required(string? value, string label) => !string.IsNullOrWhiteSpace(value) ? value.Trim()
        : throw new DomainValidationException($"Enter {label} before approving FIS results.");
    private static string ValidCodex(string? value) => value is { Length: 4 } && value.All(char.IsAsciiDigit)
        ? value : throw new DomainValidationException("FIS XML needs the competition's four-digit codex.");
    private static string FisListNumber(string code) => code.Length == 4 && code.All(char.IsAsciiDigit)
        ? int.Parse(code[..2], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
        : throw new DomainValidationException("The drawn FIS points-list code is invalid.");
    private static void Validate(FisPerson person)
    {
        Required(person.FirstName, "person first name"); Required(person.LastName, "person last name");
        if (Required(person.Nation, "person nation").Length != 3)
        { throw new DomainValidationException("Jury and course-setter nations must be three-letter codes."); }
    }
    private static void Person(XmlWriter w, string element, string? function, FisPerson person)
    {
        w.WriteStartElement(element); if (function is not null) { w.WriteAttributeString("Function", function); }
        if (!string.IsNullOrWhiteSpace(person.Number)) { E(w, "Number", person.Number); }
        E(w, "Lastname", person.LastName.Trim()); E(w, "Firstname", person.FirstName.Trim());
        E(w, "Nation", person.Nation.Trim().ToUpperInvariant()); w.WriteEndElement();
    }
    private static void Competitor(XmlWriter w, StartListEntry entry)
    {
        var athlete = entry.Entrant.Athlete; w.WriteStartElement("Competitor");
        E(w, "Fiscode", athlete.FederationCode!.Trim()); E(w, "Lastname", athlete.Surname.Trim());
        E(w, "Firstname", athlete.FirstName.Trim()); E(w, "Gender", athlete.Gender == Gender.Male ? "M" : "F");
        E(w, "Nation", athlete.Nation!.Trim());
        if (athlete.BirthYear is { } year) { E(w, "Yearofbirth", year.ToString(CultureInfo.InvariantCulture)); }
        if (!string.IsNullOrWhiteSpace(athlete.Club)) { E(w, "Clubname", athlete.Club); }
        w.WriteEndElement();
    }
    private static string DisciplineCode(Discipline value) => value switch
    {
        Discipline.Downhill => "DH", Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS",
        Discipline.SuperG => "SG", _ => throw new DomainValidationException("Unsupported FIS XML discipline.")
    };
}

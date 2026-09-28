using System.Globalization;
using System.Text;
using System.Xml;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Application;

public sealed record FisPerson(string FirstName, string LastName, string Nation);
public sealed record FisRunXmlDetails(int Gates, int TurningGates, string StartTime, FisPerson CourseSetter);
public sealed record FisXmlDetails(string Category, FisPerson TechnicalDelegate, FisPerson ChiefOfRace,
    IReadOnlyList<FisRunXmlDetails> Runs);

public static class FisResultXml
{
    public static string FileName(FisRaceResult race, SeriesValues series)
    {
        ArgumentNullException.ThrowIfNull(race); ArgumentNullException.ThrowIfNull(series);
        return series.Nation.ToUpperInvariant() + ValidCodex(race.FirstList.Plan.Competition.FisCode) + ".xml";
    }

    public static byte[] Create(FisRaceResult race, SeriesValues series, FisPenaltyResult penalty, FisXmlDetails details)
    {
        ArgumentNullException.ThrowIfNull(race); ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(penalty); ArgumentNullException.ThrowIfNull(details);
        var plan = race.FirstList.Plan;
        if (plan.Competition.RaceType != RaceType.Fis) { throw new DomainValidationException("FIS XML is for a FIS competition."); }
        var codex = ValidCodex(plan.Competition.FisCode);
        var category = Required(details.Category, "FIS calendar category");
        var competition = plan.Competition;
        var nation = Required(series.Nation, "organizing nation");
        if (nation.Length != 3) { throw new DomainValidationException("Use a three-letter organizing nation."); }
        if (race.Rows.Count == 0 || race.Rows.Any(x => x.Entry.Entrant.Athlete.FederationCode is null
            || string.IsNullOrWhiteSpace(x.Entry.Entrant.Athlete.Surname)
            || string.IsNullOrWhiteSpace(x.Entry.Entrant.Athlete.FirstName)
            || string.IsNullOrWhiteSpace(x.Entry.Entrant.Athlete.Nation)))
        { throw new DomainValidationException("All starters need FIS code, name and nation before FIS XML can be created."); }
        if (details.Runs.Count != competition.RunCount || competition.HomologationNumber is null
            || competition.StartAltitudeMeters is null || competition.FinishAltitudeMeters is null)
        { throw new DomainValidationException("Enter homologation, start/finish elevations and information for every run."); }
        foreach (var run in details.Runs)
        {
            if (run.Gates <= 0 || run.TurningGates <= 0 || run.TurningGates > run.Gates
                || !TimeOnly.TryParseExact(run.StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            { throw new DomainValidationException("Each run needs gate counts and a 24-hour HH:mm start time."); }
            Validate(run.CourseSetter);
        }
        Validate(details.TechnicalDelegate); Validate(details.ChiefOfRace);
        if (race.Rows.Count(x => x.Status == TimingStatus.Finished) != penalty.RacePoints.Count)
        { throw new DomainValidationException("Penalty and classified results do not match."); }

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true,
            CloseOutput = false, NewLineChars = "\n" }))
        {
            writer.WriteStartDocument(); writer.WriteStartElement("Fisresults");
            writer.WriteStartElement("Raceheader"); writer.WriteAttributeString("Sector", "AL");
            writer.WriteAttributeString("Gender", plan.Gender == Gender.Male ? "M" : "W");
            E(writer, "Season", (competition.Date.Month >= 6 ? competition.Date.Year + 1 : competition.Date.Year).ToString(CultureInfo.InvariantCulture));
            E(writer, "Codex", codex); E(writer, "Nation", nation); E(writer, "Discipline", DisciplineCode(competition.Discipline));
            E(writer, "Category", category); E(writer, "Type", "Official");
            E(writer, "Eventname", competition.Name); E(writer, "Place", series.Location);
            writer.WriteStartElement("Racedate"); E(writer, "Day", competition.Date.Day.ToString(CultureInfo.InvariantCulture));
            E(writer, "Month", competition.Date.Month.ToString(CultureInfo.InvariantCulture));
            E(writer, "Year", competition.Date.Year.ToString(CultureInfo.InvariantCulture)); writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteStartElement("AL_race"); writer.WriteStartElement("AL_raceinfo");
            E(writer, "Usedfislist", FisListNumber(plan.PointsList.Code));
            E(writer, "Appliedpenalty", D(penalty.Applied)); E(writer, "Calculatedpenalty", D(penalty.Calculated));
            E(writer, "Fvalue", penalty.FValue.ToString(CultureInfo.InvariantCulture));
            Person(writer, "Jury", "TechnicalDelegate", details.TechnicalDelegate);
            Person(writer, "Jury", "ChiefRace", details.ChiefOfRace);
            for (var i = 0; i < details.Runs.Count; i++)
            {
                var run = details.Runs[i]; writer.WriteStartElement("Runinfo");
                writer.WriteAttributeString("No", (i + 1).ToString(CultureInfo.InvariantCulture));
                writer.WriteStartElement("Course");
                if (!string.IsNullOrWhiteSpace(competition.CourseName)) { E(writer, "Name", competition.CourseName); }
                E(writer, "Homologation", competition.HomologationNumber);
                E(writer, "Gates", run.Gates.ToString(CultureInfo.InvariantCulture));
                E(writer, "Turninggates", run.TurningGates.ToString(CultureInfo.InvariantCulture));
                E(writer, "Startelev", competition.StartAltitudeMeters.Value.ToString(CultureInfo.InvariantCulture));
                E(writer, "Finishelev", competition.FinishAltitudeMeters.Value.ToString(CultureInfo.InvariantCulture));
                Person(writer, "Coursesetter", null, run.CourseSetter);
                writer.WriteEndElement(); E(writer, "Starttime", run.StartTime); writer.WriteEndElement();
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

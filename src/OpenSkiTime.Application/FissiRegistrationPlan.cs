using System.Globalization;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

/// <summary>What the update found and proposes for one competition.</summary>
public sealed record FissiRaceOutcome(Guid CompetitionId, string ShortLabel, string? Codex, DateOnly? Date, int? Registered,
    int New, int Added, int Removed, FissiRaceStatus Status)
{
    public string StatusText => Status switch
    {
        FissiRaceStatus.Ready => Added + Removed > 0 ? "Review and save" : "Up to date",
        FissiRaceStatus.DateDiffers => "Date differs from the competition, check the race",
        FissiRaceStatus.NoneRegistered => "None registered",
        FissiRaceStatus.NoCodex => "No codex",
        FissiRaceStatus.NotFound => "Not found",
        FissiRaceStatus.RaceDiffers => "Gender or discipline differs",
        FissiRaceStatus.SeveralMatches => "Several matches",
        FissiRaceStatus.StartListSaved => "Start list saved, not updated",
        _ => Status.ToString(),
    };
}

/// <summary>
/// The changes the registrations propose to a series: new competitors, changed details, entries and removed entries.
/// They are staged for the operator's review; nothing here writes.
/// </summary>
public sealed record FissiRegistrationPlan(IReadOnlyList<ImportReviewItem> Rows, IReadOnlyList<FissiRaceOutcome> Races,
    IReadOnlyList<string> Warnings)
{
    /// <param name="series">The open series with its competitions.</param>
    /// <param name="desk">The saved competitors and entries of the series.</param>
    /// <param name="registrations">The registrations found for the competitions.</param>
    /// <param name="lockedCompetitions">Competitions with a saved start list: their entries stay as they are.</param>
    /// <param name="lockedCompetitors">Competitors on a saved start list: their details stay as they are, because a
    /// later run requires the details of Run 1.</param>
    /// <param name="fisAthletes">The loaded FIS points list, used only to split a name written in capitals.</param>
    public static FissiRegistrationPlan Build(SeriesDetails series, CompetitorDeskDetails desk,
        IReadOnlyList<FissiCompetition> registrations, IReadOnlySet<Guid> lockedCompetitions, IReadOnlySet<Guid> lockedCompetitors,
        IReadOnlyDictionary<string, FisAthlete>? fisAthletes = null)
    {
        ArgumentNullException.ThrowIfNull(series); ArgumentNullException.ThrowIfNull(desk); ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(lockedCompetitions); ArgumentNullException.ThrowIfNull(lockedCompetitors);
        var competitions = series.Competitions.ToDictionary(x => x.Id);
        if (series.Id != desk.SeriesId || registrations.Any(x => !competitions.ContainsKey(x.CompetitionId))) { throw new SeriesConflictException(); }
        // A start list may have been saved while the registrations were read.
        var current = registrations.Select(x => x.Status != FissiRaceStatus.NoCodex && lockedCompetitions.Contains(x.CompetitionId)
            ? x with { Status = FissiRaceStatus.StartListSaved, Registrations = null } : x).ToArray();
        // A competition without registrations removes nobody: its registration may not be open yet.
        var active = current.Where(x => x.Status is FissiRaceStatus.Ready or FissiRaceStatus.DateDiffers && x.Registrations is { Athletes.Count: > 0 }).ToArray();
        var warnings = new List<string>();
        var keepEntries = new HashSet<Guid>();
        var people = Resolve(Collect(active, competitions), desk, warnings, keepEntries);

        var rows = new List<ImportReviewItem>();
        var updates = new Dictionary<Guid, Update>();
        var counts = current.ToDictionary(x => x.CompetitionId, _ => new Counts());
        var entered = desk.Participations.Where(x => x.Participates).Select(x => (x.CompetitorId, x.CompetitionId)).ToHashSet();
        var registered = new HashSet<(Guid CompetitorId, Guid CompetitionId)>();
        foreach (var person in people)
        {
            var before = person.Match?.Values;
            var notes = new List<string>();
            var values = person.Match is { } known && lockedCompetitors.Contains(known.Id) ? known.Values : Values(person, before, fisAthletes, notes);
            // A gender taken from the race must fit the entries the competitor already has, or Save would refuse everything.
            if (person.Match is { } ungendered && before!.Gender is null && values.Gender is { } inferred
                && series.Competitions.FirstOrDefault(x => entered.Contains((ungendered.Id, x.Id))
                    && !CompetitionGenderRule.Allows(x.Values.Calendar?.Gender, inferred)) is { } other)
            {
                values = values with { Gender = null };
                warnings.Add($"{Describe(person.Athlete)} is also entered in {other.Values.ShortLabel}, a competition of the other gender. The gender was not filled in.");
            }
            try { if (!ReferenceEquals(values, before)) { values = values.Validated(series.Values.EndDate.Year); } }
            catch (DomainValidationException ex)
            {
                warnings.Add($"{Describe(person.Athlete)}: {ex.Message} Not updated.");
                if (person.Match is { } kept) { keepEntries.Add(kept.Id); }
                continue;
            }
            // Compared with the site's Code: a competitor whose details are locked keeps the saved one.
            if (before?.FederationCode is { } oldCode && person.Athlete.Code is { } newCode
                && !oldCode.Equals(newCode, StringComparison.OrdinalIgnoreCase))
            { notes.Add($"Code is {oldCode} here and {newCode} on regs.fissi.fi; confirm this match."); }
            var entries = new List<ImportEntryPatch>();
            foreach (var id in person.Competitions)
            {
                if (person.Match is { } match)
                {
                    registered.Add((match.Id, id));
                    if (entered.Contains((match.Id, id))) { continue; }
                }
                var competition = competitions[id].Values;
                var gender = competition.Calendar?.Gender;
                if (!CompetitionGenderRule.Allows(gender, values.Gender))
                {
                    warnings.Add(CompetitionGenderRule.Violation($"{values.Surname} {values.FirstName}".Trim(), values.Gender!.Value,
                        competition.ShortLabel, CompetitionGenderRule.Required(gender)!.Value) + " The entry was not added.");
                    continue;
                }
                entries.Add(new ImportEntryPatch(id, true, false, null));
                counts[id].Added++;
                if (person.Match is null) { counts[id].New++; }
            }
            if (person.Match is { } existing) { updates.Add(existing.Id, new Update(values, Changes(existing.Values, values), entries, notes)); }
            else { rows.Add(new ImportReviewItem(rows.Count + 1, null, values, entries, ["NEW"], notes, true)); }
        }
        // Entered in the series but no longer registered: the entry is proposed for removal, but never when the code or
        // the name is on the registration list.
        foreach (var competitor in desk.Competitors.Where(x => !keepEntries.Contains(x.Id)))
        {
            foreach (var registration in active)
            {
                var id = registration.CompetitionId;
                if (!entered.Contains((competitor.Id, id)) || registered.Contains((competitor.Id, id))) { continue; }
                if (registration.Registrations!.Athletes.Any(x => IsListed(x, competitor.Values)))
                {
                    warnings.Add($"{competitor.Values.Surname} {competitor.Values.FirstName}".Trim()
                        + $": an athlete with the same name or code is registered in {competitions[id].Values.ShortLabel}. The entry was kept; check it.");
                    continue;
                }
                if (!updates.TryGetValue(competitor.Id, out var update)) { updates.Add(competitor.Id, update = new Update(competitor.Values, [], [], [])); }
                update.Entries.Add(new ImportEntryPatch(id, false, false, null));
                counts[id].Removed++;
            }
        }
        foreach (var competitor in desk.Competitors)
        {
            if (!updates.TryGetValue(competitor.Id, out var update) || update.Changed.Count + update.Entries.Count == 0) { continue; }
            rows.Add(new ImportReviewItem(rows.Count + 1, competitor.Id, update.Values, update.Entries,
                update.Entries.Count > 0 ? [.. update.Changed, "Entry"] : update.Changed, update.Notes, false));
        }
        return new FissiRegistrationPlan(rows, current.Select(x => new FissiRaceOutcome(x.CompetitionId, competitions[x.CompetitionId].Values.ShortLabel,
            x.Codex, x.Registrations?.Race.Date, x.Registrations?.Athletes.Count, counts[x.CompetitionId].New, counts[x.CompetitionId].Added,
            counts[x.CompetitionId].Removed, x.Status)).ToArray(), warnings);
    }

    // Repeated: the registrations have this code, or this name and birth year without a code, for different athletes.
    private sealed record Person(FissiAthlete Athlete, List<Guid> Competitions, HashSet<Gender> Genders, CompetitorDetails? Match = null)
    { public bool Repeated { get; set; } }
    private sealed record Update(CompetitorValues Values, List<string> Changed, List<ImportEntryPatch> Entries, List<string> Notes);
    private sealed class Counts { public int New { get; set; } public int Added { get; set; } public int Removed { get; set; } }

    // One person per FIS code, or per name and birth year without a code.
    private static List<Person> Collect(IReadOnlyList<FissiCompetition> active, Dictionary<Guid, CompetitionDetails> competitions)
    {
        var people = new Dictionary<string, Person>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<Person>();
        foreach (var registration in active)
        {
            var gender = CompetitionGenderRule.Required(registration.Registrations!.Race.Gender)
                ?? CompetitionGenderRule.Required(competitions[registration.CompetitionId].Values.Calendar?.Gender);
            foreach (var athlete in registration.Registrations!.Athletes)
            {
                var key = athlete.Code is { } code ? "code:" + code : "name:" + NameKey(athlete.FullName, athlete.BirthYear);
                if (!people.TryGetValue(key, out var person))
                {
                    people.Add(key, person = new Person(athlete, [], []));
                    ordered.Add(person);
                }
                if (!person.Competitions.Contains(registration.CompetitionId)) { person.Competitions.Add(registration.CompetitionId); }
                else if (athlete.Code is null || athlete != person.Athlete) { person.Repeated = true; }
                // Another race may show the same athlete with another club, never with another name or birth year.
                if (!athlete.FullName.Equals(person.Athlete.FullName, StringComparison.OrdinalIgnoreCase) || athlete.BirthYear != person.Athlete.BirthYear)
                { person.Repeated = true; }
                if (gender is { } value) { person.Genders.Add(value); }
            }
        }
        return ordered;
    }

    // A person whose match is not certain is left out with a warning, and nobody involved loses an entry.
    private static List<Person> Resolve(List<Person> people, CompetitorDeskDetails desk, List<string> warnings, HashSet<Guid> keepEntries)
    {
        var byCode = desk.Competitors.Where(x => x.Values.FederationCode is not null)
            .ToLookup(x => x.Values.FederationCode!, StringComparer.OrdinalIgnoreCase);
        var byName = desk.Competitors.SelectMany(x => new[]
            {
                (Name: $"{x.Values.FirstName} {x.Values.Surname}".Trim(), Competitor: x),
                (Name: $"{x.Values.Surname} {x.Values.FirstName}".Trim(), Competitor: x),
            }).ToLookup(x => x.Name, x => x.Competitor, StringComparer.OrdinalIgnoreCase);
        var sameName = people.GroupBy(x => NameKey(x.Athlete.FullName, x.Athlete.BirthYear), StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<Person>();
        foreach (var person in people)
        {
            var name = NameKey(person.Athlete.FullName, person.Athlete.BirthYear);
            var coded = person.Athlete.Code is { } code ? byCode[code] : [];
            var namesakes = byName[person.Athlete.FullName].DistinctBy(x => x.Id).ToArray();
            var named = person.Athlete.BirthYear is { } year ? namesakes.Where(x => x.Values.BirthYear == year) : [];
            var involved = coded.Concat(named).DistinctBy(x => x.Id).ToArray();
            // The same name with a birth year missing on either side may or may not be the same person.
            var unclear = coded.Any() ? [] : namesakes.Where(x => person.Athlete.BirthYear is null || x.Values.BirthYear is null).ToArray();
            var problem = involved.Length > 1 ? "matches several competitors"
                : person.Repeated || sameName.Contains(name) ? "is registered more than once or with different details"
                : person.Genders.Count > 1 ? "is registered in men's and women's competitions"
                : unclear.Length > 0 ? "cannot be matched without the birth year"
                : involved.Length == 0 && person.Athlete is { Code: null, BirthYear: null } ? "has neither a FIS code nor a birth year" : null;
            involved = [.. involved, .. unclear];
            if (problem is null)
            {
                resolved.Add(person with { Match = involved.FirstOrDefault() });
                continue;
            }
            warnings.Add($"{Describe(person.Athlete)} {problem}. Not updated.");
            keepEntries.UnionWith(involved.Select(x => x.Id));
        }
        // Two registered athletes for one competitor: neither is certain.
        foreach (var group in resolved.Where(x => x.Match is not null).GroupBy(x => x.Match!.Id).Where(x => x.Count() > 1).ToArray())
        {
            foreach (var person in group)
            {
                warnings.Add($"{Describe(person.Athlete)} matches a competitor that another registered athlete matches too. Not updated.");
                resolved.Remove(person);
            }
            keepEntries.Add(group.Key);
        }
        return resolved;
    }

    private static CompetitorValues Values(Person person, CompetitorValues? before, IReadOnlyDictionary<string, FisAthlete>? fisAthletes, List<string> notes)
    {
        var athlete = person.Athlete;
        var surname = before?.Surname ?? string.Empty;
        var firstName = before?.FirstName ?? string.Empty;
        // The same name in other capitals or order keeps the saved one.
        if (before is null || !SameName(athlete.FullName, firstName, surname))
        {
            var split = Split(athlete.FullName);
            if (!split.Certain && athlete.Code is { } code && fisAthletes is not null && fisAthletes.TryGetValue(code, out var fis)
                && SameName(athlete.FullName, fis.FirstName, fis.Surname))
            { split = (fis.FirstName, fis.Surname, true); }
            (firstName, surname) = (split.FirstName, split.Surname);
            if (!split.Certain) { notes.Add($"Check the first name and surname of {athlete.FullName}."); }
        }
        return new CompetitorValues(surname, firstName, athlete.BirthYear ?? before?.BirthYear, athlete.Code ?? before?.FederationCode,
            athlete.Nation ?? before?.Nation, athlete.Club ?? before?.Club,
            before?.Gender ?? (person.Genders.Count == 1 ? person.Genders.Single() : null));
    }

    // The site writes "First name SURNAME". Lower-case particles directly before the capitals belong to the surname.
    private static (string FirstName, string Surname, bool Certain) Split(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var start = parts.Length;
        while (start > 0 && IsUpper(parts[start - 1])) { start--; }
        if (start == 0 || start == parts.Length) { return (string.Join(' ', parts[..^1]), parts[^1], false); }
        while (start > 1 && char.IsLower(parts[start - 1][0])) { start--; }
        return (string.Join(' ', parts[..start]), string.Join(' ', parts[start..]), true);
    }

    private static bool IsUpper(string value) => value.Any(char.IsLetter)
        && value.Where(char.IsLetter).All(c => char.ToUpperInvariant(c) == c);

    private static bool IsListed(FissiAthlete athlete, CompetitorValues competitor)
        => (athlete.Code is { } code && code.Equals(competitor.FederationCode, StringComparison.OrdinalIgnoreCase))
            || SameName(athlete.FullName, competitor.FirstName, competitor.Surname);

    private static bool SameName(string fullName, string firstName, string surname)
        => fullName.Equals($"{firstName} {surname}".Trim(), StringComparison.OrdinalIgnoreCase)
            || fullName.Equals($"{surname} {firstName}".Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NameKey(string fullName, int? year) => $"{fullName.Trim()}|{year?.ToString(CultureInfo.InvariantCulture)}";

    private static string Describe(FissiAthlete athlete) => athlete.Code is { } code ? $"{athlete.FullName} ({code})" : athlete.FullName;

    private static List<string> Changes(CompetitorValues before, CompetitorValues next)
    {
        var changed = new List<string>();
        if (before.Surname != next.Surname) { changed.Add("Surname"); }
        if (before.FirstName != next.FirstName) { changed.Add("First name"); }
        if (before.BirthYear != next.BirthYear) { changed.Add("Year"); }
        if (before.Gender != next.Gender) { changed.Add("Gender"); }
        if (before.Nation != next.Nation) { changed.Add("Nation"); }
        if (before.Club != next.Club) { changed.Add("Club"); }
        if (before.FederationCode != next.FederationCode) { changed.Add("Code"); }
        return changed;
    }
}

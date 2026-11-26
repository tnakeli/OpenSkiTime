using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class FissiRegistrationTests
{
    private static readonly DateOnly s_date = new(2026, 11, 28);
    private static readonly IReadOnlySet<Guid> s_none = new HashSet<Guid>();

    private static CompetitionDetails Competition(string label, string? codex, string gender = "M",
        Discipline discipline = Discipline.Slalom, DateOnly? date = null)
        => new(Guid.NewGuid(), new CompetitionValues("Synthetic " + label, label, date ?? s_date, discipline, RaceType.Fis, 2, 0, codex,
            Calendar: new(2027, "Test place", "FIN", "FIS", gender)));

    private static SeriesDetails Series(params CompetitionDetails[] competitions)
        => new(Guid.NewGuid(), new("Synthetic", "Test place", "Club", s_date, s_date.AddDays(3), "FIN", "2026/27"), 1, competitions);

    private static CompetitorDetails Competitor(string surname, string firstName, string? code, int? year = 2008,
        Gender? gender = Gender.Male, string? club = "Synthetic Club", string? nation = "FIN")
        => new(Guid.NewGuid(), new(surname, firstName, year, code, nation, club, gender));

    private static CompetitorDeskDetails Desk(SeriesDetails series, IReadOnlyList<CompetitorDetails> competitors,
        params (CompetitorDetails Competitor, CompetitionDetails Competition)[] entries)
        => new(series.Id, series.Revision, competitors, entries.Select(x => new ParticipationDetails(x.Competitor.Id, x.Competition.Id, true, null, null)).ToArray(), []);

    private static FissiCompetition Registered(CompetitionDetails competition, params FissiAthlete[] athletes)
        => new(competition.Id, competition.Values.FisCode, athletes.Length == 0 ? FissiRaceStatus.NoneRegistered : FissiRaceStatus.Ready,
            new(new(competition.Values.FisCode!, competition.Values.Date, "SL", competition.Values.Calendar!.Gender), athletes));

    private static FissiAthlete Person(string name, string? code = null, int? year = 2008, string? club = "Synthetic Club")
        => new(code, name, year, "FIN", club);

    private static FissiRegistrationPlan Plan(SeriesDetails series, CompetitorDeskDetails desk, params FissiCompetition[] registrations)
        => FissiRegistrationPlan.Build(series, desk, registrations, s_none, s_none);

    [Fact]
    public void NewAthletesAreMergedOverTheCompetitionsTheyAreRegisteredIn()
    {
        var first = Competition("SL1", "0507"); var second = Competition("GS1", "0508", discipline: Discipline.GiantSlalom);
        var series = Series(first, second);
        var plan = Plan(series, Desk(series, []), Registered(first, Person("Testi ESIMERKKI", "100001"), Person("Toinen von der ESIMERKKI", "100002")),
            Registered(second, Person("Testi ESIMERKKI", "100001")));
        Assert.Equal(2, plan.Rows.Count);
        Assert.Equal(new CompetitorValues("ESIMERKKI", "Testi", 2008, "100001", "FIN", "Synthetic Club", Gender.Male), plan.Rows[0].Values);
        Assert.Equal([first.Id, second.Id], plan.Rows[0].Entries.Select(x => x.CompetitionId));
        Assert.Equal(("VON DER ESIMERKKI", "Toinen"), (plan.Rows[1].Values.Surname, plan.Rows[1].Values.FirstName));
        Assert.All(plan.Rows, x => { Assert.True(x.IsNew); Assert.False(x.PointsSpecified); Assert.Empty(x.Warnings); });
        Assert.Equal((2, 2, 2, 0), (plan.Races[0].Registered, plan.Races[0].New, plan.Races[0].Added, plan.Races[0].Removed));
        Assert.Equal((1, 1, 1, 0), (plan.Races[1].Registered, plan.Races[1].New, plan.Races[1].Added, plan.Races[1].Removed));
    }

    [Fact]
    public void ExistingDetailsAreOverwrittenAndBlankOnesPreserved()
    {
        var race = Competition("SL1", "0507"); var series = Series(race);
        var changed = Competitor("VANHA", "Nimi", "100001", 2007, club: "Old Club");
        var same = Competitor("ESIMERKKI", "Toinen", "100002");
        var blank = Competitor("ESIMERKKI", "Kolmas", "100003", gender: null);
        var plan = Plan(series, Desk(series, [changed, same, blank], (changed, race), (same, race), (blank, race)),
            Registered(race, Person("Uusi NIMI", "100001"), Person("TOINEN ESIMERKKI", "100002"), new FissiAthlete("100003", "Kolmas ESIMERKKI", null, null, null)));
        Assert.Equal(2, plan.Rows.Count);
        Assert.Equal(new CompetitorValues("NIMI", "Uusi", 2008, "100001", "FIN", "Synthetic Club", Gender.Male), plan.Rows[0].Values);
        Assert.Equal(["Surname", "First name", "Year", "Club"], plan.Rows[0].ChangedFields);
        Assert.Equal(["Gender"], plan.Rows[1].ChangedFields);
        Assert.Equal(blank.Values with { Gender = Gender.Male }, plan.Rows[1].Values);
        Assert.Equal("Up to date", Assert.Single(plan.Races).StatusText);
    }

    [Fact]
    public void RepeatingAnAppliedUpdateProposesNothing()
    {
        var race = Competition("SL1", "0507"); var series = Series(race);
        var known = Competitor("ESIMERKKI", "Testi", "100001");
        var plan = Plan(series, Desk(series, [known], (known, race)), Registered(race, Person("Testi ESIMERKKI", "100001")));
        Assert.Empty(plan.Rows); Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void EntriesNoLongerRegisteredAreProposedForRemovalOnlyInReadCompetitions()
    {
        var race = Competition("SL1", "0507"); var empty = Competition("SL2", "0508"); var missing = Competition("SL3", "0509");
        var series = Series(race, empty, missing);
        var stays = Competitor("ESIMERKKI", "Testi", "100001"); var leaves = Competitor("POISTUVA", "Testi", "100002");
        var plan = Plan(series, Desk(series, [stays, leaves], (stays, race), (leaves, race), (leaves, empty), (leaves, missing)),
            Registered(race, Person("Testi ESIMERKKI", "100001")), Registered(empty), new(missing.Id, "0509", FissiRaceStatus.NotFound));
        var row = Assert.Single(plan.Rows);
        Assert.Equal(leaves.Id, row.CompetitorId);
        Assert.Equal(leaves.Values, row.Values);
        Assert.Equal(new ImportEntryPatch(race.Id, false, false, null), Assert.Single(row.Entries));
        Assert.Equal([1, 0, 0], plan.Races.Select(x => x.Removed));
        Assert.Equal(["Review and save", "None registered", "Not found"], plan.Races.Select(x => x.StatusText));
    }

    [Fact]
    public void CompetitionsAndCompetitorsOnAStartListStayAsTheyAre()
    {
        var drawn = Competition("SL1", "0507"); var open = Competition("SL2", "0508"); var series = Series(drawn, open);
        var starter = Competitor("VANHA", "Nimi", "100001", club: "Old Club"); var leaves = Competitor("POISTUVA", "Testi", "100002");
        var plan = FissiRegistrationPlan.Build(series, Desk(series, [starter, leaves], (starter, drawn), (leaves, drawn)),
            [Registered(drawn, Person("Uusi NIMI", "100001"), Person("Uusi TULOKAS", "100009")), Registered(open, Person("Uusi NIMI", "100001"))],
            new HashSet<Guid> { drawn.Id }, new HashSet<Guid> { starter.Id });
        var row = Assert.Single(plan.Rows);
        Assert.Equal((starter.Id, starter.Values), (row.CompetitorId!.Value, row.Values));
        Assert.Equal(["Entry"], row.ChangedFields);
        Assert.Equal(open.Id, Assert.Single(row.Entries).CompetitionId);
        Assert.Equal((FissiRaceStatus.StartListSaved, (int?)null), (plan.Races[0].Status, plan.Races[0].Registered));
    }

    [Fact]
    public void NameMatchTakesTheCodeAndAChangedCodeNeedsConfirmation()
    {
        var race = Competition("SL1", "0507"); var series = Series(race);
        var withoutCode = Competitor("ESIMERKKI", "Testi", null); var otherCode = Competitor("ESIMERKKI", "Toinen", "100009");
        var plan = Plan(series, Desk(series, [withoutCode, otherCode]), Registered(race, Person("Testi ESIMERKKI", "100001"), Person("Toinen ESIMERKKI", "100002")));
        Assert.Equal([withoutCode.Id, otherCode.Id], plan.Rows.Select(x => x.CompetitorId!.Value));
        Assert.Equal("100001", plan.Rows[0].Values.FederationCode); Assert.Empty(plan.Rows[0].Warnings);
        Assert.Equal("100002", plan.Rows[1].Values.FederationCode);
        Assert.Contains("100009 here and 100002", Assert.Single(plan.Rows[1].Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void UncertainMatchesAreLeftOutAndLoseNoEntry()
    {
        var men = Competition("SL1 M", "0507"); var women = Competition("SL1 W", "5489", "W"); var series = Series(men, women);
        var byCode = Competitor("TOINEN", "Nimi", "100001"); var byName = Competitor("ESIMERKKI", "Testi", "100002");
        var both = Competitor("MOLEMMAT", "Testi", "100003");
        var plan = Plan(series, Desk(series, [byCode, byName, both], (byCode, men), (byName, men), (both, men)),
            Registered(men, Person("Testi ESIMERKKI", "100001"), Person("Testi MOLEMMAT", "100003"), Person("Sama NIMI", "100004"), Person("Sama NIMI", "100005")),
            Registered(women, Person("Testi MOLEMMAT", "100003")));
        Assert.Empty(plan.Rows);
        Assert.Equal(4, plan.Warnings.Count);
        Assert.Equal([0, 0], plan.Races.Select(x => x.Removed + x.Added));
    }

    // Without a code and a birth year a saved competitor could never be found again and would be added on every update.
    [Fact]
    public void AthletesThatCannotBeIdentifiedAreNeitherAddedNorReplaced()
    {
        var race = Competition("SL1 M", "0507"); var series = Series(race);
        var unknownYear = Competitor("ESIMERKKI", "Testi", null, null); var otherYear = Competitor("ESIMERKKI", "Kolmas", null, 2001);
        var plan = Plan(series, Desk(series, [unknownYear, otherYear], (unknownYear, race)), Registered(race,
            Person("Testi ESIMERKKI", null, null), Person("Toinen ESIMERKKI", null, null), Person("Kolmas ESIMERKKI", null, 2008)));
        var row = Assert.Single(plan.Rows);
        Assert.True(row.IsNew); Assert.Equal("Kolmas", row.Values.FirstName);
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Equal(0, plan.Races[0].Removed);
        // A code alone does not tell whether the saved competitor without a birth year is the same person.
        var coded = Plan(series, Desk(series, [unknownYear], (unknownYear, race)), Registered(race, Person("Testi ESIMERKKI", "100001")));
        Assert.Empty(coded.Rows); Assert.Single(coded.Warnings);
    }

    [Fact]
    public void AthletesRepeatedInOneListAreNotMerged()
    {
        var first = Competition("SL1 M", "0507"); var second = Competition("SL2 M", "0508"); var series = Series(first, second);
        var known = Competitor("SAMA", "Nimi", null);
        var plan = Plan(series, Desk(series, [known], (known, first)),
            Registered(first, Person("Nimi SAMA", club: "Club A"), Person("Nimi SAMA", club: "Club B"), Person("Testi KOODI", "100001"), Person("Eri KOODI", "100001"),
                Person("Testi KAHDESTI", "100002"), Person("Testi KAHDESTI", "100002")),
            Registered(second, Person("Nimi SAMA", club: "Club A"), Person("Testi KAHDESTI", "100002")));
        var row = Assert.Single(plan.Rows);
        Assert.Equal("KAHDESTI", row.Values.Surname);
        Assert.Equal([first.Id, second.Id], row.Entries.Select(x => x.CompetitionId));
        Assert.Equal(2, plan.Warnings.Count);
        Assert.Equal(0, plan.Races[0].Removed);
    }

    [Fact]
    public void OneCodeWithDifferentNamesOrYearsInTwoRacesIsNotMerged()
    {
        var first = Competition("SL1 M", "0507"); var second = Competition("SL2 M", "0508"); var series = Series(first, second);
        var plan = Plan(series, Desk(series, []),
            Registered(first, Person("Testi EKA", "100001"), Person("Testi VUOSI", "100002"), Person("Testi SEURA", "100003", club: "Club A")),
            Registered(second, Person("Testi TOKA", "100001"), Person("Testi VUOSI", "100002", 2009), Person("Testi SEURA", "100003", club: "Club B")));
        Assert.Equal("SEURA", Assert.Single(plan.Rows).Values.Surname);
        Assert.Equal(2, plan.Warnings.Count);
    }

    // Nobody whose code or name is on the list is proposed for removal, however unclear the match is.
    [Fact]
    public void AListedNameOrCodeKeepsTheEntry()
    {
        var race = Competition("SL1 M", "0507"); var series = Series(race);
        var first = Competitor("EKA", "Testi", null); var second = Competitor("TOKA", "Testi", null);
        var sharedCode = Plan(series, Desk(series, [first, second], (first, race), (second, race)),
            Registered(race, Person("Testi EKA", "100001"), Person("Testi TOKA", "100001")));
        Assert.Empty(sharedCode.Rows); Assert.Equal(0, sharedCode.Races[0].Removed); Assert.NotEmpty(sharedCode.Warnings);

        var dated = Competitor("SAMA", "Nimi", null); var undated = Competitor("SAMA", "Nimi", null, null);
        var missingYear = Plan(series, Desk(series, [dated, undated], (dated, race), (undated, race)), Registered(race, Person("Nimi SAMA")));
        Assert.Empty(missingYear.Rows); Assert.Equal(0, missingYear.Races[0].Removed); Assert.Single(missingYear.Warnings);

        // The registered athlete is certain by code; a namesake keeps the entry and is named for a check.
        var coded = Competitor("SAMA", "Nimi", "100001"); var namesake = Competitor("SAMA", "Nimi", "100002", 1999);
        var certain = Plan(series, Desk(series, [coded, namesake], (coded, race), (namesake, race)), Registered(race, Person("Nimi SAMA", "100001")));
        Assert.Empty(certain.Rows); Assert.Equal(0, certain.Races[0].Removed);
        Assert.Contains("The entry was kept", Assert.Single(certain.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentCodeNeedsConfirmationAlsoWhenTheDetailsAreLocked()
    {
        var race = Competition("SL2", "0508"); var series = Series(race);
        var starter = Competitor("ESIMERKKI", "Testi", "100009");
        var plan = FissiRegistrationPlan.Build(series, Desk(series, [starter]), [Registered(race, Person("Testi ESIMERKKI", "100001"))],
            s_none, new HashSet<Guid> { starter.Id });
        var row = Assert.Single(plan.Rows);
        Assert.Equal(starter.Values, row.Values);
        Assert.Contains("confirm this match", Assert.Single(row.Warnings), StringComparison.Ordinal);
        Assert.Empty(row.ReviewNotes);
    }

    [Fact]
    public void AnEntryOfTheOtherGenderAndInvalidDetailsAreReportedAndNotStaged()
    {
        var race = Competition("SL1 M", "0507"); var series = Series(race);
        var woman = Competitor("ESIMERKKI", "Testi", "100001", gender: Gender.Female); var entered = Competitor("VIRHE", "Vuosi", "100002");
        var plan = Plan(series, Desk(series, [woman, entered], (entered, race)), Registered(race, Person("Testi ESIMERKKI", "100001"),
            Person("Vuosi VIRHE", "100002", 2090), Person("Uusi VIRHE", "100003", 2090)));
        Assert.Empty(plan.Rows);
        Assert.Equal(3, plan.Warnings.Count);
        Assert.Contains(plan.Warnings, x => x.Contains("The entry was not added", StringComparison.Ordinal));
    }

    [Fact]
    public void AGenderThatDoesNotFitAnotherEntryIsNotFilledIn()
    {
        var men = Competition("SL1 M", "0507"); var women = Competition("SL1 W", "5489", "W"); var series = Series(men, women);
        var known = Competitor("ESIMERKKI", "Testi", "100001", gender: null, club: "Old Club");
        var plan = Plan(series, Desk(series, [known], (known, men), (known, women)), Registered(men, Person("Testi ESIMERKKI", "100001")),
            new FissiCompetition(women.Id, "5489", FissiRaceStatus.NotFound));
        Assert.Equal(["Club"], Assert.Single(plan.Rows).ChangedFields);
        Assert.Null(plan.Rows[0].Values.Gender);
        Assert.Contains("The gender was not filled in", Assert.Single(plan.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void NamesInCapitalsUseTheFisListOrGetANoteThatDoesNotBlockSave()
    {
        var race = Competition("SL1", "0507"); var series = Series(race); var desk = Desk(series, []);
        var registered = Registered(race, Person("TESTI KAKSI ESIMERKKI", "100001"), Person("J. Matti ESIMERKKI", "100002"));
        var unknown = Plan(series, desk, registered);
        Assert.Equal(("ESIMERKKI", "TESTI KAKSI"), (unknown.Rows[0].Values.Surname, unknown.Rows[0].Values.FirstName));
        // A name split that may be wrong is a note, not a match to confirm: the competitor can be saved unfinished.
        Assert.Contains("Check the first name", Assert.Single(unknown.Rows[0].ReviewNotes), StringComparison.Ordinal);
        Assert.Empty(unknown.Rows[0].Warnings);
        Assert.Equal(("ESIMERKKI", "J. Matti"), (unknown.Rows[1].Values.Surname, unknown.Rows[1].Values.FirstName));
        Assert.Empty(unknown.Rows[1].Warnings); Assert.Empty(unknown.Rows[1].ReviewNotes);
        var list = new Dictionary<string, FisAthlete> { ["100001"] = new("100001", "KAKSI ESIMERKKI", "Testi", 2008, Gender.Male, "FIN", null, null) };
        var known = FissiRegistrationPlan.Build(series, desk, [registered], s_none, s_none, list);
        Assert.Equal(("KAKSI ESIMERKKI", "Testi"), (known.Rows[0].Values.Surname, known.Rows[0].Values.FirstName));
        Assert.Empty(known.Rows[0].Warnings); Assert.Empty(known.Rows[0].ReviewNotes);
    }
}

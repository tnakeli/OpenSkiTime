using System.Text.Json;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class FlexStartOrderTests
{
    private static readonly CompetitionValues s_race = new("Synthetic youth race", "YR1", new(2026, 11, 7), Discipline.GiantSlalom, RaceType.Club, 2, 0);
    private static readonly DateTimeOffset s_at = new(2026, 11, 6, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid s_competition = Guid.Parse("20000000-0000-0000-0000-000000000001");

    private static DrawEntrant Entrant(int i, string className, Gender gender, decimal? points = null, bool code = true)
        => new(new Guid(i, 0, 0, new byte[8]), new($"ATHLETE{i}", "Test", 2014, code ? $"{200000 + i}" : null, null, "Synthetic", gender), points, className);

    private static DrawEntrant[] Class(string name, Gender gender, int from, int count, Func<int, decimal?>? points = null)
        => Enumerable.Range(from, count).Select(i => Entrant(i, name, gender, points?.Invoke(i))).ToArray();

    private static StartBlock Block(string name, string seed, FirstRunSetting? first = null, LaterRunSetting? later = null, int? firstBib = null, params string[] classes)
        => new(name, classes.Length == 0 ? [name] : classes, first ?? new(), later ?? new(), seed, firstBib);

    private static StartListPlan Draw(IReadOnlyList<DrawEntrant> entrants, StartOrderSetup setup, Gender? gender = null, CompetitionValues? race = null, DrawOptions? options = null)
        => FlexStartOrder.FirstRun(s_competition, race ?? s_race, gender, entrants, PointsListSource.None, options ?? new(), setup);

    private static StartListRevision Stored(StartListPlan plan) => new(Guid.NewGuid(), 1, s_at, null, "Test operator", "Fixture", plan);

    private static RunFinish[] Results(StartListPlan plan, Func<int, StartListEntry, RunFinish?> status)
        => plan.Entries.Select((e, i) => status(i, e) ?? new RunFinish(e.Entrant.CompetitorId, FinishStatus.Finished, 5000 + i)).ToArray();

    [Fact]
    public void ClassesAreListedAsBlocksInTheChosenOrderWithContinuousBibsAndIndependentSeeds()
    {
        var entrants = Class("N10", Gender.Female, 1, 6).Concat(Class("M10", Gender.Male, 11, 5)).Concat(Class("N12", Gender.Female, 21, 4)).ToArray();
        var setup = new StartOrderSetup([Block("M10", "seed-m10"), Block("N12", "seed-n12"), Block("N10", "seed-n10")]);
        var plan = Draw(entrants, setup);
        Assert.Equal(["M10", "N12", "N10"], plan.Entries.Select(x => x.Entrant.Class).Distinct());
        Assert.Equal(Enumerable.Range(1, 15), plan.Entries.Select(x => x.Bib));
        Assert.Null(plan.Gender);
        Assert.Equal(plan.Entries, Draw(entrants.Reverse().ToArray(), setup).Entries);
        // Drawing one class again leaves the others untouched.
        var redrawn = Draw(entrants, new([setup.Blocks[0], setup.Blocks[1] with { Seed = "another" }, setup.Blocks[2]]));
        Assert.Equal(plan.Entries.Where(x => x.Entrant.Class != "N12").Select(x => x.Entrant.CompetitorId),
            redrawn.Entries.Where(x => x.Entrant.Class != "N12").Select(x => x.Entrant.CompetitorId));
        Assert.NotEqual(plan.Entries.Where(x => x.Entrant.Class == "N12").Select(x => x.Entrant.CompetitorId),
            redrawn.Entries.Where(x => x.Entrant.Class == "N12").Select(x => x.Entrant.CompetitorId));
    }

    [Fact]
    public void MergedClassesAreDrawnAsOneGroupAndBlocksCanStartAtOwnBibs()
    {
        var entrants = Class("N10", Gender.Female, 1, 8).Concat(Class("M10", Gender.Male, 11, 8)).Concat(Class("N12", Gender.Female, 21, 3)).ToArray();
        var setup = new StartOrderSetup([Block("N10 + M10", "merged", classes: ["N10", "M10"]), Block("N12", "n12", firstBib: 101)]);
        var plan = Draw(entrants, setup, options: new(SkippedBibs: [4]));
        // Both classes are drawn into one group, so the first eight starters are not simply the whole first class.
        Assert.Equal(["N10", "M10"], plan.Entries.Take(16).Select(x => x.Entrant.Class).Distinct().Order(StringComparer.Ordinal).Reverse());
        Assert.NotEqual(Enumerable.Repeat("N10", 8), plan.Entries.Take(8).Select(x => x.Entrant.Class));
        Assert.Equal([1, 2, 3, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 101, 102, 103], plan.Entries.Select(x => x.Bib));
        Assert.Throws<DomainValidationException>(() => Draw(entrants, new([setup.Blocks[0] with { FirstBib = 50 }, setup.Blocks[1] with { FirstBib = 20 }])));
    }

    [Fact]
    public void ByPointsDrawsTheBestGroupThenFollowsHigherPointsAndDrawsUnpointedLast()
    {
        // Higher is better; 0 counts as no points. 20 competitors with points 100..82 (distinct), two ties and four without points.
        var entrants = Class("M16", Gender.Male, 1, 20, i => i switch { <= 4 => 0m, 5 => 50m, 6 => 50m, _ => 100m - i }).ToArray();
        entrants[18] = entrants[18] with { Points = null };
        var rule = new FirstRunSetting(FirstRunKind.ByPoints, 3, PointsDirection.HigherIsBetter, ZeroIsNoPoints: true);
        var plan = FlexStartOrder.FirstRun(s_competition, s_race, null, entrants, new("CUP", new(2026, 1, 1), new(2026, 12, 31)), new(),
            new([Block("M16", "seed", rule)]));
        var group = plan.Entries.Select(x => x.Group).ToArray();
        Assert.Equal(3, group.Count(x => x == "First group"));
        Assert.Equal(new decimal?[] { 93m, 92m, 91m }, plan.Entries.Take(3).Select(x => x.Entrant.Points).OrderByDescending(x => x));
        var tail = plan.Entries.Where(x => x.Group == "No points").ToArray();
        Assert.Equal(5, tail.Length);
        Assert.Equal(tail, plan.Entries.TakeLast(5));
        var ordered = plan.Entries.Where(x => x.Group == "Points order").Select(x => x.Entrant.Points!.Value).ToArray();
        Assert.Equal(ordered.OrderByDescending(x => x), ordered);
        Assert.Throws<DomainValidationException>(() => Draw(entrants, new([Block("M16", "seed", rule)])));
    }

    [Fact]
    public void ByPointsWithoutAnyPointsDrawsEveryone()
    {
        var entrants = Class("N14", Gender.Female, 1, 12).ToArray();
        var plan = FlexStartOrder.FirstRun(s_competition, s_race, null, entrants, new("L", new(2026, 1, 1), new(2026, 12, 31)), new(),
            new([Block("N14", "s", new(FirstRunKind.ByPoints))]));
        Assert.All(plan.Entries, x => Assert.Equal("No points", x.Group));
        Assert.Equal(12, plan.Entries.Select(x => x.Entrant.CompetitorId).Distinct().Count());
    }

    [Fact]
    public void ReverseStartOrderNeedsNoResultsAndKeepsEveryoneExceptWhoDidNotStart()
    {
        var entrants = Class("N10", Gender.Female, 1, 5).Concat(Class("M10", Gender.Male, 11, 4)).ToArray();
        var first = Stored(Draw(entrants, new([Block("N10", "a"), Block("M10", "b")])));
        var later = new StartOrderSetup([Block("N10", "", later: new()), Block("M10", "", later: new())]);
        var second = FlexStartOrder.LaterRun(first, [], later);
        Assert.Equal(first.Plan.Entries.Take(5).Reverse().Select(x => x.Bib), second.Entries.Take(5).Select(x => x.Bib));
        Assert.Equal(first.Plan.Entries.Skip(5).Reverse().Select(x => x.Bib), second.Entries.Skip(5).Select(x => x.Bib));
        Assert.All(second.Entries, x => Assert.Equal(first.Plan.Entries.Single(y => y.Entrant.CompetitorId == x.Entrant.CompetitorId).Bib, x.Bib));
        Assert.Equal(first.Id, second.SourceListId);
        // With results known, DNF and DSQ keep their place (their number's place) and DNS/NPS stay out.
        var results = Results(first.Plan, (i, e) => i switch
        {
            1 => new(e.Entrant.CompetitorId, FinishStatus.DNS, null), 2 => new(e.Entrant.CompetitorId, FinishStatus.DNF, null),
            6 => new(e.Entrant.CompetitorId, FinishStatus.DSQ, null), _ => null,
        });
        var known = FlexStartOrder.LaterRun(first, results, later);
        Assert.Equal(first.Plan.Entries.Count - 1, known.Entries.Count);
        Assert.DoesNotContain(known.Entries, x => x.Entrant.CompetitorId == first.Plan.Entries[1].Entrant.CompetitorId);
        Assert.Contains(known.Entries, x => x.Entrant.CompetitorId == first.Plan.Entries[2].Entrant.CompetitorId);
        Assert.Contains(known.Entries, x => x.Entrant.CompetitorId == first.Plan.Entries[6].Entrant.CompetitorId);
        Assert.Equal(first.Plan.Entries.Skip(5).Reverse().Select(x => x.Bib), known.Entries.Skip(4).Select(x => x.Bib));
    }

    [Fact]
    public void ReverseResultsStartsTheBestNLastAndPutsDnfAndDsqLastOrNotAtAll()
    {
        var entrants = Class("M16", Gender.Male, 1, 10).ToArray();
        var first = Stored(Draw(entrants, new([Block("M16", "s")])));
        var results = Results(first.Plan, (i, e) => i switch
        {
            3 => new(e.Entrant.CompetitorId, FinishStatus.DNF, null), 5 => new(e.Entrant.CompetitorId, FinishStatus.DSQ, null),
            7 => new(e.Entrant.CompetitorId, FinishStatus.DNS, null), _ => null,
        });
        var bibsByTime = first.Plan.Entries.Where((_, i) => i is not (3 or 5 or 7)).Select(x => x.Bib).ToArray();
        var last = new StartOrderSetup([Block("M16", "", later: new(LaterRunKind.ReverseResults, 3, NonFinishers.StartLast))]);
        var second = FlexStartOrder.LaterRun(first, results, last);
        // Result order is the position order here; the best three (positions 0, 1, 2) are reversed, the rest follow.
        Assert.Equal([bibsByTime[2], bibsByTime[1], bibsByTime[0], .. bibsByTime.Skip(3), first.Plan.Entries[3].Bib, first.Plan.Entries[5].Bib],
            second.Entries.Select(x => x.Bib));
        var excluded = FlexStartOrder.LaterRun(first, results, new([Block("M16", "", later: new(LaterRunKind.ReverseResults, null, NonFinishers.DoNotStart))]));
        Assert.Equal(bibsByTime.Reverse(), excluded.Entries.Select(x => x.Bib));
        Assert.Throws<DomainValidationException>(() => FlexStartOrder.LaterRun(first, [], last));
        Assert.Throws<DomainValidationException>(() => FlexStartOrder.LaterRun(first, results.Skip(1).ToArray(), last));
    }

    [Fact]
    public void TiedTimesAtTheReversalBoundaryJoinTheReversedGroup()
    {
        var first = Stored(Draw(Class("N12", Gender.Female, 1, 6), new([Block("N12", "s")])));
        var results = first.Plan.Entries.Select((e, i) => new RunFinish(e.Entrant.CompetitorId, FinishStatus.Finished, i is 2 or 3 ? 5002 : 5000 + i)).ToArray();
        var second = FlexStartOrder.LaterRun(first, results, new([Block("N12", "", later: new(LaterRunKind.ReverseResults, 3))]));
        Assert.Equal(4, second.Entries.Count(x => x.Group == "Reversed results"));
    }

    [Fact]
    public void ManyRunsFollowEachOtherAndSameOrderRepeats()
    {
        var race = s_race with { RunCount = 20 };
        var plan = Draw(Class("M12", Gender.Male, 1, 5), new([Block("M12", "s")]), race: race);
        var list = Stored(plan);
        var order = new[] { plan.Entries.Select(x => x.Bib).ToArray() };
        for (var run = 2; run <= 20; run++)
        {
            var kind = run % 5 == 0 ? LaterRunKind.SameOrder : LaterRunKind.ReverseStartOrder;
            var next = FlexStartOrder.LaterRun(list, [], new([Block("M12", "", later: new(kind))]));
            Assert.Equal(run, next.RunNumber);
            var previous = list.Plan.Entries.Select(x => x.Bib).ToArray();
            Assert.Equal(kind == LaterRunKind.SameOrder ? previous : previous.Reverse(), next.Entries.Select(x => x.Bib));
            list = Stored(next);
        }
        Assert.Throws<DomainValidationException>(() => FlexStartOrder.LaterRun(list, [], new([Block("M12", "")])));
    }

    [Fact]
    public void SetupMustCoverEveryClassExactlyOnce()
    {
        var entrants = Class("N10", Gender.Female, 1, 3).Concat(Class("M10", Gender.Male, 11, 3)).ToArray();
        Assert.Throws<DomainValidationException>(() => Draw(entrants, new([Block("N10", "a")])));
        Assert.Throws<DomainValidationException>(() => Draw(entrants, new([Block("N10", "a"), Block("M10", "b"), Block("again", "c", classes: ["N10"])])));
        Assert.Throws<DomainValidationException>(() => Draw(entrants, new([Block("N10", ""), Block("M10", "b")])));
        Assert.Throws<DomainValidationException>(() => Draw(entrants, new([]), null));
        Assert.Throws<DomainValidationException>(() => Draw(entrants.Where(x => x.Class == "N10").ToArray(), new([Block("N10", "a")]), Gender.Male));
        Assert.Throws<DomainValidationException>(() => Draw(entrants, new([Block("N10", "a"), Block("M10", "b")]), race: s_race with { RunCount = 100 }));
        // Athletes without FIS code or nation can be drawn.
        var anonymous = new[] { Entrant(1, "N10", Gender.Female, code: false), Entrant(2, "N10", Gender.Female, code: false) };
        Assert.Equal(2, Draw(anonymous, new([Block("N10", "a")])).Entries.Count);
    }



    [Fact]
    public void DefaultClassesFollowTheSeasonAndNeverOverlap()
    {
        // 2025/26: the age classes of the 2025/26 rules; each season every birth year moves on by one.
        var last = DefaultCategories.ForSeason(2026);
        (int From, int To) Years(string label) { var r = last.Single(x => x.Label == label); return (r.BirthYearMin, r.BirthYearMax); }
        Assert.Equal((2016, 2018), Years("N10")); Assert.Equal((2014, 2015), Years("M12")); Assert.Equal((2012, 2013), Years("N14"));
        Assert.Equal((2010, 2011), Years("M16")); Assert.Equal((2008, 2009), Years("N18")); Assert.Equal((2005, 2007), Years("M21"));
        var now = DefaultCategories.ForSeason(2027);
        Assert.Equal((2017, 2019), (now.Single(x => x.Label == "N10").BirthYearMin, now.Single(x => x.Label == "N10").BirthYearMax));
        Assert.Equal(14, now.Count);
        Assert.All(now, x => Assert.Equal(x, x.Validated(2027)));
        var rules = now.Select((x, i) => new CategoryRuleDetails(Guid.NewGuid(), x)).ToArray();
        foreach (var gender in new[] { Gender.Female, Gender.Male })
        {
            for (var year = 1950; year <= 2019; year++)
            {
                var label = CategoryResolver.Resolve(new("A", "B", year, null, null, null, gender), rules);
                Assert.StartsWith(gender == Gender.Female ? "N" : "M", label, StringComparison.Ordinal);
            }
        }
        Assert.Equal(CategoryResolver.Unclassified, CategoryResolver.Resolve(new("A", "B", 2020, null, null, null, Gender.Female), rules));
    }
    [Fact]
    public async Task PointsColumnIsPreviewedAndTheGeneralValueWinsOverDisciplinePoints()
    {
        var points = new CompetitorFisPoints("L", 10m, 20m, 30m, 40m, null);
        Assert.Equal(30m, points.For("SL"));
        Assert.Equal(99.5m, (points with { All = 99.5m }).For("SL"));
        Assert.Equal(99.5m, (points with { All = 99.5m }).For("AC"));
        Assert.Throws<DomainValidationException>(() => (points with { All = 1.234m }).Validated());
        var root = Path.Combine(Path.GetTempPath(), "openskitime-flex-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(root, "p.ost"), new("Synthetic", "Test", "Test", s_race.Date, s_race.Date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, s_race, series.Revision);
            await workspace.SaveDeskRowAsync(null, new("ONE", "Racer", 2014, null, null, null, Gender.Male), series.Competitions[0].Id, true, null, series.Revision);
            var desk = await workspace.ReadCompetitorDeskAsync();
            var preview = TsvExchange.Preview("Surname\tFirst name\tYear\tPoints\nONE\tRacer\t2014\t12,5\nTWO\tRacer\t2014\t\nTHREE\tRacer\t2014\t~",
                await workspace.ReadAsync(), desk);
            Assert.Equal([12.5m, null, null], preview.Rows.Select(x => x.Points));
            Assert.Equal([true, false, true], preview.Rows.Select(x => x.PointsSpecified));
            Assert.Contains("Points", preview.Rows[0].ChangedFields);
            var current = await workspace.ReadAsync();
            Assert.Throws<DomainValidationException>(() => TsvExchange.Preview("Surname\tPoints\nONE\t1.234", current, desk));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task MixedClubRaceIsSavedReplayedAndTransferredThroughManyRuns()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-flex-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var race = s_race with { RunCount = 3 };
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(Path.Combine(root, "mixed.ost"), new("Synthetic", "Test", "Test", race.Date, race.Date, "FIN", "2026/27"));
            series = await workspace.SaveCompetitionAsync(null, race, series.Revision);
            var competitionId = series.Competitions[0].Id;
            var entrants = new List<DrawEntrant>();
            var revision = series.Revision;
            foreach (var e in Class("N10", Gender.Female, 1, 4).Concat(Class("M10", Gender.Male, 11, 4)))
            {
                var saved = await workspace.SaveDeskRowAsync(null, e.Athlete, competitionId, true, null, revision);
                revision = saved.Revision;
                entrants.Add(e with { CompetitorId = saved.Value.Id });
            }
            var setup = new StartOrderSetup([Block("N10", "seed-a"), Block("M10", "seed-b")]);
            var plan = FlexStartOrder.FirstRun(competitionId, race, null, entrants, PointsListSource.None, new(), setup);
            var tampered = plan with { Entries = plan.Entries.Reverse().Select((x, i) => x with { Position = i + 1 }).ToArray() };
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(tampered, revision, "Operator", "Edited", s_at)));
            var desk = await workspace.SaveStartListAsync(new(plan, revision, "Operator", "Draw", s_at));
            var first = Assert.Single(desk.Revisions);
            Assert.Null(first.Plan.Gender);
            desk = await workspace.MarkRunStartedAsync(first.Id, desk.SeriesRevision, "Starter", s_at);
            // A second list for one gender cannot be added beside the list for all genders.
            var men = FlexStartOrder.FirstRun(competitionId, race, Gender.Male, entrants.Where(x => x.Athlete.Gender == Gender.Male).ToArray(),
                PointsListSource.None, new(FirstBib: 50), new([Block("M10", "x")]));
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(men, desk.SeriesRevision, "Operator", "Men only", s_at)));
            var second = FlexStartOrder.LaterRun(desk.Revisions.Single(), [], new([Block("M10", "", later: new()), Block("N10", "", later: new())]));
            desk = await workspace.SaveStartListAsync(new(second, desk.SeriesRevision, "Operator", "Run 2", s_at));
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveStartListAsync(new(plan, desk.SeriesRevision, "Operator", "Redraw", s_at)));
            var secondStored = desk.Revisions.Single(x => x.Plan.RunNumber == 2);
            desk = await workspace.MarkRunStartedAsync(secondStored.Id, desk.SeriesRevision, "Starter", s_at);
            var third = FlexStartOrder.LaterRun(desk.Revisions.Single(x => x.Plan.RunNumber == 2), [], new([Block("N10", "", later: new(LaterRunKind.SameOrder)), Block("M10", "", later: new(LaterRunKind.SameOrder))]));
            desk = await workspace.SaveStartListAsync(new(third, desk.SeriesRevision, "Operator", "Run 3", s_at));
            Assert.Equal(3, desk.Revisions.Count);
            var copy = Path.Combine(root, "transferred.ost");
            await workspace.BackupAsync(copy);
            await workspace.OpenAsync(copy);
            var reopened = (await workspace.ReadStartListsAsync(competitionId)).Revisions;
            Assert.Equal(JsonSerializer.Serialize(third), JsonSerializer.Serialize(reopened.Single(x => x.Plan.RunNumber == 3).Plan));
            // A FIS competition refuses the flexible start order.
            series = await workspace.ReadAsync();
            await Assert.ThrowsAsync<DomainValidationException>(() => workspace.SaveCompetitionAsync(competitionId, race with { RaceType = RaceType.Fis, FisCode = "1" }, series.Revision));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

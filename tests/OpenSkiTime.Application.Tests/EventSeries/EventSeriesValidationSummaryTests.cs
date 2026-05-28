using OpenSkiTime.Application.Series;
using OpenSkiTime.Application.Tests.Fakes;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitors;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.EventSeriesTests;

public class EventSeriesValidationSummaryTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    private static EventSeries NewSeries() =>
        EventSeries.Create(SeriesId, "S", "L", "O",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2),
            "FIN", "2025/26", DateTime.UtcNow);

    private static EventSeriesValidationSummaryService Sut(EventSeries series)
        => new(new FakeEventSeriesRepository(series));

    [Fact]
    public async Task Valid_competitors_report_zero_missing()
    {
        var series = NewSeries();
        series.AddCompetitor(Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005));
        series.AddCompetitor(Competitor.Create(Guid.NewGuid(), SeriesId, "JONES", "Alice", 2007));

        var summary = await Sut(series).GetAsync(SeriesId);

        summary!.TotalCompetitors.Should().Be(2);
        summary.CompetitorsWithMissingData.Should().Be(0);
    }

    [Fact]
    public async Task Returns_null_for_unknown_series()
    {
        var series = NewSeries();
        var summary = await Sut(series).GetAsync(Guid.NewGuid());
        summary.Should().BeNull();
    }

    [Fact]
    public async Task Series_with_competitions_and_no_issues_reports_empty_missing_list()
    {
        var series = NewSeries();
        var competition = OpenSkiTime.Domain.Competitions.Competition.Create(
            Guid.NewGuid(), SeriesId, "Club Race", "3.1 SL",
            new DateOnly(2026, 1, 1), Discipline.SL, RaceType.Club, 2, 0);
        series.AddCompetition(competition);

        var summary = await Sut(series).GetAsync(SeriesId);

        summary!.CompetitionsWithMissingData.Should().BeEmpty();
    }

    [Fact]
    public async Task Total_competitor_count_matches_series_competitor_count()
    {
        var series = NewSeries();
        series.AddCompetitor(OpenSkiTime.Domain.Competitors.Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005));
        series.AddCompetitor(OpenSkiTime.Domain.Competitors.Competitor.Create(Guid.NewGuid(), SeriesId, "JONES", "Alice", 2007));

        var summary = await Sut(series).GetAsync(SeriesId);

        summary!.TotalCompetitors.Should().Be(2);
        summary.CompetitorsWithMissingData.Should().Be(0);
    }
}

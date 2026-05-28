using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitors;
using OpenSkiTime.Domain.Series;
using OpenSkiTime.Application.Tests.Fakes;

namespace OpenSkiTime.Application.Tests.Competitors;

public class EditCompetitorUseCaseTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    private static (EditCompetitorUseCase, FakeEventSeriesRepository) Make(EventSeries series)
    {
        var repo = new FakeEventSeriesRepository(series);
        var uow = new FakeUnitOfWork();
        return (new EditCompetitorUseCase(repo, uow), repo);
    }

    private static EventSeries SeriesWithCompetitor(out Competitor competitor)
    {
        var series = EventSeries.Create(SeriesId, "S", "L", "O",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), "FIN", "2025/26",
            DateTime.UtcNow);
        competitor = Competitor.Create(Guid.NewGuid(), SeriesId,
            "smith", "John", 2005, gender: Gender.Male);
        series.AddCompetitor(competitor);
        return series;
    }

    [Fact]
    public async Task Updates_personal_data_and_uppercases_last_name()
    {
        var series = SeriesWithCompetitor(out var competitor);
        var (sut, _) = Make(series);

        var result = await sut.ExecuteAsync(new EditCompetitorRequest
        {
            EventSeriesId = SeriesId,
            CompetitorId = competitor.Id,
            LastName = "jones",
            FirstName = "Alice",
            YearOfBirth = 2006,
            NationCode = "GBR",
            Gender = Gender.Female,
        });

        result.Succeeded.Should().BeTrue();
        competitor.LastName.Value.Should().Be("JONES");
        competitor.FirstName.Should().Be("Alice");
        competitor.YearOfBirth.Should().Be(2006);
        competitor.Gender.Should().Be(Gender.Female);
    }

    [Fact]
    public async Task Returns_failure_when_series_not_found()
    {
        var series = SeriesWithCompetitor(out var competitor);
        var (sut, _) = Make(series);

        var result = await sut.ExecuteAsync(new EditCompetitorRequest
        {
            EventSeriesId = Guid.NewGuid(),
            CompetitorId = competitor.Id,
            LastName = "Jones",
            FirstName = "Alice",
            YearOfBirth = 2006,
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task Returns_failure_when_competitor_not_found()
    {
        var series = SeriesWithCompetitor(out _);
        var (sut, _) = Make(series);

        var result = await sut.ExecuteAsync(new EditCompetitorRequest
        {
            EventSeriesId = SeriesId,
            CompetitorId = Guid.NewGuid(),
            LastName = "Jones",
            FirstName = "Alice",
            YearOfBirth = 2006,
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task Returns_failure_on_invalid_first_name()
    {
        var series = SeriesWithCompetitor(out var competitor);
        var (sut, _) = Make(series);

        var result = await sut.ExecuteAsync(new EditCompetitorRequest
        {
            EventSeriesId = SeriesId,
            CompetitorId = competitor.Id,
            LastName = "Jones",
            FirstName = "   ",
            YearOfBirth = 2006,
        });

        result.Succeeded.Should().BeFalse();
    }
}

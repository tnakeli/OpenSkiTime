using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Domain.Competitors;
using OpenSkiTime.Domain.Series;
using OpenSkiTime.Application.Tests.Fakes;

namespace OpenSkiTime.Application.Tests.Competitors;

public class CompetitorCodeUniqueWithinSeriesTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    private static (AddCompetitorUseCase, FakeEventSeriesRepository) Make(EventSeries series)
    {
        var repo = new FakeEventSeriesRepository(series);
        var uow = new FakeUnitOfWork();
        return (new AddCompetitorUseCase(repo, uow), repo);
    }

    private static EventSeries SeriesWithCompetitor(string fisCode)
    {
        var series = EventSeries.Create(SeriesId, "S", "L", "O",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), "FIN", "2025/26",
            DateTime.UtcNow);
        var competitor = Competitor.Create(Guid.NewGuid(), SeriesId,
            "SMITH", "John", 2005, fisCode: fisCode);
        series.AddCompetitor(competitor);
        return series;
    }

    [Fact]
    public async Task Adding_duplicate_name_and_yob_returns_failure()
    {
        var series = SeriesWithCompetitor("1234567");
        var (sut, _) = Make(series);

        var result = await sut.ExecuteAsync(new AddCompetitorRequest
        {
            EventSeriesId = SeriesId,
            LastName = "SMITH",
            FirstName = "John",
            YearOfBirth = 2005,
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists");
    }

    [Fact]
    public async Task Adding_same_name_different_yob_succeeds()
    {
        var series = SeriesWithCompetitor("");
        var (sut, _) = Make(series);

        var result = await sut.ExecuteAsync(new AddCompetitorRequest
        {
            EventSeriesId = SeriesId,
            LastName = "SMITH",
            FirstName = "John",
            YearOfBirth = 2006,
        });

        result.Succeeded.Should().BeTrue();
    }
}

using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Application.Tests.Fakes;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Competitors;

public class RemoveCompetitorUseCaseTests
{
    private static (RemoveCompetitorUseCase sut, FakeEventSeriesRepository repo, FakeUnitOfWork uow, EventSeries series) BuildSut()
    {
        var repo = new FakeEventSeriesRepository();
        var uow = new FakeUnitOfWork();
        var series = EventSeries.Create(
            Guid.NewGuid(), "Winter Cup", "Lahti", "Club",
            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 12),
            "FIN", "2025/26", DateTime.UtcNow);
        repo.AddAsync(series).GetAwaiter().GetResult();
        return (new RemoveCompetitorUseCase(repo, uow), repo, uow, series);
    }

    [Fact]
    public async Task Removes_existing_competitor_and_saves()
    {
        var (sut, repo, uow, series) = BuildSut();

        var addUseCase = new AddCompetitorUseCase(repo, uow);
        var addResult = await addUseCase.ExecuteAsync(new AddCompetitorRequest
        {
            EventSeriesId = series.Id,
            LastName = "SMITH", FirstName = "John", YearOfBirth = 2005,
        });
        uow.Reset();

        var result = await sut.ExecuteAsync(series.Id, addResult.Value);

        result.Succeeded.Should().BeTrue();
        uow.SaveCount.Should().Be(1);

        var loaded = await repo.GetByIdAsync(series.Id);
        loaded!.Competitors.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_series_returns_failure()
    {
        var (sut, _, uow, _) = BuildSut();
        var result = await sut.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid());
        result.Succeeded.Should().BeFalse();
        uow.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_competitor_returns_failure()
    {
        var (sut, _, uow, series) = BuildSut();
        var result = await sut.ExecuteAsync(series.Id, Guid.NewGuid());
        result.Succeeded.Should().BeFalse();
        uow.SaveCount.Should().Be(0);
    }
}

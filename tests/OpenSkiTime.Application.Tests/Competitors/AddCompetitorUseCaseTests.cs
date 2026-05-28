using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Application.Tests.Fakes;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Competitors;

public class AddCompetitorUseCaseTests
{
    private static (AddCompetitorUseCase sut, FakeEventSeriesRepository repo, FakeUnitOfWork uow, EventSeries series) BuildSut()
    {
        var repo = new FakeEventSeriesRepository();
        var uow = new FakeUnitOfWork();
        var series = EventSeries.Create(
            Guid.NewGuid(), "Winter Cup", "Lahti", "Club",
            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 12),
            "FIN", "2025/26", DateTime.UtcNow);
        repo.AddAsync(series).GetAwaiter().GetResult();
        return (new AddCompetitorUseCase(repo, uow), repo, uow, series);
    }

    [Fact]
    public async Task Valid_request_adds_competitor_and_saves()
    {
        var (sut, repo, uow, series) = BuildSut();

        var request = new AddCompetitorRequest
        {
            EventSeriesId = series.Id,
            LastName = "smith",
            FirstName = "John",
            YearOfBirth = 2005,
            NationCode = "FIN",
        };

        var result = await sut.ExecuteAsync(request);

        result.Succeeded.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        uow.SaveCount.Should().Be(1);

        var loaded = await repo.GetByIdAsync(series.Id);
        loaded!.Competitors.Should().ContainSingle(c =>
            c.LastName.Value == "SMITH" && c.FirstName == "John");
    }

    [Fact]
    public async Task Unknown_series_returns_failure()
    {
        var (sut, _, uow, _) = BuildSut();
        var result = await sut.ExecuteAsync(new AddCompetitorRequest
        {
            EventSeriesId = Guid.NewGuid(),
            LastName = "SMITH", FirstName = "John", YearOfBirth = 2005,
        });
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        uow.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Duplicate_competitor_returns_failure()
    {
        var (sut, _, uow, series) = BuildSut();

        var req = new AddCompetitorRequest
        {
            EventSeriesId = series.Id,
            LastName = "JONES", FirstName = "Alice", YearOfBirth = 2007,
        };

        await sut.ExecuteAsync(req);
        var second = await sut.ExecuteAsync(req);

        second.Succeeded.Should().BeFalse();
        second.ErrorMessage.Should().Contain("already exists");
    }
}

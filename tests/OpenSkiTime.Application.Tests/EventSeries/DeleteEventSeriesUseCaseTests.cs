using OpenSkiTime.Application.Series;
using OpenSkiTime.Application.Tests.Fakes;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Series;

public class DeleteEventSeriesUseCaseTests
{
    private static (DeleteEventSeriesUseCase sut, FakeEventSeriesRepository repo, FakeUnitOfWork uow, EventSeries series) BuildSut()
    {
        var repo = new FakeEventSeriesRepository();
        var uow = new FakeUnitOfWork();
        var series = EventSeries.Create(
            Guid.NewGuid(), "Levi Cup", "Levi", "Club",
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 5),
            "FIN", "2025/26",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        series.AddCompetition(Competition.Create(
            Guid.NewGuid(), series.Id, "Slalom", "3.1 SL",
            new DateOnly(2026, 4, 3), Discipline.SL, RaceType.Club, 2, 0));
        repo.AddAsync(series).GetAwaiter().GetResult();

        return (new DeleteEventSeriesUseCase(repo, uow), repo, uow, series);
    }

    [Fact]
    public async Task Existing_series_is_removed_and_persisted()
    {
        var (sut, repo, uow, series) = BuildSut();

        var result = await sut.ExecuteAsync(series.Id);

        result.Succeeded.Should().BeTrue();
        uow.SaveCount.Should().Be(1);
        (await repo.GetByIdAsync(series.Id)).Should().BeNull();
        (await repo.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_series_returns_failure_and_does_not_persist()
    {
        var (sut, _, uow, _) = BuildSut();

        var result = await sut.ExecuteAsync(Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        uow.SaveCount.Should().Be(0);
    }
}

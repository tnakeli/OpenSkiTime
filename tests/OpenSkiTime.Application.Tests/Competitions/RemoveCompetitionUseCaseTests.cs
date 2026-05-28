using OpenSkiTime.Application.Competitions;
using OpenSkiTime.Application.Tests.Fakes;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Competitions;

public class RemoveCompetitionUseCaseTests
{
    private static (RemoveCompetitionUseCase sut, FakeEventSeriesRepository repo, FakeUnitOfWork uow, EventSeries series, Competition first, Competition second) BuildSut()
    {
        var repo = new FakeEventSeriesRepository();
        var uow = new FakeUnitOfWork();
        var series = EventSeries.Create(
            Guid.NewGuid(), "Levi Cup", "Levi", "Club",
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 5),
            "FIN", "2025/26",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var c1 = Competition.Create(
            Guid.NewGuid(), series.Id, "Slalom 1", "3.1 SL",
            new DateOnly(2026, 4, 3), Discipline.SL, RaceType.Club, 2, 0);
        var c2 = Competition.Create(
            Guid.NewGuid(), series.Id, "Slalom 2", "4.1 GS",
            new DateOnly(2026, 4, 4), Discipline.GS, RaceType.Club, 2, 0);
        series.AddCompetition(c1);
        series.AddCompetition(c2);
        repo.AddAsync(series).GetAwaiter().GetResult();

        return (new RemoveCompetitionUseCase(repo, uow), repo, uow, series, c1, c2);
    }

    [Fact]
    public async Task Removes_only_target_competition_other_remains()
    {
        var (sut, repo, uow, series, c1, c2) = BuildSut();

        var result = await sut.ExecuteAsync(series.Id, c1.Id);

        result.Succeeded.Should().BeTrue();
        uow.SaveCount.Should().Be(1);

        var reloaded = await repo.GetByIdAsync(series.Id);
        reloaded!.Competitions.Should().ContainSingle(c => c.Id == c2.Id);
    }

    [Fact]
    public async Task Unknown_series_returns_failure()
    {
        var (sut, _, uow, _, c1, _) = BuildSut();

        var result = await sut.ExecuteAsync(Guid.NewGuid(), c1.Id);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Event Series");
        uow.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_competition_returns_failure()
    {
        var (sut, _, uow, series, _, _) = BuildSut();

        var result = await sut.ExecuteAsync(series.Id, Guid.NewGuid());

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Competition");
        uow.SaveCount.Should().Be(0);
    }
}

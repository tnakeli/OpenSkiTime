using OpenSkiTime.Application.Competitions;
using OpenSkiTime.Application.Tests.Fakes;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Competitions;

public class AddCompetitionUseCaseTests
{
    private static (AddCompetitionUseCase sut, FakeEventSeriesRepository repo, FakeUnitOfWork uow, EventSeries series) BuildSut()
    {
        var repo = new FakeEventSeriesRepository();
        var uow = new FakeUnitOfWork();
        var series = EventSeries.Create(
            Guid.NewGuid(), "Levi Cup", "Levi", "Club",
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 5),
            "FIN", "2025/26",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        repo.AddAsync(series).GetAwaiter().GetResult();

        return (new AddCompetitionUseCase(repo, uow), repo, uow, series);
    }

    private static AddCompetitionCommand ValidClubCmd(Guid seriesId) => new(
        EventSeriesId: seriesId,
        Name: "Slalom 1",
        ShortLabel: "3.1 SL",
        Date: new DateOnly(2026, 4, 3),
        Discipline: Discipline.SL,
        RaceType: RaceType.Club,
        NumberOfRuns: 2,
        NumberOfIntermediateTimes: 0);

    [Fact]
    public async Task Valid_command_adds_competition_and_persists()
    {
        var (sut, repo, uow, series) = BuildSut();

        var result = await sut.ExecuteAsync(ValidClubCmd(series.Id));

        result.Succeeded.Should().BeTrue();
        uow.SaveCount.Should().Be(1);

        var reloaded = await repo.GetByIdAsync(series.Id);
        reloaded!.Competitions.Should().ContainSingle(c => c.Id == result.Value);
    }

    [Fact]
    public async Task Unknown_series_returns_failure()
    {
        var (sut, _, uow, _) = BuildSut();

        var result = await sut.ExecuteAsync(ValidClubCmd(Guid.NewGuid()));

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
        uow.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task FIS_race_without_FisCode_returns_failure()
    {
        var (sut, _, uow, series) = BuildSut();

        var result = await sut.ExecuteAsync(ValidClubCmd(series.Id) with
        {
            RaceType = RaceType.FIS,
            FisCode = null,
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("FIS code");
        uow.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task FIS_race_with_FisCode_succeeds()
    {
        var (sut, _, _, series) = BuildSut();

        var result = await sut.ExecuteAsync(ValidClubCmd(series.Id) with
        {
            RaceType = RaceType.FIS,
            FisCode = "FIN-001",
            ShortLabel = "5.1 GS",
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Duplicate_short_label_returns_failure()
    {
        var (sut, _, _, series) = BuildSut();

        (await sut.ExecuteAsync(ValidClubCmd(series.Id))).Succeeded.Should().BeTrue();
        var second = await sut.ExecuteAsync(ValidClubCmd(series.Id) with { Name = "Slalom 2" });

        second.Succeeded.Should().BeFalse();
        second.ErrorMessage.Should().Contain("short label");
    }

    [Fact]
    public async Task Runs_below_one_returns_failure()
    {
        var (sut, _, _, series) = BuildSut();

        var result = await sut.ExecuteAsync(ValidClubCmd(series.Id) with { NumberOfRuns = 0 });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("NumberOfRuns");
    }
}

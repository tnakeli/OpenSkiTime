using OpenSkiTime.Application.Series;
using OpenSkiTime.Application.Tests.Fakes;

namespace OpenSkiTime.Application.Tests.Series;

public class CreateEventSeriesUseCaseTests
{
    private static (CreateEventSeriesUseCase sut, FakeEventSeriesRepository repo, FakeUnitOfWork uow) BuildSut()
    {
        var repo = new FakeEventSeriesRepository();
        var uow = new FakeUnitOfWork();
        var clock = new FixedClock(
            today: new DateOnly(2026, 1, 15),
            utcNow: new DateTime(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc));
        return (new CreateEventSeriesUseCase(repo, uow, clock), repo, uow);
    }

    private static CreateEventSeriesCommand ValidCommand() => new(
        Name: "Levi Spring Cup",
        Location: "Levi",
        Organizer: "Levi Ski Club",
        StartDate: new DateOnly(2026, 4, 3),
        EndDate: new DateOnly(2026, 4, 5),
        Nation: "FIN",
        Season: "2025/26");

    [Fact]
    public async Task Valid_command_creates_series_and_persists()
    {
        var (sut, repo, uow) = BuildSut();

        var result = await sut.ExecuteAsync(ValidCommand());

        result.Succeeded.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);
        uow.SaveCount.Should().Be(1);

        var stored = await repo.GetByIdAsync(result.Value);
        stored.Should().NotBeNull();
        stored!.Name.Should().Be("Levi Spring Cup");
        stored.Nation.Should().Be("FIN");
    }

    [Fact]
    public async Task Empty_name_returns_failure_and_does_not_persist()
    {
        var (sut, repo, uow) = BuildSut();

        var result = await sut.ExecuteAsync(ValidCommand() with { Name = "" });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        uow.SaveCount.Should().Be(0);
        (await repo.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task End_before_start_returns_failure()
    {
        var (sut, _, _) = BuildSut();

        var result = await sut.ExecuteAsync(ValidCommand() with
        {
            StartDate = new DateOnly(2026, 4, 5),
            EndDate = new DateOnly(2026, 4, 3),
        });

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("EndDate");
    }
}

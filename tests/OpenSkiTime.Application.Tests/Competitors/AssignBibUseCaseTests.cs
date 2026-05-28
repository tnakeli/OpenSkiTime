using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Application.Tests.Fakes;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Competitors;

public class AssignBibUseCaseTests
{
    private static async Task<(AssignBibUseCase sut, FakeEventSeriesRepository repo, FakeUnitOfWork uow, EventSeries series, Guid c1Id, Guid c2Id)> BuildSutAsync()
    {
        var repo = new FakeEventSeriesRepository();
        var uow = new FakeUnitOfWork();
        var series = EventSeries.Create(
            Guid.NewGuid(), "Winter Cup", "Lahti", "Club",
            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 12),
            "FIN", "2025/26", DateTime.UtcNow);
        await repo.AddAsync(series);

        var addUseCase = new AddCompetitorUseCase(repo, uow);
        var r1 = await addUseCase.ExecuteAsync(new AddCompetitorRequest
        {
            EventSeriesId = series.Id, LastName = "SMITH", FirstName = "John", YearOfBirth = 2005,
        });
        var r2 = await addUseCase.ExecuteAsync(new AddCompetitorRequest
        {
            EventSeriesId = series.Id, LastName = "JONES", FirstName = "Alice", YearOfBirth = 2007,
        });
        uow.Reset();

        return (new AssignBibUseCase(repo, uow), repo, uow, series, r1.Value, r2.Value);
    }

    [Fact]
    public async Task Assigns_bib_and_saves()
    {
        var (sut, repo, uow, series, c1Id, _) = await BuildSutAsync();

        var result = await sut.ExecuteAsync(series.Id, c1Id, 7);

        result.Succeeded.Should().BeTrue();
        uow.SaveCount.Should().Be(1);

        var loaded = await repo.GetByIdAsync(series.Id);
        loaded!.Competitors.First(c => c.Id == c1Id).BibNumber.Should().Be(7);
    }

    [Fact]
    public async Task Clears_bib_when_null()
    {
        var (sut, repo, uow, series, c1Id, _) = await BuildSutAsync();
        await sut.ExecuteAsync(series.Id, c1Id, 7);
        uow.Reset();

        var result = await sut.ExecuteAsync(series.Id, c1Id, null);

        result.Succeeded.Should().BeTrue();
        var loaded = await repo.GetByIdAsync(series.Id);
        loaded!.Competitors.First(c => c.Id == c1Id).BibNumber.Should().BeNull();
    }

    [Fact]
    public async Task Returns_failure_on_bib_conflict()
    {
        var (sut, _, uow, series, c1Id, c2Id) = await BuildSutAsync();
        await sut.ExecuteAsync(series.Id, c1Id, 5);
        uow.Reset();

        var result = await sut.ExecuteAsync(series.Id, c2Id, 5);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already assigned");
        uow.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task Returns_failure_when_series_not_found()
    {
        var (sut, _, uow, _, c1Id, _) = await BuildSutAsync();
        var result = await sut.ExecuteAsync(Guid.NewGuid(), c1Id, 3);
        result.Succeeded.Should().BeFalse();
        uow.SaveCount.Should().Be(0);
    }
}

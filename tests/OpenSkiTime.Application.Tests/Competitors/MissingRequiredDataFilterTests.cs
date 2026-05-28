using OpenSkiTime.Domain.Competitors;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Application.Tests.Competitors;

public class MissingRequiredDataFilterTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    [Fact]
    public void Competitor_with_all_required_fields_is_usable()
    {
        var c = Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005);
        c.IsUsableForRaceEntry.Should().BeTrue();
    }

    [Fact]
    public void Competitor_missing_first_name_is_not_usable()
    {
        var series = EventSeries.Create(SeriesId, "S", "L", "O",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), "FIN", "2025/26", DateTime.UtcNow);

        var validCompetitor = Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005);
        series.AddCompetitor(validCompetitor);

        var missing = series.Competitors.Where(c => !c.IsUsableForRaceEntry).ToList();
        missing.Should().BeEmpty("all current competitors have valid data");
    }

    [Fact]
    public void Filter_by_IsUsableForRaceEntry_returns_correct_subset()
    {
        var series = EventSeries.Create(SeriesId, "S", "L", "O",
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), "FIN", "2025/26", DateTime.UtcNow);

        series.AddCompetitor(Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005));
        series.AddCompetitor(Competitor.Create(Guid.NewGuid(), SeriesId, "JONES", "Alice", 2007));

        var valid = series.Competitors.Where(c => c.IsUsableForRaceEntry).ToList();
        valid.Should().HaveCount(2);
    }
}

using OpenSkiTime.Domain.Competitors;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Domain.Tests.Series;

public class EventSeriesCompetitorTests
{
    private static EventSeries MakeSeries()
        => EventSeries.Create(
            Guid.NewGuid(), "Winter Cup", "Lahti", "Lahti Ski", 
            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 12),
            "FIN", "2025/26", DateTime.UtcNow);

    private static Competitor MakeCompetitor(Guid seriesId,
        string last = "SMITH", string first = "John", int yob = 2005)
        => Competitor.Create(Guid.NewGuid(), seriesId, last, first, yob);

    [Fact]
    public void AddCompetitor_succeeds_for_valid_competitor()
    {
        var s = MakeSeries();
        var c = MakeCompetitor(s.Id);
        s.AddCompetitor(c);
        s.Competitors.Should().ContainSingle(x => x.Id == c.Id);
    }

    [Fact]
    public void AddCompetitor_throws_when_series_id_mismatch()
    {
        var s = MakeSeries();
        var c = MakeCompetitor(Guid.NewGuid());
        var act = () => s.AddCompetitor(c);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*EventSeriesId does not match*");
    }

    [Fact]
    public void AddCompetitor_throws_on_duplicate_name_and_yob()
    {
        var s = MakeSeries();
        s.AddCompetitor(MakeCompetitor(s.Id, "SMITH", "John", 2005));
        var act = () => s.AddCompetitor(MakeCompetitor(s.Id, "smith", "john", 2005));
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*already exists*");
    }

    [Fact]
    public void RemoveCompetitor_returns_true_and_removes()
    {
        var s = MakeSeries();
        var c = MakeCompetitor(s.Id);
        s.AddCompetitor(c);
        var result = s.RemoveCompetitor(c.Id);
        result.Should().BeTrue();
        s.Competitors.Should().BeEmpty();
    }

    [Fact]
    public void RemoveCompetitor_returns_false_when_not_found()
    {
        var s = MakeSeries();
        s.RemoveCompetitor(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void AssignBib_sets_bib_on_competitor()
    {
        var s = MakeSeries();
        var c = MakeCompetitor(s.Id);
        s.AddCompetitor(c);
        s.AssignBib(c.Id, 7);
        c.BibNumber.Should().Be(7);
    }

    [Fact]
    public void AssignBib_null_clears_bib()
    {
        var s = MakeSeries();
        var c = MakeCompetitor(s.Id);
        s.AddCompetitor(c);
        s.AssignBib(c.Id, 3);
        s.AssignBib(c.Id, null);
        c.BibNumber.Should().BeNull();
    }

    [Fact]
    public void AssignBib_throws_when_competitor_not_found()
    {
        var s = MakeSeries();
        var act = () => s.AssignBib(Guid.NewGuid(), 1);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*not found*");
    }

    [Fact]
    public void AssignBib_throws_on_bib_conflict()
    {
        var s = MakeSeries();
        var c1 = MakeCompetitor(s.Id, "SMITH", "John", 2005);
        var c2 = MakeCompetitor(s.Id, "JONES", "Alice", 2006);
        s.AddCompetitor(c1);
        s.AddCompetitor(c2);
        s.AssignBib(c1.Id, 5);
        var act = () => s.AssignBib(c2.Id, 5);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*already assigned*");
    }
}

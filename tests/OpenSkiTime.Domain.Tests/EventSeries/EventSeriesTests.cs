using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;

namespace OpenSkiTime.Domain.Tests.Series;

public class EventSeriesTests
{
    private static EventSeries NewSeries() =>
        EventSeries.Create(
            id: Guid.NewGuid(),
            name: "Levi Spring Cup",
            location: "Levi",
            organizer: "Levi Ski Club",
            startDate: new DateOnly(2026, 4, 3),
            endDate: new DateOnly(2026, 4, 5),
            nation: "FIN",
            season: "2025/26",
            createdAtUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData("", "Levi", "Club", "FIN", "2025/26")]
    [InlineData("   ", "Levi", "Club", "FIN", "2025/26")]
    [InlineData("Cup", "", "Club", "FIN", "2025/26")]
    [InlineData("Cup", "Levi", "", "FIN", "2025/26")]
    [InlineData("Cup", "Levi", "Club", "F", "2025/26")]   // nation not 3 chars
    [InlineData("Cup", "Levi", "Club", "FIN", "")]
    public void Create_rejects_missing_required_fields(
        string name, string location, string organizer, string nation, string season)
    {
        var act = () => EventSeries.Create(
            Guid.NewGuid(), name, location, organizer,
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 5),
            nation, season, DateTime.UtcNow);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_rejects_end_before_start()
    {
        var act = () => EventSeries.Create(
            Guid.NewGuid(), "Cup", "Levi", "Club",
            new DateOnly(2026, 4, 5), new DateOnly(2026, 4, 3),
            "FIN", "2025/26", DateTime.UtcNow);
        act.Should().Throw<ArgumentException>().WithMessage("*EndDate*");
    }

    [Fact]
    public void Create_uppercases_nation_and_trims()
    {
        var s = EventSeries.Create(
            Guid.NewGuid(), "  Cup  ", "  Levi ", " Club ",
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 5),
            "fin", " 2025/26 ", DateTime.UtcNow);

        s.Name.Should().Be("Cup");
        s.Location.Should().Be("Levi");
        s.Organizer.Should().Be("Club");
        s.Nation.Should().Be("FIN");
        s.Season.Should().Be("2025/26");
        s.RowVersion.Should().Be(1);
    }

    [Fact]
    public void AddCompetition_with_mismatched_series_id_throws()
    {
        var s = NewSeries();
        var c = Competition.Create(
            Guid.NewGuid(), Guid.NewGuid() /* different series */,
            "Slalom", "3.1 SL", new DateOnly(2026, 4, 3),
            Discipline.SL, RaceType.Club, 2, 0);

        var act = () => s.AddCompetition(c);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddCompetition_with_duplicate_short_label_throws()
    {
        var s = NewSeries();
        var c1 = Competition.Create(
            Guid.NewGuid(), s.Id, "Slalom", "3.1 SL",
            new DateOnly(2026, 4, 3), Discipline.SL, RaceType.Club, 2, 0);
        var c2 = Competition.Create(
            Guid.NewGuid(), s.Id, "Slalom 2", "3.1 sl" /* same label, different case */,
            new DateOnly(2026, 4, 4), Discipline.SL, RaceType.Club, 2, 0);

        s.AddCompetition(c1);
        var act = () => s.AddCompetition(c2);
        act.Should().Throw<InvalidOperationException>().WithMessage("*short label*");
    }

    [Fact]
    public void AddCompetition_appends_to_collection()
    {
        var s = NewSeries();
        var c = Competition.Create(
            Guid.NewGuid(), s.Id, "Slalom", "3.1 SL",
            new DateOnly(2026, 4, 3), Discipline.SL, RaceType.Club, 2, 0);

        s.AddCompetition(c);
        s.Competitions.Should().ContainSingle(x => x.Id == c.Id);
    }

    [Fact]
    public void RemoveCompetition_returns_true_when_present_false_otherwise()
    {
        var s = NewSeries();
        var c = Competition.Create(
            Guid.NewGuid(), s.Id, "Slalom", "3.1 SL",
            new DateOnly(2026, 4, 3), Discipline.SL, RaceType.Club, 2, 0);
        s.AddCompetition(c);

        s.RemoveCompetition(c.Id).Should().BeTrue();
        s.Competitions.Should().BeEmpty();
        s.RemoveCompetition(c.Id).Should().BeFalse();
    }

    [Fact]
    public void UpdateBasicData_revalidates()
    {
        var s = NewSeries();
        var act = () => s.UpdateBasicData(
            "", "Levi", "Club",
            new DateOnly(2026, 4, 3), new DateOnly(2026, 4, 5),
            "FIN", "2025/26");
        act.Should().Throw<ArgumentException>();
    }
}

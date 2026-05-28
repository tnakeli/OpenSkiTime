using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitors;

namespace OpenSkiTime.Domain.Tests.Competitors;

public class CompetitorTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    private static Competitor MakeCompetitor(
        string lastName = "SMITH",
        string firstName = "John",
        int yob = 2005,
        int? bib = null)
        => Competitor.Create(Guid.NewGuid(), SeriesId, lastName, firstName, yob, bibNumber: bib);

    [Fact]
    public void Create_stores_uppercased_last_name()
    {
        var c = MakeCompetitor(lastName: "smith");
        c.LastName.Value.Should().Be("SMITH");
    }

    [Fact]
    public void Create_trims_first_name()
    {
        var c = MakeCompetitor(firstName: "  Jane  ");
        c.FirstName.Should().Be("Jane");
    }

    [Fact]
    public void Create_with_empty_last_name_throws()
    {
        var act = () => MakeCompetitor(lastName: "   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_empty_first_name_throws()
    {
        var act = () => MakeCompetitor(firstName: "");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(1899)]
    [InlineData(3000)]
    public void Create_with_invalid_year_of_birth_throws(int yob)
    {
        var act = () => MakeCompetitor(yob: yob);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_normalizes_fis_code_to_uppercase()
    {
        var c = Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005,
            fisCode: "abc1234");
        c.FisCode.Should().Be("ABC1234");
    }

    [Fact]
    public void Create_normalizes_nation_code_to_uppercase_3_chars()
    {
        var c = Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005,
            nationCode: "fin");
        c.NationCode.Should().Be("FIN");
    }

    [Fact]
    public void Create_with_invalid_nation_code_throws()
    {
        var act = () => Competitor.Create(Guid.NewGuid(), SeriesId, "SMITH", "John", 2005,
            nationCode: "XX");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_negative_bib_throws()
    {
        var act = () => MakeCompetitor(bib: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void UpdatePersonalData_changes_name_and_normalizes()
    {
        var c = MakeCompetitor();
        c.UpdatePersonalData("jones", "Alice", 2006, null, "NOR", null, Gender.Female);
        c.LastName.Value.Should().Be("JONES");
        c.FirstName.Should().Be("Alice");
        c.NationCode.Should().Be("NOR");
        c.Gender.Should().Be(Gender.Female);
    }
}

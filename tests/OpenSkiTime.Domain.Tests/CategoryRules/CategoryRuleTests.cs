using OpenSkiTime.Domain.CategoryRules;
using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Domain.Tests.CategoryRules;

public class CategoryRuleTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    private static CategoryRule Make(
        string label = "U12 Boys",
        int min = 2012,
        int max = 2015,
        Gender? gender = null)
        => CategoryRule.Create(Guid.NewGuid(), SeriesId, label, min, max, gender);

    [Fact]
    public void Create_sets_properties_correctly()
    {
        var r = Make("U12 Boys", 2012, 2015, Gender.Male);
        r.Label.Should().Be("U12 Boys");
        r.BirthYearMin.Should().Be(2012);
        r.BirthYearMax.Should().Be(2015);
        r.Gender.Should().Be(Gender.Male);
    }

    [Fact]
    public void Create_with_empty_label_throws()
    {
        var act = () => Make(label: "  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_with_max_less_than_min_throws()
    {
        var act = () => Make(min: 2015, max: 2010);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(2012, Gender.Male, true)]
    [InlineData(2015, Gender.Male, true)]
    [InlineData(2016, Gender.Male, false)]
    [InlineData(2011, Gender.Male, false)]
    [InlineData(2013, Gender.Female, false)]
    public void Matches_respects_year_and_gender(int yob, Gender gender, bool expected)
    {
        var rule = Make("U12 Boys", 2012, 2015, Gender.Male);
        rule.Matches(yob, gender).Should().Be(expected);
    }

    [Fact]
    public void Matches_null_gender_rule_matches_any_gender()
    {
        var rule = Make("Youth", 2010, 2015, gender: null);
        rule.Matches(2012, Gender.Female).Should().BeTrue();
        rule.Matches(2012, Gender.Male).Should().BeTrue();
        rule.Matches(2012, null).Should().BeTrue();
    }

    [Fact]
    public void Update_changes_properties()
    {
        var r = Make();
        r.Update("U10 Girls", 2014, 2017, Gender.Female, 2);
        r.Label.Should().Be("U10 Girls");
        r.BirthYearMax.Should().Be(2017);
        r.Gender.Should().Be(Gender.Female);
        r.DisplayOrder.Should().Be(2);
    }
}

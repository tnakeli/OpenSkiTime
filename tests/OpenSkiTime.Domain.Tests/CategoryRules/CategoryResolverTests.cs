using OpenSkiTime.Domain.CategoryRules;
using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Domain.Tests.CategoryRules;

public class CategoryResolverTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();

    private static CategoryRule Rule(string label, int min, int max, Gender? gender = null, int order = 0)
        => CategoryRule.Create(Guid.NewGuid(), SeriesId, label, min, max, gender, order);

    [Fact]
    public void Matches_when_year_and_gender_fit()
    {
        var rule = Rule("U16 Boys", 2010, 2013, Gender.Male);
        rule.Matches(2011, Gender.Male).Should().BeTrue();
    }

    [Fact]
    public void No_match_when_year_below_min()
    {
        var rule = Rule("U16 Boys", 2010, 2013, Gender.Male);
        rule.Matches(2009, Gender.Male).Should().BeFalse();
    }

    [Fact]
    public void No_match_when_year_above_max()
    {
        var rule = Rule("U16 Boys", 2010, 2013, Gender.Male);
        rule.Matches(2014, Gender.Male).Should().BeFalse();
    }

    [Fact]
    public void No_match_when_gender_differs()
    {
        var rule = Rule("U16 Boys", 2010, 2013, Gender.Male);
        rule.Matches(2011, Gender.Female).Should().BeFalse();
    }

    [Fact]
    public void Null_gender_on_rule_matches_any_gender()
    {
        var rule = Rule("Open U16", 2010, 2013);
        rule.Matches(2012, Gender.Male).Should().BeTrue();
        rule.Matches(2012, Gender.Female).Should().BeTrue();
        rule.Matches(2012, null).Should().BeTrue();
    }

    [Fact]
    public void Boundary_years_inclusive()
    {
        var rule = Rule("U16", 2010, 2013);
        rule.Matches(2010, null).Should().BeTrue();
        rule.Matches(2013, null).Should().BeTrue();
    }

    [Fact]
    public void First_match_wins_in_ordered_set()
    {
        var rules = new[]
        {
            Rule("U14 Boys", 2012, 2015, Gender.Male, order: 1),
            Rule("U16 Boys", 2010, 2013, Gender.Male, order: 2),
            Rule("Open",     1900, 2099, null,         order: 3),
        }.OrderBy(r => r.DisplayOrder).ToList();

        var matched = rules.FirstOrDefault(r => r.Matches(2013, Gender.Male));
        matched!.Label.Should().Be("U14 Boys");
    }

    [Fact]
    public void Falls_through_to_open_category_when_no_specific_rule_matches()
    {
        var rules = new[]
        {
            Rule("U14 Boys", 2012, 2015, Gender.Male, order: 1),
            Rule("Open",     1900, 2099, null,         order: 2),
        }.OrderBy(r => r.DisplayOrder).ToList();

        var matched = rules.FirstOrDefault(r => r.Matches(2000, Gender.Male));
        matched!.Label.Should().Be("Open");
    }

    [Fact]
    public void Returns_null_when_no_rule_matches()
    {
        var rules = new[]
        {
            Rule("U14 Boys", 2012, 2015, Gender.Male, order: 1),
        };

        var matched = rules.FirstOrDefault(r => r.Matches(2000, Gender.Female));
        matched.Should().BeNull();
    }
}

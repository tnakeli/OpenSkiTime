namespace OpenSkiTime.Import.Tests;

public class CompetitorMatcherTests
{
    private static CompetitorRef Ref(string last, string first, int yob, string code = "")
        => new(Guid.NewGuid(), code, last, first, yob);

    [Fact]
    public void Matches_by_fis_code_takes_priority()
    {
        var existing = new List<CompetitorRef>
        {
            Ref("DIFFERENT", "Person", 2000, "9876543"),
            Ref("SMITH", "John", 2005, "1234567"),
        };
        var matcher = new CompetitorMatcher(existing);
        var result = matcher.Match("JONES", "Alice", 2007, "1234567");
        result.Should().NotBeNull();
        result!.LastNameUpper.Should().Be("SMITH");
    }

    [Fact]
    public void Falls_back_to_name_and_yob_when_no_fis_code()
    {
        var existing = new List<CompetitorRef>
        {
            Ref("SMITH", "John", 2005),
        };
        var matcher = new CompetitorMatcher(existing);
        var result = matcher.Match("smith", "john", 2005, null);
        result.Should().NotBeNull();
    }

    [Fact]
    public void Returns_null_when_no_match()
    {
        var existing = new List<CompetitorRef>
        {
            Ref("SMITH", "John", 2005),
        };
        var matcher = new CompetitorMatcher(existing);
        var result = matcher.Match("JONES", "Alice", 2007, null);
        result.Should().BeNull();
    }

    [Fact]
    public void Name_match_is_case_insensitive()
    {
        var existing = new List<CompetitorRef> { Ref("SMITH", "John", 2005) };
        var matcher = new CompetitorMatcher(existing);
        matcher.Match("smith", "JOHN", 2005, null).Should().NotBeNull();
    }

    [Fact]
    public void Yob_must_match_exactly()
    {
        var existing = new List<CompetitorRef> { Ref("SMITH", "John", 2005) };
        var matcher = new CompetitorMatcher(existing);
        matcher.Match("SMITH", "John", 2006, null).Should().BeNull();
    }
}

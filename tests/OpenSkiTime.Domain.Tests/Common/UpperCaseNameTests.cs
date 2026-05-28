using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Domain.Tests.Common;

public class UpperCaseNameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void Constructor_rejects_null_or_whitespace(string? raw)
    {
        var act = () => new UpperCaseName(raw!);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("hirscher", "HIRSCHER")]
    [InlineData("Hirscher", "HIRSCHER")]
    [InlineData("HIRSCHER", "HIRSCHER")]
    [InlineData("  hirscher  ", "HIRSCHER")]
    [InlineData("van der poel", "VAN DER POEL")]
    public void Constructor_uppercases_and_trims(string raw, string expected)
    {
        var sut = new UpperCaseName(raw);
        sut.Value.Should().Be(expected);
        sut.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("ärm", "ÄRM")]
    [InlineData("özsoy", "ÖZSOY")]
    [InlineData("müller", "MÜLLER")]
    [InlineData("kyllä", "KYLLÄ")]
    public void Constructor_preserves_diacritics_when_uppercasing(string raw, string expected)
    {
        new UpperCaseName(raw).Value.Should().Be(expected);
    }

    [Fact]
    public void Equality_is_case_insensitive_via_uppercase_normalization()
    {
        var a = new UpperCaseName("Hirscher");
        var b = new UpperCaseName("HIRSCHER");
        var c = new UpperCaseName("hirscher");

        (a == b).Should().BeTrue();
        (b == c).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Implicitly_converts_to_string()
    {
        UpperCaseName name = new UpperCaseName("hirscher");
        string s = name;
        s.Should().Be("HIRSCHER");
    }

    [Fact]
    public void CompareTo_orders_alphabetically_on_uppercase_value()
    {
        var a = new UpperCaseName("Aamu");
        var b = new UpperCaseName("zebra");

        a.CompareTo(b).Should().BeLessThan(0);
        b.CompareTo(a).Should().BeGreaterThan(0);
        a.CompareTo(a).Should().Be(0);
    }
}

using OpenSkiTime.Domain.Competitors;

namespace OpenSkiTime.Domain.Tests.Competitors;

public class BibNumberTests
{
    [Fact]
    public void Constructor_accepts_positive_value()
    {
        var b = new BibNumber(1);
        b.Value.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_rejects_non_positive_value(int value)
    {
        var act = () => new BibNumber(value);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Comparison_operators_work()
    {
        var a = new BibNumber(1);
        var b = new BibNumber(2);
        (a < b).Should().BeTrue();
        (b > a).Should().BeTrue();
        (a <= b).Should().BeTrue();
        (b >= a).Should().BeTrue();
    }
}

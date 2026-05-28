namespace OpenSkiTime.Import.Tests;

public class ParticipationValueMatcherTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("YES")]
    [InlineData("Yes")]
    [InlineData("kyllä")]
    [InlineData("KYLLÄ")]
    [InlineData("Kyllä")]
    [InlineData("x")]
    [InlineData("X")]
    public void Truthy_values_return_true(string value)
        => ParticipationValueMatcher.IsParticipating(value).Should().BeTrue();

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("kyllä!")]
    [InlineData("no")]
    [InlineData("ei")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Non_truthy_values_return_false(string? value)
        => ParticipationValueMatcher.IsParticipating(value).Should().BeFalse();

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        ParticipationValueMatcher.IsParticipating("  x  ").Should().BeTrue();
        ParticipationValueMatcher.IsParticipating("  yes  ").Should().BeTrue();
    }
}

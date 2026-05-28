namespace OpenSkiTime.Import.Tests;

public class CombinedNameParserTests
{
    private static (string? last, string? first) Split(string fullName)
    {
        var row = new RawImportRow(
            new Dictionary<ImportField, string>
            {
                [ImportField.FullName] = fullName,
            },
            new Dictionary<string, string>());
        return NameProjector.Project(row);
    }

    [Theory]
    [InlineData("SMITH, John",    "SMITH",        "John")]
    [InlineData("SMITH,John",     "SMITH",        "John")]
    [InlineData("SMITH,  John",   "SMITH",        "John")]
    public void Comma_separated_last_first(string input, string expectedLast, string expectedFirst)
    {
        var (last, first) = Split(input);
        last.Should().Be(expectedLast);
        first.Should().Be(expectedFirst);
    }

    [Theory]
    [InlineData("John SMITH",  "SMITH", "John")]
    [InlineData("Alice JONES", "JONES", "Alice")]
    public void Last_token_uppercase_becomes_last_name(string input, string expectedLast, string expectedFirst)
    {
        var (last, first) = Split(input);
        last.Should().Be(expectedLast);
        first.Should().Be(expectedFirst);
    }

    [Fact]
    public void Multi_word_last_name_requires_comma_format()
    {
        var (last, first) = Split("VAN DER POEL, Jeroen");
        last.Should().Be("VAN DER POEL");
        first.Should().Be("Jeroen");
    }

    [Fact]
    public void First_token_uppercase_becomes_last_name()
    {
        var (last, first) = Split("MÜLLER Hannes");
        last.Should().Be("MÜLLER");
        first.Should().Be("Hannes");
    }

    [Fact]
    public void Single_token_returns_it_as_last_name()
    {
        var (last, first) = Split("SMITH");
        last.Should().Be("SMITH");
        first.Should().BeNull();
    }

    [Fact]
    public void No_casing_hint_uses_last_token_as_last_name()
    {
        var (last, first) = Split("John Smith");
        last.Should().Be("Smith");
        first.Should().Be("John");
    }

    [Fact]
    public void Full_name_column_flagged_as_uncertain_not_used_when_separate_columns_present()
    {
        var row = new RawImportRow(
            new Dictionary<ImportField, string>
            {
                [ImportField.LastName] = "JONES",
                [ImportField.FirstName] = "Alice",
                [ImportField.FullName] = "SHOULD BE IGNORED",
            },
            new Dictionary<string, string>());

        var (last, _) = NameProjector.Project(row);
        last.Should().Be("JONES");
    }
}

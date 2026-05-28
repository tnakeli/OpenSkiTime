namespace OpenSkiTime.Import.Tests;

public class NameProjectorTests
{
    private static RawImportRow Row(Dictionary<ImportField, string> fields)
        => new(fields, new Dictionary<string, string>());

    [Fact]
    public void Two_column_preferred_returns_separate_fields()
    {
        var row = Row(new()
        {
            [ImportField.LastName] = "SMITH",
            [ImportField.FirstName] = "John",
        });
        var (last, first) = NameProjector.Project(row);
        last.Should().Be("SMITH");
        first.Should().Be("John");
    }

    [Fact]
    public void Last_name_only_when_no_first_name_column()
    {
        var row = Row(new() { [ImportField.LastName] = "SMITH" });
        var (last, first) = NameProjector.Project(row);
        last.Should().Be("SMITH");
        first.Should().BeNull();
    }

    [Fact]
    public void Full_name_comma_separated_splits_correctly()
    {
        var (last, first) = NameProjector.SplitFullName("SMITH, John");
        last.Should().Be("SMITH");
        first.Should().Be("John");
    }

    [Fact]
    public void Full_name_comma_no_space_splits_correctly()
    {
        var (last, first) = NameProjector.SplitFullName("SMITH,John");
        last.Should().Be("SMITH");
        first.Should().Be("John");
    }

    [Fact]
    public void Full_name_last_token_uppercase_is_last_name()
    {
        var (last, first) = NameProjector.SplitFullName("John SMITH");
        last.Should().Be("SMITH");
        first.Should().Be("John");
    }

    [Fact]
    public void Full_name_first_token_uppercase_is_last_name()
    {
        var (last, first) = NameProjector.SplitFullName("SMITH John");
        last.Should().Be("SMITH");
        first.Should().Be("John");
    }

    [Fact]
    public void Full_name_single_token_returns_last_name_only()
    {
        var (last, first) = NameProjector.SplitFullName("SMITH");
        last.Should().Be("SMITH");
        first.Should().BeNull();
    }

    [Fact]
    public void Full_name_diacritics_uppercase_detected_correctly()
    {
        var (last, first) = NameProjector.SplitFullName("Hannes MÜLLER");
        last.Should().Be("MÜLLER");
        first.Should().Be("Hannes");
    }

    [Fact]
    public void No_name_column_returns_null()
    {
        var row = Row(new() { [ImportField.YearOfBirth] = "2005" });
        var (last, first) = NameProjector.Project(row);
        last.Should().BeNull();
        first.Should().BeNull();
    }
}

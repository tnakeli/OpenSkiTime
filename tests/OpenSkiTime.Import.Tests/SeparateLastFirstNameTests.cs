namespace OpenSkiTime.Import.Tests;

public class SeparateLastFirstNameTests
{
    private static RawImportRow Row(string last, string first)
        => new(new Dictionary<ImportField, string>
        {
            [ImportField.LastName] = last,
            [ImportField.FirstName] = first,
        }, new Dictionary<string, string>());

    [Fact]
    public void Last_name_is_returned_as_given_by_projector()
    {
        var (last, first) = NameProjector.Project(Row("smith", "John"));
        last.Should().Be("smith");
        first.Should().Be("John");
    }

    [Fact]
    public void First_name_is_preserved_as_given()
    {
        var (_, first) = NameProjector.Project(Row("JONES", "alice"));
        first.Should().Be("alice");
    }

    [Fact]
    public void Whitespace_is_trimmed()
    {
        var (last, first) = NameProjector.Project(Row("  müller  ", "  Hans  "));
        last.Should().Be("müller");
        first.Should().Be("Hans");
    }

    [Fact]
    public void Two_column_layout_preferred_over_fullname()
    {
        var row = new RawImportRow(
            new Dictionary<ImportField, string>
            {
                [ImportField.LastName] = "SMITH",
                [ImportField.FirstName] = "John",
                [ImportField.FullName] = "Should be ignored",
            },
            new Dictionary<string, string>());

        var (last, _) = NameProjector.Project(row);
        last.Should().Be("SMITH");
    }
}

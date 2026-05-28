namespace OpenSkiTime.Import.Tests;

public class TsvTokenizerTests
{
    [Fact]
    public void Empty_string_returns_empty()
    {
        TsvTokenizer.Tokenize("").Should().BeEmpty();
        TsvTokenizer.Tokenize("   ").Should().BeEmpty();
    }

    [Fact]
    public void Single_header_row_is_returned()
    {
        var rows = TsvTokenizer.Tokenize("LastName\tFirstName\tYOB");
        rows.Should().ContainSingle();
        rows[0].Should().Equal("LastName", "FirstName", "YOB");
    }

    [Fact]
    public void Data_rows_are_split_correctly()
    {
        var tsv = "LastName\tFirstName\tYOB\r\nSMITH\tJohn\t2005\nJONES\tAlice\t2007";
        var rows = TsvTokenizer.Tokenize(tsv);
        rows.Should().HaveCount(3);
        rows[1].Should().Equal("SMITH", "John", "2005");
        rows[2].Should().Equal("JONES", "Alice", "2007");
    }

    [Fact]
    public void Blank_lines_are_skipped()
    {
        var tsv = "A\tB\n\n1\t2\n\n3\t4";
        var rows = TsvTokenizer.Tokenize(tsv);
        rows.Should().HaveCount(3);
    }

    [Fact]
    public void Quoted_fields_with_tabs_are_handled()
    {
        var tsv = "Name\tNote\n\"SMITH, John\"\t\"tab\there\"";
        var rows = TsvTokenizer.Tokenize(tsv);
        rows[1][0].Should().Be("SMITH, John");
        rows[1][1].Should().Be("tab\there");
    }

    [Fact]
    public void Short_rows_are_padded_to_header_width()
    {
        var tsv = "A\tB\tC\n1\t2";
        var rows = TsvTokenizer.Tokenize(tsv);
        rows[1].Should().HaveCount(3);
        rows[1][2].Should().BeEmpty();
    }

    [Fact]
    public void Escaped_quotes_in_quoted_field_are_unescaped()
    {
        var tsv = "Name\n\"He said \"\"hello\"\"\"";
        var rows = TsvTokenizer.Tokenize(tsv);
        rows[1][0].Should().Be("He said \"hello\"");
    }
}

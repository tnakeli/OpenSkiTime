namespace OpenSkiTime.Import.Tests;

public class HeaderMapperTests
{
    private static HeaderMapper Make(params string[] labels) => new(labels);

    [Theory]
    [InlineData("LastName", ImportField.LastName)]
    [InlineData("last name", ImportField.LastName)]
    [InlineData("Surname", ImportField.LastName)]
    [InlineData("FirstName", ImportField.FirstName)]
    [InlineData("first", ImportField.FirstName)]
    [InlineData("Name", ImportField.FullName)]
    [InlineData("Athlete", ImportField.FullName)]
    [InlineData("YOB", ImportField.YearOfBirth)]
    [InlineData("Year of Birth", ImportField.YearOfBirth)]
    [InlineData("FIS Code", ImportField.FisCode)]
    [InlineData("fis", ImportField.FisCode)]
    [InlineData("Nat", ImportField.NationCode)]
    [InlineData("Country", ImportField.NationCode)]
    [InlineData("Club", ImportField.ClubName)]
    [InlineData("Team", ImportField.ClubName)]
    [InlineData("Bib", ImportField.BibNumber)]
    [InlineData("Nr", ImportField.BibNumber)]
    public void Known_headers_map_correctly(string header, ImportField expected)
    {
        Make().Map(header).Should().Be(expected);
    }

    [Fact]
    public void Unknown_header_returns_unknown()
    {
        Make().Map("Something random").Should().Be(ImportField.Unknown);
    }

    [Fact]
    public void Competition_short_label_maps_to_participation_flag()
    {
        var mapper = Make("3.1 SL", "4.1 GS");
        mapper.Map("3.1 SL").Should().Be(ImportField.ParticipationFlag);
        mapper.Map("4.1 gs").Should().Be(ImportField.ParticipationFlag);
    }

    [Fact]
    public void MapHeaders_returns_correct_indices()
    {
        var mapper = Make("3.1 SL");
        var headers = new[] { "LastName", "FirstName", "YOB", "3.1 SL" };
        var mappings = mapper.MapHeaders(headers);

        mappings.Should().HaveCount(4);
        mappings[0].Field.Should().Be(ImportField.LastName);
        mappings[3].Field.Should().Be(ImportField.ParticipationFlag);
        mappings[3].RawHeader.Should().Be("3.1 SL");
    }
}

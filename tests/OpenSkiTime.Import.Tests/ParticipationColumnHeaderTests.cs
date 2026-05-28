namespace OpenSkiTime.Import.Tests;

public class ParticipationColumnHeaderTests
{
    [Fact]
    public void Competition_short_label_maps_to_participation_flag()
    {
        var mapper = new HeaderMapper(["3.1 SL", "4.1 GS"]);
        var mappings = mapper.MapHeaders(["LastName", "FirstName", "YOB", "3.1 SL", "4.1 GS"]);

        mappings[3].Field.Should().Be(ImportField.ParticipationFlag);
        mappings[3].RawHeader.Should().Be("3.1 SL");
        mappings[4].Field.Should().Be(ImportField.ParticipationFlag);
        mappings[4].RawHeader.Should().Be("4.1 GS");
    }

    [Fact]
    public void Unknown_column_maps_to_ImportField_Unknown()
    {
        var mapper = new HeaderMapper(["3.1 SL"]);
        var mappings = mapper.MapHeaders(["LastName", "SomeUnknownColumn"]);

        mappings[1].Field.Should().Be(ImportField.Unknown);
    }

    [Fact]
    public void Header_matching_is_case_insensitive_for_known_fields()
    {
        var mapper = new HeaderMapper([]);
        var mappings = mapper.MapHeaders(["lastname", "FIRSTNAME", "yob"]);

        mappings[0].Field.Should().Be(ImportField.LastName);
        mappings[1].Field.Should().Be(ImportField.FirstName);
        mappings[2].Field.Should().Be(ImportField.YearOfBirth);
    }

    [Fact]
    public void Short_label_match_is_case_insensitive_via_constructor_comparer()
    {
        var mapper = new HeaderMapper(["3.1 SL"]);
        var mappings = mapper.MapHeaders(["3.1 sl"]);

        mappings[0].Field.Should().Be(ImportField.ParticipationFlag);
    }
}

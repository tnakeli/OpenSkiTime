namespace OpenSkiTime.Import.Tests;

public class PartialUpdateTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid CompetitorId = Guid.NewGuid();
    private static readonly Guid Competition1Id = Guid.NewGuid();
    private static readonly Guid Competition2Id = Guid.NewGuid();

    private static EventSeriesSnapshot Snapshot() =>
        new(SeriesId, 1,
            [
                new CompetitionRef(Competition1Id, "3.1 SL", new DateOnly(2026, 1, 10)),
                new CompetitionRef(Competition2Id, "4.1 GS", new DateOnly(2026, 1, 11)),
            ],
            [new CompetitorRef(CompetitorId, "1234567", "SMITH", "John", 2005, null, null, null)],
            [
                new ParticipationRef(CompetitorId, Competition1Id, true),
                new ParticipationRef(CompetitorId, Competition2Id, false),
            ]);

    [Fact]
    public void Only_columns_present_in_source_produce_diffs()
    {
        var row = new RawImportRow(
            new Dictionary<ImportField, string>
            {
                [ImportField.LastName] = "SMITH",
                [ImportField.FirstName] = "John",
                [ImportField.YearOfBirth] = "2005",
            },
            new Dictionary<string, string>
            {
                ["3.1 SL"] = "x"
            });

        var diff = DiffEngine.Compute(Snapshot(), [row]);

        diff.Participations.Should().BeEmpty(
            "3.1 SL is already true; 4.1 GS not present in source so not touched");
    }

    [Fact]
    public void Absent_column_never_produces_FieldDelta_for_existing_competitor()
    {
        var row = new RawImportRow(
            new Dictionary<ImportField, string>
            {
                [ImportField.LastName] = "SMITH",
                [ImportField.FirstName] = "John",
                [ImportField.YearOfBirth] = "2005",
            },
            new Dictionary<string, string>());

        var diff = DiffEngine.Compute(Snapshot(), [row]);

        diff.Participations.Should().BeEmpty("no participation columns present — nothing to change");
        diff.NewCompetitors.Should().BeEmpty("competitor already exists");
        diff.BibAssignments.Should().BeEmpty("no bib column present");
    }

    [Fact]
    public void Only_provided_competition_columns_are_updated()
    {
        var row = new RawImportRow(
            new Dictionary<ImportField, string>
            {
                [ImportField.LastName] = "SMITH",
                [ImportField.FirstName] = "John",
                [ImportField.YearOfBirth] = "2005",
            },
            new Dictionary<string, string>
            {
                ["4.1 GS"] = "yes"
            });

        var diff = DiffEngine.Compute(Snapshot(), [row]);

        diff.Participations.Should().HaveCount(1);
        diff.Participations[0].CompetitionId.Should().Be(Competition2Id);
        diff.Participations[0].IsParticipating.Should().BeTrue();
    }
}

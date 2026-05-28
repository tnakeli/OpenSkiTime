namespace OpenSkiTime.Import.Tests;

public class EmptyDoesNotOverwriteTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid CompetitorId = Guid.NewGuid();
    private static readonly Guid CompetitionId = Guid.NewGuid();

    private static EventSeriesSnapshot SnapshotWithCompetitor(int? bib = 5) =>
        new(SeriesId, 1,
            [new CompetitionRef(CompetitionId, "3.1 SL", new DateOnly(2026, 1, 10))],
            [new CompetitorRef(CompetitorId, "1234567", "SMITH", "John", 2005, null, null, null)],
            [new ParticipationRef(CompetitorId, CompetitionId, true)]);

    private static RawImportRow Row(string last, string first, int yob,
        Dictionary<string, string>? participations = null)
        => new(new Dictionary<ImportField, string>
        {
            [ImportField.LastName] = last,
            [ImportField.FirstName] = first,
            [ImportField.YearOfBirth] = yob.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, participations ?? new Dictionary<string, string>());

    [Fact]
    public void Empty_participation_cell_does_not_produce_diff_for_existing_true()
    {
        var snapshot = SnapshotWithCompetitor();
        var row = Row("SMITH", "John", 2005, new Dictionary<string, string>
        {
            ["3.1 SL"] = ""
        });

        var diff = DiffEngine.Compute(snapshot, [row]);

        diff.Participations.Should().BeEmpty("empty cell should not overwrite existing participation");
    }

    [Fact]
    public void Missing_participation_column_entirely_does_not_produce_diff()
    {
        var snapshot = SnapshotWithCompetitor();
        var row = Row("SMITH", "John", 2005);

        var diff = DiffEngine.Compute(snapshot, [row]);

        diff.Participations.Should().BeEmpty("absent column must not touch existing value");
    }

    [Fact]
    public void Explicit_truthy_value_produces_participation_diff_when_currently_false()
    {
        var snapshot = new EventSeriesSnapshot(SeriesId, 1,
            [new CompetitionRef(CompetitionId, "3.1 SL", new DateOnly(2026, 1, 10))],
            [new CompetitorRef(CompetitorId, "1234567", "SMITH", "John", 2005, null, null, null)],
            [new ParticipationRef(CompetitorId, CompetitionId, false)]);

        var row = Row("SMITH", "John", 2005, new Dictionary<string, string>
        {
            ["3.1 SL"] = "x"
        });

        var diff = DiffEngine.Compute(snapshot, [row]);

        diff.Participations.Should().HaveCount(1);
        diff.Participations[0].IsParticipating.Should().BeTrue();
    }

    [Fact]
    public void No_diff_when_participation_already_matches()
    {
        var snapshot = SnapshotWithCompetitor();
        var row = Row("SMITH", "John", 2005, new Dictionary<string, string>
        {
            ["3.1 SL"] = "x"
        });

        var diff = DiffEngine.Compute(snapshot, [row]);

        diff.Participations.Should().BeEmpty("no change when value already matches");
    }
}

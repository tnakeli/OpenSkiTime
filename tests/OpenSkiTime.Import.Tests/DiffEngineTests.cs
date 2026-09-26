namespace OpenSkiTime.Import.Tests;

public class DiffEngineTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid Comp1Id = Guid.NewGuid();
    private static readonly Guid Comp2Id = Guid.NewGuid();

    private static EventSeriesSnapshot EmptySnapshot() => new(
        SeriesId, 1,
        new List<CompetitionRef>
        {
            new(Comp1Id, "3.1 SL", new DateOnly(2026, 1, 10)),
            new(Comp2Id, "4.1 GS", new DateOnly(2026, 1, 11)),
        },
        new List<CompetitorRef>(),
        new List<ParticipationRef>());

    private static string BuildTsv(params string[] dataRows)
    {
        var header = "LastName\tFirstName\tYOB\tNat\tBib\t3.1 SL\t4.1 GS";
        return header + "\n" + string.Join("\n", dataRows);
    }

    private static IReadOnlyList<RawImportRow> ParseRows(
        string tsv, EventSeriesSnapshot snapshot)
    {
        var mapper = new HeaderMapper(snapshot.Competitions.Select(c => c.ShortLabel));
        var rows = TsvTokenizer.Tokenize(tsv);
        var mappings = mapper.MapHeaders(rows[0]);
        return RowParser.Parse(rows, mappings);
    }

    [Fact]
    public void New_competitor_row_appears_in_NewCompetitors()
    {
        var snapshot = EmptySnapshot();
        var tsv = BuildTsv("SMITH\tJohn\t2005\tFIN\t1\tx\t");
        var rows = ParseRows(tsv, snapshot);

        var diff = DiffEngine.Compute(snapshot, rows);

        diff.NewCompetitors.Should().ContainSingle(c =>
            c.LastName == "SMITH" && c.FirstName == "John" && c.YearOfBirth == 2005);
        diff.NewCompetitors[0].BibNumber.Should().Be(1);
        diff.NewCompetitors[0].ParticipatingIn.Should().Contain("3.1 SL");
        diff.NewCompetitors[0].ParticipatingIn.Should().NotContain("4.1 GS");
    }

    [Fact]
    public void Existing_competitor_with_bib_generates_bib_assignment()
    {
        var existingId = Guid.NewGuid();
        var snapshot = new EventSeriesSnapshot(
            SeriesId, 1,
            new List<CompetitionRef> { new(Comp1Id, "3.1 SL", new DateOnly(2026, 1, 10)) },
            new List<CompetitorRef> { new(existingId, "", "SMITH", "John", 2005, null, null, null) },
            new List<ParticipationRef>());

        var tsv = "LastName\tFirstName\tYOB\tBib\n" +
                  "SMITH\tJohn\t2005\t7";
        var rows = ParseRows(tsv, snapshot);

        var diff = DiffEngine.Compute(snapshot, rows);

        diff.NewCompetitors.Should().BeEmpty();
        diff.BibAssignments.Should().ContainSingle(b =>
            b.CompetitorId == existingId && b.NewBib == 7);
    }

    [Fact]
    public void Participation_change_for_existing_competitor_is_detected()
    {
        var existingId = Guid.NewGuid();
        var snapshot = new EventSeriesSnapshot(
            SeriesId, 1,
            new List<CompetitionRef> { new(Comp1Id, "3.1 SL", new DateOnly(2026, 1, 10)) },
            new List<CompetitorRef> { new(existingId, "", "SMITH", "John", 2005, null, null, null) },
            new List<ParticipationRef>
            {
                new(existingId, Comp1Id, false),
            });

        var tsv = "LastName\tFirstName\tYOB\t3.1 SL\nSMITH\tJohn\t2005\tx";
        var rows = ParseRows(tsv, snapshot);

        var diff = DiffEngine.Compute(snapshot, rows);

        diff.Participations.Should().ContainSingle(p =>
            p.CompetitorId == existingId &&
            p.CompetitionId == Comp1Id &&
            p.IsParticipating);
    }

    [Fact]
    public void Row_with_missing_yob_generates_warning_and_no_entry()
    {
        var snapshot = EmptySnapshot();
        var tsv = "LastName\tFirstName\nSMITH\tJohn";
        var rows = ParseRows(tsv, snapshot);

        var diff = DiffEngine.Compute(snapshot, rows);

        diff.NewCompetitors.Should().BeEmpty();
        diff.Warnings.Should().ContainSingle(w => w.Message.Contains("year of birth"));
    }

    [Fact]
    public void Row_with_no_name_column_generates_warning()
    {
        var snapshot = EmptySnapshot();
        var tsv = "YOB\tNat\n2005\tFIN";
        var rows = ParseRows(tsv, snapshot);

        var diff = DiffEngine.Compute(snapshot, rows);

        diff.NewCompetitors.Should().BeEmpty();
        diff.Warnings.Should().ContainSingle();
    }

    [Fact]
    public void No_changes_when_participation_unchanged()
    {
        var existingId = Guid.NewGuid();
        var snapshot = new EventSeriesSnapshot(
            SeriesId, 1,
            new List<CompetitionRef> { new(Comp1Id, "3.1 SL", new DateOnly(2026, 1, 10)) },
            new List<CompetitorRef> { new(existingId, "", "SMITH", "John", 2005, null, null, null) },
            new List<ParticipationRef> { new(existingId, Comp1Id, true) });

        var tsv = "LastName\tFirstName\tYOB\t3.1 SL\nSMITH\tJohn\t2005\tx";
        var rows = ParseRows(tsv, snapshot);

        var diff = DiffEngine.Compute(snapshot, rows);
        diff.HasChanges.Should().BeFalse();
    }
}

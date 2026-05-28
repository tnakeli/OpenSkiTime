namespace OpenSkiTime.Import.Tests;

public class ImportPreviewServiceTests
{
    private static readonly Guid SeriesId = Guid.NewGuid();
    private static readonly Guid Comp1Id = Guid.NewGuid();

    private static FakeRepository MakeRepo(EventSeriesSnapshot? snapshot = null)
    {
        snapshot ??= new EventSeriesSnapshot(
            SeriesId, 1,
            new List<CompetitionRef> { new(Comp1Id, "3.1 SL", new DateOnly(2026, 1, 10)) },
            new List<CompetitorRef>(),
            new List<ParticipationRef>());
        return new FakeRepository(snapshot);
    }

    [Fact]
    public async Task Returns_failure_when_tsv_is_empty()
    {
        var svc = new ImportPreviewService(MakeRepo());
        var result = await svc.PreviewAsync(SeriesId, "   ");
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Returns_failure_when_series_not_found()
    {
        var svc = new ImportPreviewService(MakeRepo());
        var result = await svc.PreviewAsync(Guid.NewGuid(),
            "LastName\tFirstName\tYOB\nSMITH\tJohn\t2005");
        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task Valid_tsv_returns_preview_with_correct_version()
    {
        var svc = new ImportPreviewService(MakeRepo());
        var tsv = "LastName\tFirstName\tYOB\t3.1 SL\nSMITH\tJohn\t2005\t1";

        var result = await svc.PreviewAsync(SeriesId, tsv);

        result.Succeeded.Should().BeTrue();
        result.Value!.SnapshotVersion.Should().Be(1);
        result.Value.Diff.NewCompetitors.Should().ContainSingle(c => c.LastName == "SMITH");
    }

    [Fact]
    public async Task Full_name_column_is_parsed_via_name_projector()
    {
        var svc = new ImportPreviewService(MakeRepo());
        var tsv = "Name\tYOB\nSMITH, John\t2005";

        var result = await svc.PreviewAsync(SeriesId, tsv);

        result.Succeeded.Should().BeTrue();
        result.Value!.Diff.NewCompetitors.Should().ContainSingle(c =>
            c.LastName == "SMITH" && c.FirstName == "John");
    }

    // ── Minimal fake repository ───────────────────────────────────────────────

    private sealed class FakeRepository : IEventSeriesRepository
    {
        private readonly EventSeriesSnapshot? _snapshot;

        public FakeRepository(EventSeriesSnapshot? snapshot) => _snapshot = snapshot;

        public Task<EventSeriesSnapshot?> LoadSnapshotAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(id == _snapshot?.Id ? _snapshot : null);

        public Task<IReadOnlyList<EventSeriesSummary>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EventSeriesSummary>>(Array.Empty<EventSeriesSummary>());

        public Task<Domain.Series.EventSeries?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult<Domain.Series.EventSeries?>(null);

        public Task AddAsync(Domain.Series.EventSeries series, CancellationToken ct = default)
            => Task.CompletedTask;

        public void Update(Domain.Series.EventSeries series) { }

        public void Remove(Domain.Series.EventSeries series) { }
    }
}

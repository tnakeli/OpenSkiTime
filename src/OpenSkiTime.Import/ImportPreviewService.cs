using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Import;

/// <summary>
/// Parses pasted TSV text against the current Event Series snapshot and
/// returns an <see cref="ImportDiff"/> for the user to review.
/// No data is written. The snapshot <see cref="EventSeriesSnapshot.Version"/>
/// is embedded in the diff so the apply service can detect concurrent changes.
/// </summary>
public sealed class ImportPreviewService
{
    private readonly IEventSeriesRepository _repository;

    public ImportPreviewService(IEventSeriesRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<Result<ImportPreviewResult>> PreviewAsync(
        Guid eventSeriesId,
        string tsvText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tsvText))
        {
            return Result<ImportPreviewResult>.Failure("Paste text is empty.");
        }

        var snapshot = await _repository.LoadSnapshotAsync(eventSeriesId, ct);
        if (snapshot is null)
        {
            return Result<ImportPreviewResult>.Failure(
                $"Event Series {eventSeriesId} not found.");
        }

        var competitionLabels = snapshot.Competitions.Select(c => c.ShortLabel);
        var mapper = new HeaderMapper(competitionLabels);
        var rows = TsvTokenizer.Tokenize(tsvText);

        if (rows.Count == 0)
        {
            return Result<ImportPreviewResult>.Failure("No rows found in pasted text.");
        }

        var mappings = mapper.MapHeaders(rows[0]);
        var dataRows = RowParser.Parse(rows, mappings);
        var diff = DiffEngine.Compute(snapshot, dataRows);

        return Result<ImportPreviewResult>.Success(
            new ImportPreviewResult(snapshot.Version, diff));
    }
}

/// <summary>
/// Carries the diff and the snapshot version needed by the apply step.
/// </summary>
public sealed record ImportPreviewResult(long SnapshotVersion, ImportDiff Diff);

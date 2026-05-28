using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Application.Abstractions;

/// <summary>
/// Builds a preview diff from pasted TSV text against the current snapshot.
/// Pure — no DB writes.
/// </summary>
public interface IImportPreviewService
{
    Task<Result<object>> BuildPreviewAsync(
        Guid eventSeriesId,
        string tsvText,
        CancellationToken ct = default);
}

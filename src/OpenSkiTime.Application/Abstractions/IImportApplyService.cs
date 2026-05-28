using OpenSkiTime.Application.Common;

namespace OpenSkiTime.Application.Abstractions;

/// <summary>
/// Applies a previously computed import diff to the database.
/// Transactional — either all changes commit or none do.
/// </summary>
public interface IImportApplyService
{
    Task<Result> ApplyAsync(
        Guid eventSeriesId,
        string tsvText,
        long snapshotVersion,
        CancellationToken ct = default);
}

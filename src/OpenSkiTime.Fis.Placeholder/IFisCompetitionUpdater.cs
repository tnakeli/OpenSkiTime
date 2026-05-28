namespace OpenSkiTime.Fis;

/// <summary>
/// Seam for "Update from FIS API". In feature 001 the only registered
/// implementation is <see cref="NotImplementedFisUpdater"/>, which performs
/// no network I/O (Constitution Principle IV, FR-011, SC-007).
/// </summary>
public interface IFisCompetitionUpdater
{
    Task<FisUpdateResult> UpdateAsync(Guid competitionId, CancellationToken ct = default);
}

public abstract record FisUpdateResult
{
    public sealed record NotImplemented(string UserMessage) : FisUpdateResult;

    public sealed record Updated(int FieldsChanged) : FisUpdateResult;

    public sealed record Failed(string Reason) : FisUpdateResult;
}

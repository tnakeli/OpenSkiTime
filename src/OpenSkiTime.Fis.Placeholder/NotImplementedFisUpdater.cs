namespace OpenSkiTime.Fis;

/// <summary>
/// Default <see cref="IFisCompetitionUpdater"/> for feature 001. Performs
/// zero network I/O. Replaced by a real implementation in a later feature
/// when FIS API integration lands; until then this is the deliberate
/// guarantee that clicking "Update from FIS API" never reaches the network.
/// </summary>
public sealed class NotImplementedFisUpdater : IFisCompetitionUpdater
{
    private const string Message = "FIS API update is not yet available in this release.";

    public Task<FisUpdateResult> UpdateAsync(Guid competitionId, CancellationToken ct = default)
        => Task.FromResult<FisUpdateResult>(new FisUpdateResult.NotImplemented(Message));
}

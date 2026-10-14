using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Desktop;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    // Saves the cached FIS list's points for every competitor with a Code, as Update from FIS list and Save do, without
    // changing names or other values. Draws use the points saved in the series.
    internal static async Task SaveCachedFisPointsAsync(SeriesWorkspace workspace, FisLocalStore cache)
    {
        var list = FisPointsListReader.Read(cache.LoadList() ?? throw new InvalidOperationException("No cached FIS list."));
        var desk = await workspace.ReadCompetitorDeskAsync();
        var rows = desk.Competitors.Where(x => !string.IsNullOrWhiteSpace(x.Values.FederationCode)).Select(x => new DeskBatchRow(x.Id, x.Values, [],
            CompetitorFisPoints.FromList(list.ListCode, list.Athletes.TryGetValue(x.Values.FederationCode!, out var athlete) ? athlete.Points : null), true)).ToArray();
        if (rows.Length == 0) { return; }
        await workspace.ApplyDeskBatchAsync(new DeskBatch(desk.SeriesId, desk.Revision, rows, [],
            [new FisPointsListInfo(list.ListCode, list.Name, list.ValidFrom, list.ValidTo, list.PenaltyRules)]));
    }
}

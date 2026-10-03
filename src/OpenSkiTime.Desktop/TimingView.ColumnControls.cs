using Avalonia.Controls;

namespace OpenSkiTime.Desktop;

public sealed partial class TimingView
{
    private ColumnGridController<TimingGridRow>? _rankingColumnControls;
    internal Flyout? RankingFilterMenu => _rankingColumnControls?.FilterMenu;

    private void InstallRankingColumnControls(MainViewModel? vm)
    {
        _rankingColumnControls?.Detach(); _rankingColumnControls = null;
        if (vm is null) { return; }
        _rankingColumnControls = new(this.FindControl<DataGrid>("RankingGrid")!, vm.RankingView,
            "Ranking", RankingColumnValue, (row, key) => key switch
            {
                "DisplayTime" => row.DisplayTime, "TotalTime" => row.TotalTime,
                _ => ColumnGridController<TimingGridRow>.Text(RankingColumnValue(row, key))
            }, changing: active => _synchronizingSelection = active);
    }

    private static object? RankingColumnValue(TimingGridRow row, string key) => key switch
    {
        "DisplayRank" => row.DisplayRank, "Bib" => row.Bib, "Name" => row.Name,
        "Result.Entry.Entrant.Athlete.Nation" => row.Result.Entry.Entrant.Athlete.Nation,
        "Result.Entry.Entrant.Athlete.Club" => row.Result.Entry.Entrant.Athlete.Club,
        "DisplayTime" => row.Result.Hundredths, "TotalTime" => row.Total, _ => null
    };
}

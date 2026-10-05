using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;

namespace OpenSkiTime.Desktop;

public sealed partial class TimingView
{
    private const string RankedIntermediatesKey = nameof(TimingGridRow.RankedIntermediates) + "[";
    private ColumnGridController<TimingGridRow>? _rankingColumnControls;
    internal Flyout? RankingFilterMenu => _rankingColumnControls?.FilterMenu;

    private void InstallRankingColumnControls(MainViewModel? vm)
    {
        _rankingColumnControls?.Detach(); _rankingColumnControls = null;
        if (vm is null) { return; }
        // A reinstall starts unsorted so the view never keeps a comparer whose header no longer shows it.
        vm.RankingView.SortDescriptions.Clear();
        _rankingColumnControls = new(this.FindControl<DataGrid>("RankingGrid")!, vm.RankingView,
            "Ranking", RankingColumnValue, (row, key) => key switch
            {
                "DisplayTime" => row.DisplayTime, "TotalTime" => row.TotalTime, "PreviousRunRanked" => row.PreviousRunRanked,
                _ when IntermediateIndex(key) is { } index => row.RankedIntermediates.ElementAtOrDefault(index) ?? "",
                _ => ColumnGridController<TimingGridRow>.Text(RankingColumnValue(row, key))
            }, changing: active => _synchronizingSelection = active);
    }

    // The time column names the current run; in later runs the previous run and every intermediate get their own column.
    private void ConfigureRankingColumns()
    {
        if (_viewModel is not { } vm) { return; }
        var grid = this.FindControl<DataGrid>("RankingGrid")!;
        grid.Columns.Single(x => x.Tag is "runTime").Header = string.Create(CultureInfo.InvariantCulture, $"RUN {vm.TimingRun}");
        grid.Columns.Single(x => x.Tag is "previousRun").Header = string.Create(CultureInfo.InvariantCulture, $"RUN {Math.Max(1, vm.TimingRun - 1)}");
        var count = vm.TimingCheckpoints.Count;
        var existing = grid.Columns.Count(x => x.Tag is "intermediate");
        for (var i = existing; i < count; i++)
        {
            grid.Columns.Insert(grid.Columns.IndexOf(grid.Columns.Single(x => x.Tag is "runTime")), new DataGridTextColumn
            {
                Header = string.Create(CultureInfo.InvariantCulture, $"INTERM {i + 1}"), Tag = "intermediate", Width = new DataGridLength(96),
                Binding = new Binding(string.Create(CultureInfo.InvariantCulture, $"{RankedIntermediatesKey}{i}]"))
            });
        }
        var splitColumns = grid.Columns.Where(x => x.Tag is "intermediate").ToArray();
        for (var i = 0; i < splitColumns.Length; i++) { splitColumns[i].IsVisible = i < count; }
        // Header controls are attached per column, so a newly added intermediate column needs them too.
        if (count > existing) { InstallRankingColumnControls(vm); }
    }

    private static int? IntermediateIndex(string key) => key.StartsWith(RankedIntermediatesKey, StringComparison.Ordinal)
        && int.TryParse(key.AsSpan(RankedIntermediatesKey.Length, key.Length - RankedIntermediatesKey.Length - 1),
            NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;

    private static object? RankingColumnValue(TimingGridRow row, string key) => key switch
    {
        "DisplayRank" => row.DisplayRank, "Bib" => row.Bib, "Name" => row.Name,
        "Result.Entry.Entrant.Athlete.Nation" => row.Result.Entry.Entrant.Athlete.Nation,
        "Result.Entry.Entrant.Athlete.Club" => row.Result.Entry.Entrant.Athlete.Club,
        "DisplayTime" => row.Result.Hundredths, "TotalTime" => row.Total, "PreviousRunRanked" => row.PreviousRunHundredths,
        _ when IntermediateIndex(key) is { } index => row.Result.Splits.ElementAtOrDefault(index)?.Hundredths,
        _ => null
    };
}

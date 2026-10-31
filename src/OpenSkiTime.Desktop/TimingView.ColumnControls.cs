using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

public sealed partial class TimingView
{
    private const double GroupIndent = 20;
    private const string RankedIntermediatesKey = nameof(TimingGridRow.RankedIntermediates) + "[";
    private ColumnGridController<TimingGridRow>? _rankingColumnControls;
    private ScrollBar? _rankingScrollBar;
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
                // Sorted by the number, filtered by what is shown (a forerunner's "F · A").
                "BibText" => row.BibText,
                "DisplayTime" => row.DisplayTime, "TotalTime" => row.TotalTime, "PreviousRunRanked" => row.PreviousRunRanked,
                _ when IntermediateIndex(key) is { } index => row.RankedIntermediates.ElementAtOrDefault(index) ?? "",
                _ => ColumnGridController<TimingGridRow>.Text(RankingColumnValue(row, key))
            }, changing: active => _synchronizingSelection = active);
    }

    // The time column names the current run; in later runs the previous run and every intermediate get their own column.
    // The latest finishers grid above the ranking gets the same columns, so its cells line up with the ranking.
    private void ConfigureRankingColumns()
    {
        if (_viewModel is not { } vm) { return; }
        var count = vm.TimingCheckpoints.Count;
        var existing = 0;
        foreach (var name in new[] { "RankingGrid", "LatestFinishersGrid" })
        {
            var grid = this.FindControl<DataGrid>(name)!;
            grid.Columns.Single(x => x.Tag is "runTime").Header = string.Create(CultureInfo.InvariantCulture, $"RUN {vm.TimingRun}");
            grid.Columns.Single(x => x.Tag is "previousRun").Header = string.Create(CultureInfo.InvariantCulture, $"RUN {Math.Max(1, vm.TimingRun - 1)}");
            var present = grid.Columns.Count(x => x.Tag is "intermediate");
            if (name == "RankingGrid") { existing = present; }
            for (var i = present; i < count; i++)
            {
                grid.Columns.Insert(grid.Columns.IndexOf(grid.Columns.Single(x => x.Tag is "runTime")), new DataGridTextColumn
                {
                    Header = string.Create(CultureInfo.InvariantCulture, $"INTERM {i + 1}"), Tag = "intermediate", Width = new DataGridLength(96),
                    Binding = new Binding(string.Create(CultureInfo.InvariantCulture, $"{RankedIntermediatesKey}{i}]"))
                });
            }
            var splitColumns = grid.Columns.Where(x => x.Tag is "intermediate").ToArray();
            for (var i = 0; i < splitColumns.Length; i++) { splitColumns[i].IsVisible = i < count; }
        }
        // Header controls are attached per column, so a newly added intermediate column needs them too.
        if (count > existing) { InstallRankingColumnControls(vm); }
    }

    // True while the ranking is wider than its pane and scrolls sideways. The latest finishers strip has no scroll bar of
    // its own and cannot follow that offset, so it hides; the same finishers stay marked (◆) in the ranking itself.
    public static readonly StyledProperty<bool> RankingOverflowsProperty = AvaloniaProperty.Register<TimingView, bool>(nameof(RankingOverflows));
    public bool RankingOverflows { get => GetValue(RankingOverflowsProperty); private set => SetValue(RankingOverflowsProperty, value); }

    // Copy the ranking's laid-out column widths, order and visibility to the latest finishers grid (no headers of its own)
    // and move it by the ranking's category indent, so its cells sit under the ranking's cells.
    private void AlignLatestFinishers()
    {
        var ranking = this.FindControl<DataGrid>("RankingGrid")!;
        var latest = this.FindControl<DataGrid>("LatestFinishersGrid")!;
        // Avalonia indents grouped rows by 20 px per group level (its default sublevel indent); the latest grid is not grouped.
        var indent = ranking.ItemsSource is DataGridCollectionView { GroupDescriptions.Count: var levels } ? levels * GroupIndent : 0;
        // The ranking's horizontal scroll range is positive exactly when its columns are wider than its pane. The grid sets
        // that range after its own layout pass, so a change of the range re-runs this alignment too.
        var bar = HorizontalBar(ranking);
        if (bar is not null && !ReferenceEquals(bar, _rankingScrollBar))
        {
            _rankingScrollBar = bar;
            bar.PropertyChanged += (_, e) =>
            { if (e.Property == RangeBase.MaximumProperty || e.Property == RangeBase.ValueProperty) { AlignLatestFinishers(); } };
        }
        var overflows = bar is not null && (bar.Maximum > 0.5 || bar.Value > 0.5);
        if (RankingOverflows != overflows) { RankingOverflows = overflows; }
        if (!latest.IsEffectivelyVisible || ranking.Columns.Count != latest.Columns.Count) { return; }
        for (var i = 0; i < ranking.Columns.Count; i++)
        {
            var source = ranking.Columns[i];
            var target = latest.Columns[i];
            if (target.IsVisible != source.IsVisible) { target.IsVisible = source.IsVisible; }
            if (target.DisplayIndex != source.DisplayIndex) { target.DisplayIndex = source.DisplayIndex; }
            if (!source.IsVisible || source.ActualWidth <= 0) { continue; }
            if (target.MinWidth != 0) { target.MinWidth = 0; }
            if (!target.Width.IsAbsolute || Math.Abs(target.Width.Value - source.ActualWidth) > 0.5)
            { target.Width = new DataGridLength(source.ActualWidth); }
        }
        var current = latest.RenderTransform is TranslateTransform move ? move.X : 0;
        if (Math.Abs(current - indent) > 0.5) { latest.RenderTransform = indent == 0 ? null : new TranslateTransform(indent, 0); }
    }

    private static ScrollBar? HorizontalBar(DataGrid grid)
        => grid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(x => x.Orientation == Orientation.Horizontal);

    private static int? IntermediateIndex(string key) => key.StartsWith(RankedIntermediatesKey, StringComparison.Ordinal)
        && int.TryParse(key.AsSpan(RankedIntermediatesKey.Length, key.Length - RankedIntermediatesKey.Length - 1),
            NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : null;

    private static object? RankingColumnValue(TimingGridRow row, string key) => key switch
    {
        "DisplayRank" => row.DisplayRank, "BibText" => row.Bib, "Name" => row.Name,
        "Result.Entry.Entrant.Athlete.Nation" => row.Result.Entry.Entrant.Athlete.Nation,
        "Result.Entry.Entrant.Athlete.Club" => row.Result.Entry.Entrant.Athlete.Club,
        "DisplayTime" => row.Result.Hundredths, "TotalTime" => row.Total, "PreviousRunRanked" => row.PreviousRunHundredths,
        _ when IntermediateIndex(key) is { } index => row.Result.Splits.ElementAtOrDefault(index)?.Hundredths,
        _ => null
    };
}

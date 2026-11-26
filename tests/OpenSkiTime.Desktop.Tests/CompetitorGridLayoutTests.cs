using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task CompetitorGridFiltersAndResizesWithManyReadableRaceColumns()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-grid-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 10, 2);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var races = Enumerable.Range(1, 24).Select(n => new CompetitionValues($"Synthetic race {n}", $"SL{n} W 2.10",
                date, Discipline.Slalom, RaceType.Fis, 2, 0, n.ToString("0000", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var series = await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2026/27"), races);
            var saved = await workspace.SaveDeskRowAsync(null, new("TESTONE", "Aino", 2007, "100001", "FIN", "Test club", Gender.Female),
                series.Competitions[0].Id, false, null, series.Revision);
            await workspace.SaveDeskRowAsync(null, new("TESTTWO", "Mikko", 2008, "100002", "SWE", "Other club", Gender.Male),
                series.Competitions[0].Id, false, null, saved.Revision);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitorsCommand.Execute(null);
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 1000, WindowState = WindowState.Normal };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var grid = window.FindControl<DataGrid>("CompetitorGrid")!;
            Assert.True(grid.CanUserResizeColumns); Assert.Equal(3, grid.FrozenColumnCount);
            Assert.Equal(24, grid.Columns.Count(x => x.Tag is "competition-entry"));
            Assert.All(grid.Columns.Where(x => x.Tag is "competition-entry"), x => Assert.NotNull(x.HeaderTemplate));
            Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "SL1 W\n2.10");
            var nationTitle = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(x => Equals(x.Content, "NAT"))
                .GetVisualDescendants().OfType<TextBlock>().Single(x => x.Text == "NAT");
            Assert.True(nationTitle.Bounds.Width >= 20);
            Assert.True(nationTitle.Bounds.Height <= grid.ColumnHeaderHeight);
            Assert.DoesNotContain(grid.ContextMenu!.Items.OfType<MenuItem>(), x => Equals(x.Header, "Undo changes to selected row"));

            Assert.Null(window.FindControl<TextBox>("CompetitorFilterInput"));
            Assert.Null(window.FindControl<ComboBox>("CompetitorGenderFilter"));
            Assert.Null(window.FindControl<TextBox>("CompetitionColumnFilterInput"));
            Button FilterIcon(string key) => grid.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "CompetitorFilterButton_" + key);
            void Click(Button button) { button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
            TextBox OpenFilter(string key)
            {
                Click(FilterIcon(key));
                return ((StackPanel)window.CompetitorFilterMenu!.Content!).Children.OfType<TextBox>().Single();
            }
            void ApplyFilter()
            {
                var panel = (StackPanel)window.CompetitorFilterMenu!.Content!;
                Click(panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(x => x.Name == "CompetitorFilterApply"));
            }
            void SetFilter(string key, string value)
            {
                if (!string.IsNullOrWhiteSpace(vm.CompetitorColumnFilter(key))) { Click(FilterIcon(key)); }
                if (value.Length == 0) { return; }
                OpenFilter(key).Text = value; ApplyFilter();
            }
            Assert.DoesNotContain(grid.GetVisualDescendants().OfType<TextBox>(), x => x.Name?.StartsWith("CompetitorFilter_", StringComparison.Ordinal) == true);
            var cancelledEditor = OpenFilter(nameof(CompetitorGridRow.Nation)); cancelledEditor.Text = "SWE";
            Click(((StackPanel)window.CompetitorFilterMenu!.Content!).Children.OfType<StackPanel>().Single().Children.OfType<Button>().Single(x => x.Name == "CompetitorFilterCancel"));
            Assert.Equal("", vm.CompetitorColumnFilter(nameof(CompetitorGridRow.Nation)));
            SetFilter(nameof(CompetitorGridRow.Nation), "fin");
            SetFilter(nameof(CompetitorGridRow.BirthYearText), "2007");
            Assert.Equal("TESTONE", Assert.Single(vm.VisibleCompetitors, x => !x.IsPlaceholder).Surname);
            SetFilter(nameof(CompetitorGridRow.GenderText), "Men");
            Assert.DoesNotContain(vm.VisibleCompetitors, x => !x.IsPlaceholder);
            SetFilter(nameof(CompetitorGridRow.Nation), ""); SetFilter(nameof(CompetitorGridRow.BirthYearText), "");
            Assert.Equal("TESTTWO", Assert.Single(vm.VisibleCompetitors, x => !x.IsPlaceholder).Surname);
            var genderHeader = FilterIcon(nameof(CompetitorGridRow.GenderText)).Parent as Grid;
            Assert.Equal(Avalonia.Media.FontStyle.Italic, genderHeader!.Children.OfType<TextBlock>().Single().FontStyle);
            Click(FilterIcon(nameof(CompetitorGridRow.GenderText)));
            Assert.Equal(Avalonia.Media.FontStyle.Normal, genderHeader.Children.OfType<TextBlock>().Single().FontStyle);
            Assert.Equal("", vm.CompetitorColumnFilter(nameof(CompetitorGridRow.GenderText)));
            Assert.Equal(2, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));
            var genderEditor = OpenFilter(nameof(CompetitorGridRow.GenderText));
            genderEditor.Focus(); window.KeyTextInput("men"); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control); window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs(); Assert.Equal("", genderEditor.Text);
            genderEditor.Text = "Men"; ApplyFilter();
            Assert.Equal("Men", Assert.Single(vm.VisibleCompetitors, x => !x.IsPlaceholder).GenderText);
            Click(FilterIcon(nameof(CompetitorGridRow.GenderText)));
            Assert.Equal(2, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));
            Assert.DoesNotContain(vm.VisibleCompetitors, x => x.IsPendingDelete);
            var selected = vm.VisibleCompetitors.First(x => !x.IsPlaceholder);
            grid.SelectedItem = selected; selected.Club = "Unsaved club";
            SetFilter(nameof(CompetitorGridRow.Surname), selected.Surname.ToLowerInvariant());
            Assert.Same(selected, vm.SelectedCompetitorRow); Assert.True(selected.HasPendingChanges);
            Assert.Equal("Unsaved club", selected.Club);
            Assert.DoesNotContain((await workspace.ReadCompetitorDeskAsync()).Competitors, x => x.Values.Club == "Unsaved club");
            SetFilter(nameof(CompetitorGridRow.Surname), "");
            // Only slalom races: the other disciplines' points columns are hidden, the points stay with the competitors.
            Assert.Equal(["FisSl"], grid.Columns.Where(x => x.SortMemberPath?.StartsWith("Fis", StringComparison.Ordinal) == true && x.IsVisible)
                .Select(x => x.SortMemberPath));
            Assert.Equal(24, grid.Columns.Count(x => x.Tag is "competition-entry" && x.IsVisible));
            var entryKey = grid.Columns.First(x => x.Tag is "competition-entry").SortMemberPath;
            SetFilter(entryKey, "Yes"); Assert.DoesNotContain(vm.VisibleCompetitors, x => !x.IsPlaceholder);
            Assert.True(vm.VisibleCompetitors[^1].IsPlaceholder);
            SetFilter(entryKey, ""); Assert.Equal(2, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));

            window.UpdateLayout();
            // Exercise the actual header edge rather than only assigning a width property.
            var codeColumn = grid.Columns[1];
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(x => Equals(x.Content, "CODE"));
            var edge = header.TranslatePoint(new Point(header.Bounds.Width - 1, header.Bounds.Height / 2), window)!.Value;
            var before = codeColumn.ActualWidth;
            window.MouseDown(edge, MouseButton.Left);
            window.MouseMove(edge + new Vector(35, 0));
            window.MouseUp(edge + new Vector(35, 0), MouseButton.Left);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(codeColumn.ActualWidth > before, $"Column width stayed at {codeColumn.ActualWidth} (was {before}).");
            var widened = codeColumn.ActualWidth;
            edge = header.TranslatePoint(new Point(header.Bounds.Width - 1, header.Bounds.Height / 2), window)!.Value;
            window.MouseDown(edge, MouseButton.Left);
            window.MouseMove(edge - new Vector(20, 0));
            window.MouseUp(edge - new Vector(20, 0), MouseButton.Left);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(codeColumn.ActualWidth < widened);
            var horizontal = grid.GetVisualDescendants().OfType<ScrollBar>().First(x => x.Orientation == Avalonia.Layout.Orientation.Horizontal
                && !x.GetVisualAncestors().OfType<TextBox>().Any());
            Assert.True(horizontal.Maximum > 0);
            horizontal.Value = horizontal.Maximum; window.UpdateLayout();
            Assert.Contains(grid.GetVisualDescendants().OfType<TextBlock>(), x => x.Text == "SL24 W\n2.10");
            var output = Environment.GetEnvironmentVariable("OPENSKITIME_GRID_VISUAL_DIR");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output); horizontal.Value = 0; window.UpdateLayout();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1280, 1000), new Vector(96, 96));
                bitmap.Render(window); bitmap.Save(Path.Combine(output, "competitor-grid.png"), PngBitmapEncoderOptions.Default);

            }
            await vm.DiscardDeskChangesCommand.ExecuteAsync(null);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaTheory]
    [InlineData(980, 680)]
    [InlineData(1280, 800)]
    public async Task CompetitorGridFillsTheWindowWithItsHeaderAndScrollBarsInView(int width, int height)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-grid-fill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "test.ost"); var date = new DateOnly(2026, 11, 28);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var races = Enumerable.Range(1, 8).Select(n => new CompetitionValues($"Synthetic race {n}", $"{(n % 2 == 0 ? "GS" : "SL")}{n} W 28.11",
                date, n % 2 == 0 ? Discipline.GiantSlalom : Discipline.Slalom, RaceType.Fis, 2, 0,
                n.ToString("0000", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            var series = await workspace.CreateAsync(path, new("Test", "Test place", "Club", date, date, "FIN", "2026/27"), races);
            var revision = series.Revision;
            for (var i = 0; i < 60; i++)
            {
                revision = (await workspace.SaveDeskRowAsync(null, new($"TEST{i:00}", "Aino", 2007, $"1{i:00000}", "FIN", "Test club", Gender.Female),
                    series.Competitions[0].Id, false, null, revision)).Revision;
            }
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
                fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.ShowCompetitorsCommand.Execute(null);
            var window = new MainWindow { DataContext = vm, Width = width, Height = height, WindowState = WindowState.Normal };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var grid = window.FindControl<DataGrid>("CompetitorGrid")!;
            var tools = window.FindControl<ScrollViewer>("CompetitorToolsScroll")!;
            // The view is not inside the scrolling page: the wheel at the end of the grid cannot scroll anything under it.
            Assert.False(window.FindControl<ScrollViewer>("WorkspaceScroll")!.IsVisible);
            Assert.DoesNotContain(grid.GetVisualAncestors(), x => x is ScrollViewer);
            // The grid takes the free height: it ends right above the tools, and the tools end inside the window.
            var gridBottom = grid.TranslatePoint(new Point(0, grid.Bounds.Height), window)!.Value.Y;
            var toolsTop = tools.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var toolsBottom = tools.TranslatePoint(new Point(0, tools.Bounds.Height), window)!.Value.Y;
            Assert.InRange(toolsTop - gridBottom, 0, 20);
            Assert.True(toolsBottom <= window.Bounds.Height - 20, $"Tools end at {toolsBottom} of {window.Bounds.Height}.");
            Assert.True(grid.Bounds.Height >= 300, $"Grid is {grid.Bounds.Height} high.");
            // Scroll bars stay visible, and the horizontal one is inside the window.
            var bars = grid.GetVisualDescendants().OfType<ScrollBar>().Where(x => !x.GetVisualAncestors().OfType<TextBox>().Any()).ToArray();
            Assert.NotEmpty(bars);
            Assert.All(bars, x => Assert.False(x.AllowAutoHide));
            var horizontal = bars.Single(x => x.Orientation == Avalonia.Layout.Orientation.Horizontal);
            Assert.True(horizontal.IsVisible && horizontal.Maximum > 0);
            Assert.True(horizontal.TranslatePoint(new Point(0, horizontal.Bounds.Height), window)!.Value.Y <= gridBottom);
            // Points columns of the series' disciplines only.
            Assert.Equal(["FisSl", "FisGs"], grid.Columns.Where(x => x.SortMemberPath?.StartsWith("Fis", StringComparison.Ordinal) == true && x.IsVisible)
                .Select(x => x.SortMemberPath));
            // The header stays in place when the wheel reaches the end of the rows.
            var header = grid.GetVisualDescendants().OfType<DataGridColumnHeader>().Single(x => Equals(x.Content, "CODE"));
            var headerTop = header.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var inside = grid.TranslatePoint(new Point(grid.Bounds.Width / 2, grid.Bounds.Height / 2), window)!.Value;
            for (var i = 0; i < 40; i++) { window.MouseWheel(inside, new Vector(0, -3)); }
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(headerTop, header.TranslatePoint(new Point(0, 0), window)!.Value.Y);
            // Shift and the wheel scroll the wide grid sideways.
            Assert.Equal(0, horizontal.Value);
            window.MouseWheel(inside, new Vector(0, -1), RawInputModifiers.Shift);
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.True(horizontal.Value > 0, "Shift and the wheel did not scroll sideways.");
            // One tool is open at a time.
            var expanders = tools.GetVisualDescendants().OfType<Expander>().Where(x => x.Parent?.Parent == tools).ToArray();
            Assert.Equal(3, expanders.Length);
            expanders[0].IsExpanded = true; Dispatcher.UIThread.RunJobs();
            expanders[2].IsExpanded = true; Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(expanders[0].IsExpanded); Assert.False(vm.IsFisPanelOpen);

            // Everything optional open at once: Undo with many changes, the warnings, a tool and a row note with
            // Match checked. The grid keeps its minimum and nothing ends below the view.
            var rows = vm.VisibleCompetitors.Where(x => !x.IsPlaceholder).Take(12).ToArray();
            foreach (var changed in rows) { changed.Club = "Changed club"; }
            rows[0].WarningText = "Code is 100000 here and 199999 on regs.fissi.fi; confirm this match. " + new string('x', 400);
            vm.SelectedCompetitorRow = rows[0];
            for (var i = 0; i < 12; i++) { vm.ImportWarnings.Add($"Synthetic warning {i} about a registration that was not updated."); }
            Dispatcher.UIThread.RunJobs();
            vm.IsChangeReviewOpen = true;
            window.FindControl<Expander>("ImportWarningsSection")!.IsExpanded = true;
            for (var pass = 0; pass < 3; pass++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
            var view = window.FindControl<Grid>("CompetitorsWorkspace")!;
            var workspaceBottom = view.TranslatePoint(new Point(0, view.Bounds.Height), window)!.Value.Y;
            double Bottom(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height), window)!.Value.Y;
            var match = window.FindControl<CheckBox>("MatchCheckedBox")!;
            Assert.True(match.IsEffectivelyVisible);
            Assert.True(Bottom(match) <= workspaceBottom + 0.5, $"Match checked ends at {Bottom(match)}, the view at {workspaceBottom}.");
            Assert.True(Bottom(tools) <= workspaceBottom + 0.5, $"Tools end at {Bottom(tools)}, the view at {workspaceBottom}.");
            Assert.True(Bottom(horizontal) <= Bottom(grid) + 0.5);
            Assert.True(grid.Bounds.Height >= grid.MinHeight - 0.5, $"Grid is {grid.Bounds.Height} high.");

            // A filter of a points column that gets hidden is cleared: its funnel is the only way to clear it.
            await vm.DiscardDeskChangesCommand.ExecuteAsync(null);
            vm.SetCompetitorColumnFilter("FisGs", "1");
            Assert.DoesNotContain(vm.VisibleCompetitors, x => !x.IsPlaceholder);
            for (var i = 0; i < vm.Competitions.Count; i++)
            {
                var competition = vm.Competitions[i];
                if (competition.Values.Discipline == Discipline.GiantSlalom)
                { vm.Competitions[i] = competition with { Values = competition.Values with { Discipline = Discipline.Slalom } }; }
            }
            Dispatcher.UIThread.RunJobs();
            Assert.False(grid.Columns.Single(x => x.SortMemberPath == "FisGs").IsVisible);
            Assert.Equal("", vm.CompetitorColumnFilter("FisGs"));
            Assert.Equal(60, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }
}

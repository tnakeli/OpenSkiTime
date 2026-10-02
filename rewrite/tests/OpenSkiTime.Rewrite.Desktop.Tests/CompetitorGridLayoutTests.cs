using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

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
            Assert.All(grid.Columns.Where(x => x.SortMemberPath?.StartsWith("Fis", StringComparison.Ordinal) == true), x => Assert.True(x.IsVisible));
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
                bitmap.Render(window); bitmap.Save(Path.Combine(output, "competitor-grid.png"));

            }
            await vm.DiscardDeskChangesCommand.ExecuteAsync(null);
            window.Close();
        }
        finally { Directory.Delete(folder, true); }
    }
}

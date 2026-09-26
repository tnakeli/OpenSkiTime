using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(OpenSkiTime.Rewrite.Tests.HeadlessAppBuilder))]

namespace OpenSkiTime.Rewrite.Tests;

public sealed class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task DesktopCreatesEditsBacksUpAndReopensSeries()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-m1-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "series.ost");
            var backup = Path.Combine(root, "transfer.ost");
            var dialogs = new FileDialogsStub { NewPath = file, OpenPath = backup, BackupPath = backup };
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var vm = new MainViewModel(workspace, dialogs);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.Name = "Levi Weekend";
            vm.Location = "Levi";
            vm.Organizer = "Test Club";
            vm.Season = "2025/26";
            vm.StartDateText = "06.05.2026";
            vm.EndDateText = "07.05.2026";
            AssertNumericDates(window);
            Click(window, "Create series file");
            await vm.CreateSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(File.Exists(file));
            Assert.True(vm.IsOpen);

            vm.StartDateText = "31.02.2026";
            Click(window, "Save series");
            await vm.SaveSeriesCommand.ExecutionTask!;
            Assert.True(vm.IsError);
            Assert.Equal(new DateOnly(2026, 5, 6), (await workspace.ReadAsync()).Values.StartDate);
            vm.StartDateText = "6.5.2026";
            Click(window, "Save series");
            await vm.SaveSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal("06.05.2026", vm.StartDateText);

            Click(window, "Add competition");
            vm.CompetitionName = "Slalom";
            vm.CompetitionShortLabel = "3.1 SL";
            AssertNumericDates(window);
            window.Width = 980;
            AssertFieldSpacing(window);
            window.Width = 1280;
            AssertFieldSpacing(window);
            Click(window, "Save competition");
            await vm.SaveCompetitionCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Single(vm.Competitions);
            Assert.Equal(new DateOnly(2026, 5, 6), vm.Competitions[0].Values.Date);
            Assert.Equal(new DateOnly(2026, 5, 7), (await workspace.ReadAsync()).Values.EndDate);

            Click(window, "Add competitor");
            var competitorRow = Assert.IsType<CompetitorGridRow>(vm.SelectedCompetitorRow);
            competitorRow.Surname = "Mäkelä";
            competitorRow.FirstName = "Aino";
            competitorRow.BirthYearText = "2010";
            competitorRow.GenderText = "Female";
            competitorRow.Nation = "fin";
            competitorRow.ImportedBibText = "27";
            competitorRow.IsParticipating = true;
            Click(window, "Save row");
            await vm.SaveSelectedCompetitorCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Same(competitorRow, vm.SelectedCompetitorRow);
            Assert.Equal("MÄKELÄ", competitorRow.Surname);
            Assert.Equal(27, Assert.Single((await workspace.ReadCompetitorDeskAsync()).Participations).ImportedBib);
            competitorRow.Club = "Unsaved draft";
            var selectedRace = vm.DeskCompetition;
            vm.DeskCompetition = null;
            Assert.Same(selectedRace, vm.DeskCompetition);
            Assert.True(vm.IsError);
            Click(window, "Discard draft");
            Assert.Equal(string.Empty, competitorRow.Club);
            competitorRow.Club = "Club A";
            Click(window, "Save row");
            await vm.SaveSelectedCompetitorCommand.ExecutionTask!;
            Click(window, "Undo saved");
            await vm.UndoDeskEditCommand.ExecutionTask!;
            Assert.Same(competitorRow, vm.SelectedCompetitorRow);
            Assert.Equal(string.Empty, competitorRow.Club);
            vm.CategoryLabel = "Girls U16";
            vm.CategoryMinYearText = "2010";
            vm.CategoryMaxYearText = "2011";
            vm.CategoryGenderText = "Female";
            window.GetVisualDescendants().OfType<Expander>()
                .Single(x => Equals(x.Header, "Category rules · birth-year range and gender")).IsExpanded = true;
            Click(window, "Save rule");
            await vm.SaveCategoryRuleCommand.ExecutionTask!;
            Assert.Equal("Girls U16", Assert.Single(vm.VisibleCompetitors).Category);
            vm.CompetitorFilterText = "NO MATCH";
            Assert.Empty(vm.VisibleCompetitors);
            vm.CompetitorFilterText = "MÄK";
            Assert.Single(vm.VisibleCompetitors);
            vm.CompetitorFilterText = string.Empty;

            Click(window, "Backup / transfer");
            await vm.BackupCommand.ExecutionTask!;
            Assert.True(File.Exists(backup));
            Click(window, "Close file");
            await vm.CloseSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsOpen);
            Click(window, "Open file");
            await vm.OpenSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Single(vm.Competitions);
            Assert.Equal("MÄKELÄ", Assert.Single(vm.VisibleCompetitors).Surname);
            Assert.Single(vm.CategoryRules);
            Assert.Equal(backup, vm.FileLabel);
            var startDateInput = window.FindControl<TextBox>("SeriesStartDateInput");
            Assert.NotNull(startDateInput);
            startDateInput.Text = "07.05.2026";
            var pickerButton = window.FindControl<Button>("SeriesStartDatePickerButton");
            Assert.NotNull(pickerButton);
            pickerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var datePicker = Assert.IsType<DatePicker>(Assert.IsType<Flyout>(pickerButton.Flyout).Content);
            Assert.Equal("MM MMMM", datePicker.MonthFormat);
            Assert.Equal(new DateTime(2026, 5, 7), datePicker.SelectedDate?.DateTime);
            datePicker.SelectedDate = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);
            Assert.Equal("06.05.2026", startDateInput.Text);
            Click(window, "Save series");
            await vm.SaveSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(new DateOnly(2026, 5, 6), (await workspace.ReadAsync()).Values.StartDate);
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Click(Window window, string label)
    {
        window.UpdateLayout();
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, label) && b.IsVisible);
        Assert.True(button.IsEnabled, $"{label} was disabled");
        button.Command?.Execute(button.CommandParameter);
    }

    private static void AssertNumericDates(Window window)
    {
        window.UpdateLayout();
        foreach (var name in new[] { "SeriesStartDateInput", "SeriesEndDateInput", "CompetitionDateInput" })
        {
            var input = window.FindControl<TextBox>(name);
            Assert.NotNull(input);
            var expected = name switch
            {
                "SeriesStartDateInput" => ((MainViewModel)window.DataContext!).StartDateText,
                "SeriesEndDateInput" => ((MainViewModel)window.DataContext!).EndDateText,
                _ => ((MainViewModel)window.DataContext!).CompetitionDateText,
            };
            Assert.Equal(expected, input.Text);
        }
    }

    private static void AssertFieldSpacing(Window window)
    {
        window.UpdateLayout();
        foreach (var field in window.GetVisualDescendants().OfType<StackPanel>()
            .Where(panel => panel.IsVisible && panel.Classes.Contains("field")))
        {
            var children = field.Children.OfType<Control>().ToArray();
            Assert.Equal(2, children.Length);
            Assert.True(children[1].Bounds.Top >= children[0].Bounds.Bottom + 4,
                $"The label touches its input in {children[0].GetType().Name}.");
            Assert.True(children[1].Bounds.Width >= 100,
                $"{(children[0] as TextBlock)?.Text} input became too narrow ({children[1].Bounds.Width:0}px).");
        }

        foreach (var grid in window.GetVisualDescendants().OfType<Grid>())
        {
            var fields = grid.Children.OfType<StackPanel>()
                .Where(panel => panel.IsVisible && panel.Classes.Contains("field"))
                .OrderBy(panel => panel.Bounds.Left).ToArray();
            for (var i = 1; i < fields.Length; i++)
            {
                Assert.True(fields[i - 1].Bounds.Right + 8 <= fields[i].Bounds.Left,
                    "Adjacent fields need a visible gutter.");
            }
        }
    }

    private sealed class FileDialogsStub : IFileDialogs
    {
        public required string NewPath { get; init; }
        public required string OpenPath { get; init; }
        public required string BackupPath { get; init; }
        public Task<string?> ChooseNewAsync(string suggestedName) => Task.FromResult<string?>(NewPath);
        public Task<string?> ChooseOpenAsync() => Task.FromResult<string?>(OpenPath);
        public Task<string?> ChooseBackupAsync(string suggestedName) => Task.FromResult<string?>(BackupPath);
        public Task<string?> ChooseLegacyDatabaseAsync() => Task.FromResult<string?>(null);
        public Task<bool> ConfirmRemoveAsync(string competitionName) => Task.FromResult(true);
        public Task<bool> ConfirmRemoveCompetitorAsync(string surname) => Task.FromResult(true);
    }
}

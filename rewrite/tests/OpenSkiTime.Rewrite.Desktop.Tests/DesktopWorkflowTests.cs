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
            var exchange = new EntryExchangeStub();
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var vm = new MainViewModel(workspace, dialogs, entryExchange: exchange);
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
            Assert.Equal(WorkspaceSection.Competitions, vm.ActiveSection);

            Click(window, "1  Event series");

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

            Click(window, "2  Competitions");

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
            Click(window, "Add competition");
            vm.CompetitionName = "Giant slalom";
            vm.CompetitionShortLabel = "3.2 GS";
            Click(window, "Save competition");
            await vm.SaveCompetitionCommand.ExecutionTask!;
            Assert.Equal(2, vm.Competitions.Count);

            Click(window, "3  Competitors");
            Assert.Contains(window.FindControl<DataGrid>("CompetitorGrid")!.Columns,
                column => Equals(column.Header, "3.1 SL"));
            Assert.Contains(window.FindControl<DataGrid>("CompetitorGrid")!.Columns,
                column => Equals(column.Header, "3.2 GS"));
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
            Assert.Equal(2, vm.ParticipationChoices.Count);
            var firstRaceParticipation = vm.ParticipationChoices.Single(x => x.Label == "3.1 SL");
            Assert.False(firstRaceParticipation.IsParticipating);
            firstRaceParticipation.IsParticipating = true;
            window.UpdateLayout();
            var firstRaceCheck = window.FindControl<ItemsControl>("ParticipationChoicesList")!
                .GetVisualDescendants().OfType<CheckBox>()
                .Single(x => ReferenceEquals(x.DataContext, firstRaceParticipation));
            firstRaceCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Participations.Count);
            var participation = vm.ParticipationChoices.Single(x => x.Label == "3.2 GS");
            Assert.True(participation.IsParticipating);
            participation.IsParticipating = false;
            window.UpdateLayout();
            var participationList = window.FindControl<ItemsControl>("ParticipationChoicesList");
            Assert.NotNull(participationList);
            Assert.True(participationList.IsVisible, "Participation list should be visible for the selected competitor.");
            var participationCheck = participationList.GetVisualDescendants().OfType<CheckBox>()
                .Single(x => ReferenceEquals(x.DataContext, participation));
            participationCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False((await workspace.ReadCompetitorDeskAsync()).Participations
                .Single(x => x.CompetitionId == participation.CompetitionId).Participates);
            participation.IsParticipating = true;
            participationCheck.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True((await workspace.ReadCompetitorDeskAsync()).Participations
                .Single(x => x.CompetitionId == participation.CompetitionId).Participates);
            var gridEntry = competitorRow.GridEntries.Single(x => x.Label == "3.1 SL");
            gridEntry.IsParticipating = false;
            await vm.SaveGridParticipationAsync(competitorRow, gridEntry);
            Assert.False((await workspace.ReadCompetitorDeskAsync()).Participations
                .Single(x => x.CompetitionId == gridEntry.CompetitionId).Participates);
            gridEntry.IsParticipating = true;
            await vm.SaveGridParticipationAsync(competitorRow, gridEntry);
            competitorRow.Club = "Unsaved draft";
            Click(window, "2  Competitions");
            Assert.Equal(WorkspaceSection.Competitors, vm.ActiveSection);
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

            exchange.ClipboardText = "Surname\tFirst name\tYear\tClub\t3.1 SL\tBib:3.1 SL\t3.2 GS\n"
                + "Mäkelä\tAino\t2010\tReview Club\tX\t11\t0\n"
                + "Laine\tLea\t2011\tNew Club\tX\t\t\n";
            Click(window, "Paste from Excel");
            await vm.PasteFromExcelCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(vm.IsImportReviewOpen);
            Assert.Equal(2, vm.ImportReviewRows.Count);
            Assert.True(window.FindControl<DataGrid>("CompetitorGrid")!.IsVisible);
            Assert.Equal(2, vm.VisibleCompetitors.Count);
            Assert.All(vm.VisibleCompetitors, row => Assert.True(row.IsImportHighlighted));
            Assert.Equal("NEW", vm.VisibleCompetitors.Single(x => x.Id is null).ImportMarker);
            Assert.True(vm.VisibleCompetitors.Single(x => x.Id == competitorRow.Id)
                .GridEntries.Single(x => x.Label == "3.1 SL").IsParticipating);
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            window.UpdateLayout();
            var changed = vm.ImportReviewRows.Single(x => !x.IsNew);
            Assert.True(changed.IsClubChanged);
            var stagedRow = vm.VisibleCompetitors.Single(x => x.Id == changed.CompetitorId);
            Assert.True(stagedRow.IsClubChanged);
            stagedRow.Club = "Edited in grid";
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Click(window, "Discard import");
            Assert.False(vm.IsImportReviewOpen);
            Assert.Single(vm.VisibleCompetitors);
            Assert.Equal(string.Empty, vm.VisibleCompetitors.Single().Club);
            Assert.False(vm.VisibleCompetitors.Single().IsImportHighlighted);
            Click(window, "Paste from Excel");
            await vm.PasteFromExcelCommand.ExecutionTask!;
            Assert.Equal("Review Club", vm.VisibleCompetitors.Single(x => x.Id == changed.CompetitorId).Club);
            vm.ImportSourceText += "changed source";
            Click(window, "Commit import");
            await vm.CommitImportCommand.ExecutionTask!;
            Assert.True(vm.IsError);
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            vm.ImportSourceText = exchange.ClipboardText;
            vm.VisibleCompetitors.Single(x => x.Id == changed.CompetitorId).Club = "Edited Club";
            vm.ImportReviewRows.Single(x => !x.IsNew).Entries.Single(x => x.Label == "3.1 SL").BibText = "12";
            var stagedNew = vm.VisibleCompetitors.Single(x => x.Id is null);
            var stagedEntry = stagedNew.GridEntries.Single(x => x.Label == "3.2 GS");
            stagedEntry.IsParticipating = true;
            await vm.SaveGridParticipationAsync(stagedNew, stagedEntry);
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Click(window, "Commit import");
            await vm.CommitImportCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.False(vm.IsImportReviewOpen);
            Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
            var importedDesk = await workspace.ReadCompetitorDeskAsync();
            var importedAthlete = importedDesk.Competitors.Single(x => x.Values.Surname == "MÄKELÄ");
            Assert.Equal("Edited Club", importedAthlete.Values.Club);
            Assert.Equal(12, importedDesk.Participations.Single(x =>
                x.CompetitorId == importedAthlete.Id
                && x.CompetitionId == vm.Competitions.Single(c => c.Values.ShortLabel == "3.1 SL").Id).ImportedBib);
            var importedNew = importedDesk.Competitors.Single(x => x.Values.Surname == "LAINE");
            Assert.True(importedDesk.Participations.Single(x => x.CompetitorId == importedNew.Id
                && x.CompetitionId == vm.Competitions.Single(c => c.Values.ShortLabel == "3.2 GS").Id).Participates);
            vm.SelectedCompetitorRow = vm.VisibleCompetitors.Single(x => x.Surname == "MÄKELÄ");
            window.FindControl<DataGrid>("CompetitorGrid")!.SelectedItems.Add(
                vm.VisibleCompetitors.Single(x => x.Surname == "LAINE"));
            Click(window, "Copy selection");
            await vm.CopySelectedCompetitorCommand.ExecutionTask!;
            Assert.Contains("Bib:3.1 SL", exchange.CopiedText);
            Assert.Equal(3, TsvExchange.Parse(exchange.CopiedText).Count);
            Click(window, "Export selection TSV");
            await vm.ExportSelectedTsvCommand.ExecutionTask!;
            Assert.Equal(exchange.CopiedText, exchange.SavedText);

            exchange.ClipboardText = "Surname\nUnknown";
            Click(window, "Paste from Excel");
            await vm.PasteFromExcelCommand.ExecutionTask!;
            Assert.True(Assert.Single(vm.ImportReviewRows).NeedsApproval);
            Click(window, "Commit import");
            await vm.CommitImportCommand.ExecutionTask!;
            Assert.True(vm.IsError);
            Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
            Click(window, "Discard import");
            Assert.False(vm.IsImportReviewOpen);

            Click(window, "Backup / transfer");
            await vm.BackupCommand.ExecutionTask!;
            Assert.True(File.Exists(backup));
            Click(window, "Close file");
            await vm.CloseSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsOpen);
            Click(window, "Open file");
            await vm.OpenSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(2, vm.Competitions.Count);
            Assert.Contains(vm.VisibleCompetitors, x => x.Surname == "MÄKELÄ");
            Assert.Single(vm.CategoryRules);
            Assert.Equal(backup, vm.FileLabel);
            Click(window, "1  Event series");
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
            Click(window, "3  Competitors");
            vm.SelectedCompetitorRow = vm.VisibleCompetitors.Single(x => x.Surname == "LAINE");
            Click(window, "Remove competitor");
            await vm.RemoveCompetitorCommand.ExecutionTask!;
            Assert.True(vm.SelectedCompetitorRow.IsPendingDelete);
            Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
            var deletion = vm.DeskChangeLog.Single(x => x.Kind == DeskChangeKind.Delete);
            vm.RestoreDeskChangeCommand.Execute(deletion);
            await vm.RestoreDeskChangeCommand.ExecutionTask!;
            Assert.False(vm.SelectedCompetitorRow.IsPendingDelete);
            Click(window, "Remove competitor");
            await vm.RemoveCompetitorCommand.ExecutionTask!;
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Assert.False(vm.HasDeskChangeLog);
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

    private sealed class EntryExchangeStub : IEntryExchange
    {
        public string ClipboardText { get; set; } = string.Empty;
        public string CopiedText { get; private set; } = string.Empty;
        public string SavedText { get; private set; } = string.Empty;
        public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(ClipboardText);
        public Task WriteClipboardAsync(string text) { CopiedText = text; return Task.CompletedTask; }
        public Task<bool> SaveTsvAsync(string suggestedName, string text)
        {
            SavedText = text;
            return Task.FromResult(true);
        }
    }
}

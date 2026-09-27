using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.IO.Compression;
using System.Text;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Domain;
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
    [Fact]
    public async Task CategoryRulePresetCanBeSavedAndLoadedLocally()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-rules-ui", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CategoryRulePresetStore(folder);
            var rules = new[] { new CategoryRuleValues("Women U16", 2010, 2011, Gender.Female, 1) };
            await store.SaveAsync(rules);
            Assert.Equal(rules, await store.LoadAsync());
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
    }

    [Fact]
    public async Task LoadingSavedRulesReplacesSeriesRulesAndUpdatesCompetitorCategories()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-rules-workflow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "series.ost");
            var preset = new CategoryRulePresetStore(folder);
            await preset.SaveAsync([new CategoryRuleValues("Women 2002", 2002, 2002, Gender.Female, 0)]);
            await using (var seed = new SeriesWorkspace(new SqliteSeriesFileStore()))
            {
                var series = await seed.CreateAsync(file, new SeriesValues("Race", "Levi", "Club",
                    new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 25), "FIN", "2026/27"));
                var old = await seed.SaveCategoryRuleAsync(null,
                    new CategoryRuleValues("Old", 2002, 2002, null, 0), series.Revision);
                await seed.SaveDeskRowAsync(null, new CompetitorValues("NORD", "Ada", 2002,
                    "123456", "FIN", "Club", Gender.Female), null, false, null, old.Revision);
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var vm = new MainViewModel(workspace, new FileDialogsStub
            {
                NewPath = file, OpenPath = file, BackupPath = file + ".bak"
            },
                categoryRulePresetStore: preset);
            vm.OpenSeriesCommand.Execute(null);
            await vm.OpenSeriesCommand.ExecutionTask!;
            vm.LoadCategoryRulesPresetCommand.Execute(null);
            await vm.LoadCategoryRulesPresetCommand.ExecutionTask!;
            Assert.Equal("Women 2002", Assert.Single(vm.CategoryRules).Values.Label);
            Assert.Equal("Women 2002", Assert.Single(vm.VisibleCompetitors, x => !x.IsPlaceholder).Category);
            vm.UpdateCategoriesCommand.Execute(null);
            Assert.Contains("1 competitor", vm.StatusMessage);
            vm.SaveCategoryRulesPresetCommand.Execute(null);
            await vm.SaveCategoryRulesPresetCommand.ExecutionTask!;
            Assert.Equal("Women 2002", Assert.Single(await preset.LoadAsync()).Label);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task FisDownloaderUsesEffectiveDateAndCredentialHeader()
    {
        var handler = new FisHttpHandler();
        using var client = new HttpClient(handler);
        var bytes = await new FisPointsDownloader(client).DownloadAsync(new DateOnly(2026, 9, 27), "synthetic-key");
        Assert.Equal([1, 2, 3], bytes);
        Assert.Equal("https://api.fis-ski.com/data-feeds/fis-points-lists/normal/AL/download?effectiveDate=2026-09-27",
            handler.RequestUri);
        Assert.Equal("synthetic-key", handler.Token);
    }

    [Fact]
    public async Task CodeOnlyCompetitorCanBeFilledFromCachedFisList()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-fis-code", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "series.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using (var seed = new SeriesWorkspace(new SqliteSeriesFileStore()))
            {
                var series = await seed.CreateAsync(file, new SeriesValues("Race", "Levi", "Club",
                    new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 25), "FIN", "2026/27"));
                await seed.SaveDeskRowAsync(null, new CompetitorValues("", "", null,
                    "123456", null, null, null), null, false, null, series.Revision);
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var vm = new MainViewModel(workspace, new FileDialogsStub
            {
                NewPath = file, OpenPath = file, BackupPath = file + ".bak"
            }, fisStore: cache);
            vm.OpenSeriesCommand.Execute(null);
            await vm.OpenSeriesCommand.ExecutionTask!;
            vm.StageFisUpdatesCommand.Execute(null);
            Assert.Equal("NORD", Assert.Single(vm.VisibleCompetitors, x => !x.IsPlaceholder).Surname);
            Assert.Contains(vm.DeskChangeLog, x => x.Field == nameof(CompetitorGridRow.Surname));
            vm.CommitDeskChangesCommand.Execute(null);
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            Assert.Equal("NORD", Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors).Values.Surname);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task CachedFisListStagesUpdatesAndNewAthletesOnlyUntilCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-fis-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "series.ost");
            var cache = new FisLocalStore(root);
            await cache.SaveListAsync(SyntheticFisArchive());
            await using (var seed = new SeriesWorkspace(new SqliteSeriesFileStore()))
            {
                var series = await seed.CreateAsync(file, new SeriesValues("Race", "Levi", "Club",
                    new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 25), "FIN", "2026/27"));
                var race = await seed.SaveCompetitionAsync(null, new CompetitionValues("Slalom", "SL",
                    new DateOnly(2026, 9, 26), Discipline.Slalom, RaceType.Club, 2, 0), series.Revision);
                await seed.SaveDeskRowAsync(null, new CompetitorValues("NORD", "Ada", 2002,
                    "123456", "FIN", "Old Club", Gender.Female), null, false, null, race.Revision);
            }
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var dialogs = new FileDialogsStub { NewPath = file, OpenPath = file, BackupPath = file + ".bak" };
            var vm = new MainViewModel(workspace, dialogs, fisStore: cache);
            var window = new MainWindow { DataContext = vm };
            window.WindowState = WindowState.Normal;
            window.Width = 1200;
            window.Height = 800;
            window.Show();
            Click(window, "Open file");
            await vm.OpenSeriesCommand.ExecutionTask!;
            Assert.Equal(file, window.Title);
            Click(window, "3  Competitors");
            window.UpdateLayout();
            var sections = window.GetVisualDescendants().OfType<Expander>()
                .Where(x => Equals(x.Header, "Update from FIS")
                    || x.Header?.ToString()?.StartsWith("Category rules", StringComparison.Ordinal) == true)
                .ToArray();
            var fisSection = sections.Single(x => Equals(x.Header, "Update from FIS"));
            var categorySection = sections.Single(x => !Equals(x.Header, "Update from FIS"));
            var fisPosition = fisSection.TranslatePoint(new Point(0, 0), window)!.Value;
            var categoryPosition = categorySection.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.True(fisPosition.Y < categoryPosition.Y);
            Assert.True(categoryPosition.Y < window.Bounds.Height - 35);
            var grid = window.FindControl<DataGrid>("CompetitorGrid")!;
            var menu = grid.ContextMenu!;
            Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.InputGesture));
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Select all"));
            Assert.True(grid.CanUserSortColumns);
            Assert.All(grid.Columns.Skip(1), column => Assert.False(string.IsNullOrWhiteSpace(column.SortMemberPath)));
            Assert.Equal(5, grid.Columns.Count(column => column.SortMemberPath?.StartsWith("Fis", StringComparison.Ordinal) == true));
            Assert.Equal(12.34m, Assert.Single(vm.VisibleCompetitors, x => !x.IsPlaceholder).FisSl);
            fisSection.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsFisPanelOpen);
            Assert.Equal("1327: 13th FIS points list 2026/27 (22-09-2026)", vm.FisListDisplay);
            Assert.Equal("2026-09-26", vm.FisEffectiveDateText);
            var fisDateButton = window.FindControl<Button>("FisDatePickerButton")!;
            fisDateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var fisDatePopup = Assert.IsType<StackPanel>(Assert.IsType<Flyout>(fisDateButton.Flyout).Content);
            Assert.IsType<Avalonia.Controls.Calendar>(fisDatePopup.Children[1]).SelectedDate = new DateTime(2026, 10, 2);
            Assert.Equal("2026-10-02", window.FindControl<TextBox>("FisEffectiveDateInput")!.Text);
            Click(window, "Stage updates for existing competitors");
            Assert.Contains(vm.DeskChangeLog, x => x.Field == nameof(CompetitorGridRow.Club));
            Assert.Equal("Old Club", Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors).Values.Club);
            vm.FisSearchText = "654321";
            Click(window, "×");
            Assert.Empty(vm.FisSearchText);
            vm.FisSearchText = "FIN";
            var fisResults = window.FindControl<ListBox>("FisSearchResultsList")!;
            foreach (var result in vm.FisSearchResults.Where(x => x.Athlete.Code != "123456"))
            {
                fisResults.SelectedItems!.Add(result);
            }
            Assert.Equal(2, fisResults.SelectedItems!.Count);
            Click(window, "Add selected competitors");
            Assert.Equal(3, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));
            grid.Focus();
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, grid.SelectedItems.OfType<CompetitorGridRow>().Count(x => !x.IsPlaceholder));
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            var saved = (await workspace.ReadCompetitorDeskAsync()).Competitors;
            Assert.Equal(3, saved.Count);
            Assert.Equal("North Club", saved.Single(x => x.Values.FederationCode == "123456").Values.Club);
            Assert.Equal("WEST", saved.Single(x => x.Values.FederationCode == "654321").Values.Surname);
            Assert.Equal("SOUTH", saved.Single(x => x.Values.FederationCode == "777777").Values.Surname);
            window.FindControl<ScrollViewer>("WorkspaceScroll")!.Offset = Vector.Zero;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var codeHeader = window.GetVisualDescendants().OfType<DataGridColumnHeader>()
                .Single(x => Equals(x.Content, "CODE"));
            var headerPoint = codeHeader.TranslatePoint(
                new Point(codeHeader.Bounds.Width / 2, codeHeader.Bounds.Height / 2), window)!.Value;
            Assert.True(codeHeader.IsVisible && codeHeader.Bounds.Width > 0 && codeHeader.Bounds.Height > 0,
                $"Header not visible: window={window.Bounds}, header={codeHeader.Bounds}, point={headerPoint}");
            window.MouseDown(headerPoint, MouseButton.Left);
            window.MouseUp(headerPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("↑", codeHeader.Content?.ToString());
            window.MouseDown(headerPoint, MouseButton.Left);
            window.MouseUp(headerPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("↓", codeHeader.Content?.ToString());
            Assert.Equal("777777", vm.VisibleCompetitors[0].FederationCode);
            Assert.True(vm.VisibleCompetitors[^1].IsPlaceholder);
            grid.Focus();
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.Control);
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, vm.DeskChangeLog.Count(x => x.Kind == DeskChangeKind.Delete));
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            Assert.Empty((await workspace.ReadCompetitorDeskAsync()).Competitors);
            window.Close();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static byte[] SyntheticFisArchive()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "AL1327hdr.csv", "Listid\tSeasoncode\tListnumber\tListname\tCalculationdate\tStartracedate\tEndracedate\tValidfrom\tValidto\tLastupdate\n"
                + "465\t2027\t13\t13th FIS points list 2026/27\t2026-09-22\t2026-07-01\t2026-09-20\t2026-09-24\t2026-09-30\t2026-09-22 04:19:38\n");
            Write(zip, "AL1327com.csv", "Competitorid\tSectorcode\tFiscode\tLastname\tFirstname\tGender\tBirthdate\tNationcode\tNationalcode\tSkiclub\tAssociation\tStatus\n"
                + "1\tAL\t123456\tNORD\tAda\tW\t2002-02-03\tFIN\t\tNorth Club\t\tA\n"
                + "2\tAL\t654321\tWEST\tEli\tM\t2001-01-02\tFIN\t\tWest Club\t\tA\n"
                + "3\tAL\t777777\tSOUTH\tSam\tM\t2000-01-02\tFIN\t\tSouth Club\t\tA\n");
            Write(zip, "AL1327pts.csv", "Recid\tListid\tCompetitorid\tDisciplinecode\tFispoints\tPosition\tPenalty\tLastupdate\n"
                + "7\t465\t1\tSL\t12.34\t2\t\t2026-09-22\n"
                + "8\t465\t2\tGS\t24.68\t3\t\t2026-09-22\n");
        }
        return output.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string contents)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    private sealed class FisHttpHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Token { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Token = request.Headers.GetValues("X-CSRF-TOKEN").Single();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            });
        }
    }

    [AvaloniaFact]
    public async Task DesktopStagesGridAndPasteChangesTogetherAndPersistsOnlyOnCommit()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-grid-ui", Guid.NewGuid().ToString("N"));
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

            Click(window, "1  Event series");
            vm.StartDateText = "31.02.2026";
            Click(window, "Save series");
            await vm.SaveSeriesCommand.ExecutionTask!;
            Assert.True(vm.IsError);
            vm.StartDateText = "6.5.2026";
            Click(window, "Save series");
            await vm.SaveSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
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
            Click(window, "Add competition");
            vm.CompetitionName = "Giant slalom";
            vm.CompetitionShortLabel = "3.2 GS";
            Click(window, "Save competition");
            await vm.SaveCompetitionCommand.ExecutionTask!;
            Assert.Equal(2, vm.Competitions.Count);

            Click(window, "3  Competitors");
            var grid = window.FindControl<DataGrid>("CompetitorGrid")!;
            Assert.Contains(grid.Columns, column => Equals(column.Header, "3.1 SL"));
            Assert.Contains(grid.Columns, column => Equals(column.Header, "3.2 GS"));
            Assert.Equal("CODE", grid.Columns[1].Header);
            Assert.Equal("SURNAME", grid.Columns[2].Header);
            Assert.DoesNotContain(grid.Columns, column => Equals(column.Header, "BIB REF") || Equals(column.Header, "STATUS"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button =>
                Equals(button.Content, "Save row") || Equals(button.Content, "Add competitor")
                || Equals(button.Content, "Export selection TSV") || Equals(button.Content, "Undo saved"));
            Assert.Single(vm.VisibleCompetitors);
            var row = vm.VisibleCompetitors.Single();
            Assert.True(row.IsPlaceholder);
            grid.SelectedIndex = 0;
            grid.CurrentColumn = grid.Columns[1];
            grid.Focus();
            window.KeyTextInput("F");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("F", row.FederationCode);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyTextInput("M");
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("a");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Ma", row.Surname);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyTextInput("A");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("A", row.FirstName);
            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            window.KeyTextInput("2");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2", row.BirthYearText);
            row.FederationCode = "FIN123";
            row.Surname = "Makela";
            row.FirstName = "Aino";
            row.BirthYearText = "2010";
            row.GenderText = "Women";
            row.Nation = "FIN";
            var sl = row.GridEntries.Single(x => x.Label == "3.1 SL");
            sl.IsParticipating = true;
            Assert.True(row.IsSurnameChanged);
            Assert.True(sl.IsChanged);
            Assert.Equal(2, vm.VisibleCompetitors.Count);
            Assert.True(vm.VisibleCompetitors.Last().IsPlaceholder);
            Assert.Single(vm.DeskChangeLog);
            Assert.Equal(DeskChangeKind.Add, vm.DeskChangeLog.Single().Kind);
            Assert.Empty((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Assert.Empty(vm.DeskChangeLog);
            row = vm.VisibleCompetitors.Single(x => !x.IsPlaceholder);
            Assert.False(row.IsSurnameChanged);
            Assert.False(row.GridEntries.Single(x => x.Label == "3.1 SL").IsChanged);
            Assert.Equal("Women", row.GenderText);
            Assert.Equal(["Women", "Men"], row.AvailableGenders);
            grid.SelectedItem = row;
            grid.CurrentColumn = grid.Columns[5];
            Assert.True(grid.BeginEdit());
            window.UpdateLayout();
            var genderCombo = grid.GetVisualDescendants().OfType<ComboBox>()
                .Single(x => ReferenceEquals(x.ItemsSource, row.AvailableGenders));
            genderCombo.SelectedItem = "Men";
            Assert.Equal("Men", row.GenderText);
            genderCombo.SelectedItem = "Women";
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            Assert.False(row.IsGenderChanged);
            vm.CategoryLabel = "Women U16";
            vm.CategoryMinYearText = "2010";
            vm.CategoryMaxYearText = "2011";
            vm.CategoryGenderText = "Women";
            window.GetVisualDescendants().OfType<Expander>()
                .Single(x => x.Header?.ToString()?.StartsWith("Category rules", StringComparison.Ordinal) == true).IsExpanded = true;
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(),
                x => ReferenceEquals(x.ItemsSource, vm.CategoryGenderOptions));
            Click(window, "Save rule");
            await vm.SaveCategoryRuleCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal("Women U16", vm.VisibleCompetitors.Single(x => x.Surname == "MAKELA").Category);
            Assert.Equal(["Any", "Women", "Men"], vm.CategoryGenderOptions);
            row = vm.VisibleCompetitors.Single(x => x.Surname == "MAKELA");
            grid.SelectedItem = row;
            grid.CurrentColumn = grid.Columns[7];
            grid.Focus();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyTextInput("X");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("X", row.Club);
            row.Club = "Manual edit";
            var clubChange = vm.DeskChangeLog.Single(x => x.Field == nameof(CompetitorGridRow.Club));
            Assert.Contains("FIN123 MAKELA", clubChange.DisplayLabel);
            Assert.Contains("Club: — → Manual edit", clubChange.DisplayLabel);
            vm.RestoreDeskChangeCommand.Execute(clubChange);
            Assert.Empty(vm.DeskChangeLog);
            Assert.Equal(string.Empty, row.Club);
            Assert.False(row.IsClubChanged);

            row.Club = "Manual edit";
            exchange.ClipboardText = "Surname\tFirst name\tYear\tGender\tClub\t3.1 SL\t3.2 GS\n"
                + "Makela\tAino\t2010\tWomen\tPaste club\tX\tX\n"
                + "Laine\tLea\t2011\tMen\tNew club\tX\t\n";
            vm.PasteFromExcelCommand.Execute(null);
            await vm.PasteFromExcelCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(3, vm.VisibleCompetitors.Count); // two competitors and the perpetual blank row
            Assert.True(row.IsClubChanged);
            Assert.True(row.GridEntries.Single(x => x.Label == "3.2 GS").IsChanged);
            Assert.Contains(vm.DeskChangeLog, x => x.Kind == DeskChangeKind.Add);
            Assert.Contains(vm.DeskChangeLog, x => x.Kind == DeskChangeKind.Entry);
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            var gsEntry = row.GridEntries.Single(x => x.Label == "3.2 GS");
            vm.RestoreDeskChangeCommand.Execute(vm.DeskChangeLog.Single(x =>
                x.Kind == DeskChangeKind.Entry && x.CompetitionId == gsEntry.CompetitionId));
            Assert.False(gsEntry.IsChanged);
            gsEntry.IsParticipating = true;
            clubChange = vm.DeskChangeLog.Single(x => x.Field == nameof(CompetitorGridRow.Club));
            vm.RestoreDeskChangeCommand.Execute(clubChange);
            Assert.False(row.IsClubChanged);
            Assert.True(row.GridEntries.Single(x => x.Label == "3.2 GS").IsChanged);
            row.Club = "Reviewed club";
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            var desk = await workspace.ReadCompetitorDeskAsync();
            Assert.Equal(2, desk.Competitors.Count);
            Assert.Equal("Reviewed club", desk.Competitors.Single(x => x.Values.Surname == "MAKELA").Values.Club);
            Assert.Equal(Gender.Male, desk.Competitors.Single(x => x.Values.Surname == "LAINE").Values.Gender);
            Assert.Empty(vm.DeskChangeLog);
            Assert.All(vm.VisibleCompetitors.Where(x => !x.IsPlaceholder), x => Assert.False(x.HasPendingChanges));

            var selectedRows = vm.VisibleCompetitors.Where(x => !x.IsPlaceholder).ToArray();
            grid.SelectedItems.Clear();
            foreach (var selected in selectedRows) { grid.SelectedItems.Add(selected); }
            var selectedSl = selectedRows[0].GridEntries.Single(x => x.Label == "3.1 SL");
            window.FindControl<ScrollViewer>("WorkspaceScroll")!.Offset = Vector.Zero;
            window.UpdateLayout();
            var selectedCheck = grid.GetVisualDescendants().OfType<CheckBox>().First(x =>
                ReferenceEquals(x.DataContext, selectedRows[0]) && x.Classes.Contains("entryCell"));
            var checkPoint = selectedCheck.TranslatePoint(new Point(selectedCheck.Bounds.Width / 2,
                selectedCheck.Bounds.Height / 2), window)!.Value;
            window.MouseDown(checkPoint, MouseButton.Left);
            window.MouseUp(checkPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.All(selectedRows, x => Assert.False(x.GridEntries.Single(y => y.Label == "3.1 SL").IsParticipating));
            Assert.Equal(2, vm.DeskChangeLog.Count(x => x.Kind == DeskChangeKind.Entry));
            Assert.All(vm.DeskChangeLog, x => Assert.Contains(" → ", x.DisplayLabel));
            selectedSl.IsParticipating = true;
            await vm.StageGridEntryAsync(selectedRows[0], selectedSl, selectedRows);
            Assert.Empty(vm.DeskChangeLog);

            exchange.ClipboardText = "Code\t3.1 SL\nFIN123\t";
            vm.PasteFromExcelCommand.Execute(null);
            await vm.PasteFromExcelCommand.ExecutionTask!;
            Assert.False(vm.VisibleCompetitors.Single(x => x.FederationCode == "FIN123")
                .GridEntries.Single(x => x.Label == "3.1 SL").IsParticipating);
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            var pastedRemoval = await workspace.ReadCompetitorDeskAsync();
            var removedEntryCompetitor = pastedRemoval.Competitors.Single(x => x.Values.FederationCode == "FIN123");
            Assert.False(pastedRemoval.Participations.Single(x => x.CompetitorId == removedEntryCompetitor.Id
                && x.CompetitionId == vm.Competitions.Single(y => y.Values.ShortLabel == "3.1 SL").Id).Participates);

            exchange.ClipboardText = "Surname\nUnknown";
            vm.PasteFromExcelCommand.Execute(null);
            await vm.PasteFromExcelCommand.ExecutionTask!;
            var uncertain = vm.VisibleCompetitors.Single(x => x.Surname == "UNKNOWN");
            Assert.True(uncertain.NeedsApproval);
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            Assert.True(vm.IsError);
            Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
            vm.RestoreDeskChangeCommand.Execute(vm.DeskChangeLog.Single(x => x.LocalRowId == uncertain.LocalId));
            Assert.Empty(vm.DeskChangeLog);
            Assert.Empty(vm.ImportWarnings);

            vm.SelectedCompetitorRow = vm.VisibleCompetitors.Single(x => x.Surname == "LAINE");
            vm.CopySelectedCompetitorCommand.Execute(null);
            await vm.CopySelectedCompetitorCommand.ExecutionTask!;
            Assert.DoesNotContain("Bib", exchange.CopiedText);
            var copied = TsvExchange.Parse(exchange.CopiedText);
            Assert.Equal(2, copied.Count);
            Assert.Equal("Code", copied[0][0]);
            Assert.Equal("Surname", copied[0][1]);
            grid.Focus();
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Assert.True(vm.SelectedCompetitorRow!.IsPendingDelete);
            Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
            vm.RestoreDeskChangeCommand.Execute(vm.DeskChangeLog.Single(x => x.Kind == DeskChangeKind.Delete));
            Assert.False(vm.SelectedCompetitorRow.IsPendingDelete);

            Click(window, "Backup / transfer");
            await vm.BackupCommand.ExecutionTask!;
            Click(window, "Close file");
            await vm.CloseSeriesCommand.ExecutionTask!;
            Click(window, "Open file");
            await vm.OpenSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(backup, vm.FileLabel);
            Assert.Equal(2, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));
            Click(window, "1  Event series");
            var startDateInput = window.FindControl<TextBox>("SeriesStartDateInput")!;
            startDateInput.Text = "07.05.2026";
            var pickerButton = window.FindControl<Button>("SeriesStartDatePickerButton")!;
            pickerButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var pickerContent = Assert.IsType<StackPanel>(Assert.IsType<Flyout>(pickerButton.Flyout).Content);
            var picker = Assert.IsType<Avalonia.Controls.Calendar>(pickerContent.Children[1]);
            Assert.Contains("05", Assert.IsType<TextBlock>(pickerContent.Children[0]).Text);
            picker.SelectedDate = new DateTime(2026, 5, 6);
            Assert.Equal("06.05.2026", startDateInput.Text);

            Click(window, "3  Competitors");
            grid.SelectedItem = vm.VisibleCompetitors.Single(x => x.Surname == "MAKELA");
            grid.CurrentColumn = grid.Columns[2];
            grid.SelectedItem = vm.VisibleCompetitors.Single(x => x.Surname == "LAINE");
            grid.CurrentColumn = grid.Columns[4];
            grid.SelectedItem = vm.VisibleCompetitors.Single(x => x.IsPlaceholder);
            grid.CurrentColumn = grid.Columns[7];
            window.UpdateLayout();
            var selectedColor = Color.Parse("#D8ECE9");
            var renderedCells = grid.GetVisualDescendants().OfType<DataGridCell>().ToArray();
            Assert.NotEmpty(renderedCells);
            Assert.DoesNotContain(renderedCells, cell =>
                cell.Background is ISolidColorBrush brush && brush.Color == selectedColor);
            vm.SelectedCompetitorRow = vm.VisibleCompetitors.Single(x => x.Surname == "LAINE");
            vm.RemoveCompetitorCommand.Execute(null);
            Click(window, "Commit Changes");
            await vm.CommitDeskChangesCommand.ExecutionTask!;
            Assert.Single((await workspace.ReadCompetitorDeskAsync()).Competitors);
            Assert.Empty(vm.DeskChangeLog);
            window.Close();
        }
        finally { Directory.Delete(root, recursive: true); }
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
            Assert.True(children[1].Bounds.Top >= children[0].Bounds.Bottom + 4);
            Assert.True(children[1].Bounds.Width >= 100);
        }
        foreach (var grid in window.GetVisualDescendants().OfType<Grid>())
        {
            var fields = grid.Children.OfType<StackPanel>()
                .Where(panel => panel.IsVisible && panel.Classes.Contains("field"))
                .OrderBy(panel => panel.Bounds.Left).ToArray();
            for (var i = 1; i < fields.Length; i++)
            {
                Assert.True(fields[i - 1].Bounds.Right + 8 <= fields[i].Bounds.Left);
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
        public Task<bool> ConfirmRemoveAsync(string competitionName) => Task.FromResult(true);
    }

    private sealed class EntryExchangeStub : IEntryExchange
    {
        public string ClipboardText { get; set; } = string.Empty;
        public string CopiedText { get; private set; } = string.Empty;
        public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(ClipboardText);
        public Task WriteClipboardAsync(string text) { CopiedText = text; return Task.CompletedTask; }
    }
}

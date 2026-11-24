using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

// Settings > About > Advanced > Update from Ski-Alp-Pro: advanced mode only, statistics first, replacement only after confirmation.
public sealed class SkiAlpProImportWorkflowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-ski-alp-pro-ui", Guid.NewGuid().ToString("N"));
    public SkiAlpProImportWorkflowTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Dialogs : IFileDialogs
    {
        public string? SeriesPath { get; set; }
        public string? SkiAlpProPath { get; set; }
        public int Asked { get; private set; }
        public Task<string?> ChooseNewAsync(string suggestedName) => Task.FromResult(SeriesPath);
        public Task<string?> ChooseOpenAsync() => Task.FromResult(SeriesPath);
        public Task<string?> ChooseBackupAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmRemoveAsync(string competitionName) => Task.FromResult(false);
        public Task<bool> ConfirmDiscardChangesAsync(int changeCount) => Task.FromResult(false);
        public Task<string?> ChooseStartListExportAsync(string suggestedName, bool print) => Task.FromResult<string?>(null);
        public Task<string?> ChooseResultXmlExportAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> ChoosePdfExportAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> ChooseSkiAlpProFileAsync() { Asked++; return Task.FromResult(SkiAlpProPath); }
    }

    private async Task<(SeriesWorkspace Workspace, MainViewModel ViewModel, Dialogs Dialogs)> OpenAsync()
    {
        var path = Path.Combine(_root, "series.ost");
        var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new Devices.AlgeDecoderFactory());
        var date = new DateOnly(2026, 3, 21);
        await workspace.CreateAsync(path, new("Synthetic", "Test slope", "Test club", date, date, "FIN", "2025/26"));
        var dialogs = new Dialogs { SeriesPath = path, SkiAlpProPath = SkiAlpProTestEvent.TwoRuns().Write(Path.Combine(_root, "source")) };
        var vm = new MainViewModel(workspace, dialogs, recentSeriesStore: new(_root), uiPreferencesStore: new(_root), reportDefaultsStore: new(_root),
            timingDeviceCache: new(_root), fisStore: new FisLocalStore(_root));
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        return (workspace, vm, dialogs);
    }

    [AvaloniaFact]
    public async Task TheToolNeedsAdvancedModeAndAnOpenSeries()
    {
        var (workspace, vm, dialogs) = await OpenAsync();
        await using var owned = workspace;
        using var disposable = vm;
        Assert.False(vm.CanUpdateFromSkiAlpPro);
        Assert.Null(await vm.OpenSkiAlpProAsync());
        Assert.True(vm.IsError);
        Assert.Equal(0, dialogs.Asked);
        vm.AdvancedMode = true;
        Assert.True(vm.CanUpdateFromSkiAlpPro);
        await vm.CloseSeriesCommand.ExecuteAsync(null);
        Assert.False(vm.CanUpdateFromSkiAlpPro);
        Assert.Null(await vm.OpenSkiAlpProAsync());
        Assert.Equal(0, dialogs.Asked);
    }

    [AvaloniaFact]
    public async Task SettingsShowsTheTestOnlyButtonInAdvancedModeAndTheDialogFitsItsWindow()
    {
        var (workspace, vm, _) = await OpenAsync();
        await using var owned = workspace;
        using var disposable = vm;
        var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800, WindowState = WindowState.Normal };
        window.Show();
        try
        {
            vm.ShowSettingsCommand.Execute(null);
            vm.SelectedSettingsTab = 4;
            void Layout() { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var button = settings.FindControl<Button>("UpdateFromSkiAlpProButton")!;
            Assert.False(button.IsEffectivelyVisible);
            vm.AdvancedMode = true;
            Layout();
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(button.IsEffectivelyEnabled);
            Assert.Equal("Update from Ski-Alp-Pro", button.Content);
            Assert.Contains("Test use only", settings.FindControl<TextBlock>("UpdateFromSkiAlpProNote")!.Text, StringComparison.Ordinal);
            button.BringIntoView(); Layout();
            Save(window, "ski-alp-pro-settings");

            var import = (await vm.OpenSkiAlpProAsync())!;
            var dialog = new SkiAlpProImportDialog { DataContext = import };
            var closed = dialog.ShowDialog(window);
            dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
            Save(dialog, "ski-alp-pro-dialog");
            Assert.Contains("test use only", dialog.Title, StringComparison.OrdinalIgnoreCase);
            foreach (var name in new[] { "SkiAlpProScopeBox", "SkiAlpProTargetBox", "SkiAlpProCodexBox", "SkiAlpProImportButton", "SkiAlpProStatisticsGrid" })
            {
                var control = dialog.FindControl<Control>(name)!;
                Assert.True(control.IsEffectivelyVisible, name);
                var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), dialog)!.Value.X;
                Assert.True(control.Bounds.Width > 0 && right <= dialog.Bounds.Width, name);
            }
            await import.ImportCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            await closed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(import.IsDone);
        }
        finally { window.Close(); }
    }

    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OPENSKITIME_UI_CAPTURE") is not { Length: > 0 } folder) { return; }
        Directory.CreateDirectory(folder);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public async Task AnUnknownCodexAsksForTheTargetAndDefaultsToItself()
    {
        var (workspace, vm, _) = await OpenAsync();
        await using var owned = workspace;
        using var disposable = vm;
        vm.AdvancedMode = true;
        var import = (await vm.OpenSkiAlpProAsync())!;
        Assert.False(import.CodexFound);
        Assert.True(import.HasTargetNotice);
        Assert.True(import.IsNewTarget);
        Assert.Equal("0592", import.NewCodex);
        Assert.Equal([(6, 4, 1, 1, 0), (4, 3, 0, 0, 1)], import.Statistics.Select(x => (x.Starters, x.Finished, x.Dns, x.Dnf, x.Dsq)));
        Assert.Equal(SkiAlpProScope.Run2Times, import.Scope!.Scope);
        Assert.False(import.NeedsConfirmation);
        await import.ImportCommand.ExecuteAsync(null);
        Assert.True(import.IsDone);
        Assert.False(import.IsError);
        Assert.Contains("Test import saved", vm.StatusMessage, StringComparison.Ordinal);
        var competition = Assert.Single(vm.Competitions);
        Assert.Equal("0592", competition.Values.FisCode);
        Assert.Equal(competition.Id, vm.DeskCompetition?.Id);
    }

    [AvaloniaFact]
    public async Task AKnownCodexIsTheTargetAndReplacingItNeedsConfirmation()
    {
        var (workspace, vm, dialogs) = await OpenAsync();
        await using var owned = workspace;
        using var disposable = vm;
        vm.AdvancedMode = true;
        await (await vm.OpenSkiAlpProAsync())!.ImportCommand.ExecuteAsync(null);
        var target = vm.Competitions.Single();
        // The replaced timing is open in the timing view.
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(target, 1));

        var import = (await vm.OpenSkiAlpProAsync())!;
        Assert.True(import.CodexFound);
        Assert.Equal(target.Id, import.Target!.Competition!.Id);
        Assert.True(import.NeedsConfirmation);
        Assert.Contains("6 entries, 2 start lists", import.ReplacementText, StringComparison.Ordinal);
        Assert.False(import.ImportCommand.CanExecute(null));
        import.IsConfirmed = true;
        Assert.True(import.ImportCommand.CanExecute(null));
        // Another target always needs its own confirmation.
        import.Target = import.Targets[^1];
        Assert.False(import.NeedsConfirmation);
        import.Target = import.Targets[0];
        await WaitAsync(() => import.NeedsConfirmation);
        Assert.False(import.IsConfirmed);
        Assert.False(import.ImportCommand.CanExecute(null));
        import.IsConfirmed = true;
        import.Scope = import.Scopes.Single(x => x.Scope == SkiAlpProScope.Run1Starters);
        await import.ImportCommand.ExecuteAsync(null);
        Assert.True(import.IsDone, import.Status);
        Assert.Single(Directory.GetFiles(_root, "series.before-import-*.ost"));
        Assert.Single((await workspace.ReadStartListsAsync(target.Id)).Revisions);
        // The timing view works on the new list, not on the replaced one.
        await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(vm.Competitions.Single(), 1));
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Equal(6, vm.TimingRows.Count);

        // A file that cannot be read shows why and opens nothing.
        dialogs.SkiAlpProPath = Path.Combine(_root, "missing.scdb");
        Assert.Null(await vm.OpenSkiAlpProAsync());
        Assert.True(vm.IsError);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.True(condition());
    }
}

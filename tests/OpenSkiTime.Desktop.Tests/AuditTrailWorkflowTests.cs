using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(980, 680)]
    [InlineData(1280, 800)]
    public async Task AdvancedModeIsLocalPersistentAndAddsReachableNavigationAfterPdfFactory(int width, int height)
    {
        var root = AuditFolder();
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var preferences = new UiPreferencesStore(root);
            using var vm = new MainViewModel(workspace, new AuditFileDialogs(), recentSeriesStore: new(root), uiPreferencesStore: preferences);
            var window = new MainWindow { DataContext = vm, Width = width, Height = height, WindowState = WindowState.Normal };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                var button = window.FindControl<Button>("AuditTrailButton")!;
                Assert.False(button.IsEffectivelyVisible);
                vm.SelectedSettingsTab = 4; vm.ShowSettingsCommand.Execute(null);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var check = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AdvancedModeCheckBox");
                check.IsChecked = true;
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.True(vm.AdvancedMode); Assert.True(preferences.Load());
                Assert.True(button.IsEffectivelyVisible); Assert.True(button.IsEffectivelyEnabled);
                var nav = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("sectionNav")).ToArray();
                Assert.Equal(Array.FindIndex(nav, b => b.Name == "PdfFactoryButton") + 1, Array.IndexOf(nav, button));
                foreach (var item in nav.Where(b => b.IsEffectivelyVisible)) { WithinWindowWidth(window, item); }
                await vm.ShowAuditTrailCommand.ExecuteAsync(null);
                await vm.RefreshAuditCommand.ExecuteAsync(null);
                Assert.True(vm.IsAuditTrailSection);
                Assert.Contains("Open an event series", vm.AuditStatus, StringComparison.Ordinal);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                CaptureSettingsWindow(window, width, height, "audit-trail");
                vm.AdvancedMode = false;
                Assert.True(vm.IsSettingsSection); Assert.Equal(4, vm.SelectedSettingsTab);
                Assert.False(preferences.Load());
            }
            finally { window.Close(); }
            preferences.Save(true);
            using var restarted = new MainViewModel(workspace, new AuditFileDialogs(), recentSeriesStore: new(root), uiPreferencesStore: preferences);
            restarted.LoadUiPreferences(); Assert.True(restarted.AdvancedMode);
            Assert.False(workspace.IsOpen);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task UnwritablePreferenceKeepsPriorModeAndReportsFailure()
    {
        var root = AuditFolder();
        try
        {
            var obstruction = Path.Combine(root, "file"); await File.WriteAllTextAsync(obstruction, "synthetic");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new AuditFileDialogs(), recentSeriesStore: new(root), uiPreferencesStore: new(obstruction));
            vm.AdvancedMode = true;
            Assert.False(vm.AdvancedMode); Assert.True(vm.IsError);
            Assert.Contains("could not be saved", vm.StatusMessage, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task SwitchingSeriesInvalidatesHistoryAndBundlePreview()
    {
        var root = AuditFolder();
        try
        {
            var path = Path.Combine(root, "first.ost"); var other = Path.Combine(root, "other.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(path, new("Synthetic", "Test", "Test", new(2026, 10, 3), new(2026, 10, 3), "FIN", "2026/27"));
            var dialogs = new AuditFileDialogs { OpenPath = path };
            using var vm = new MainViewModel(workspace, dialogs, recentSeriesStore: new(root), uiPreferencesStore: new(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null); vm.AdvancedMode = true;
            await vm.ShowAuditTrailCommand.ExecuteAsync(null);
            vm.SelectedAuditTab = 3; await vm.RefreshAuditCommand.ExecuteAsync(null);
            Assert.Contains(vm.AuditRecords, r => r.Reference.Source == "Series");
            vm.ReviewDebugBundleCommand.Execute(null);
            await workspace.CreateAsync(other, new("Other", "Test", "Test", new(2026, 10, 3), new(2026, 10, 3), "FIN", "2026/27"));
            dialogs.OpenPath = other; await vm.OpenSeriesCommand.ExecuteAsync(null);
            Assert.Empty(vm.AuditRecords); Assert.Empty(vm.AuditLinks);
            await vm.SaveDebugBundleCommand.ExecuteAsync(null);
            Assert.Contains("series changed", vm.DebugBundleStatus, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DefaultBundleExcludesPrivatePayloadsAndVerifiesManifestHashes()
    {
        var root = AuditFolder();
        try
        {
            var path = Path.Combine(root, "PRIVATE-SERIES.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(path, new("PRIVATE-SERIES", "PRIVATE-PLACE", "PRIVATE-ORGANIZER", new(2026, 10, 3), new(2026, 10, 3), "FIN", "2026/27"));
            var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
            var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
            var stamp = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(Path.Combine(logs, "publisher-123.log"), string.Join('\n',
                stamp + " Publisher process started", stamp + " FIS wire trace enabled: log contains racer identities",
                stamp + " FIS TCP TX <xml passwd=\"PRIVATE-SECRET\">PRIVATE-PERSON</xml>",
                stamp + " Unknown failure PRIVATE-SECRET C:\\Users\\PRIVATE-PERSON", stamp + " Authoritative state published"));
            await File.WriteAllTextAsync(Path.Combine(logs, "other.log"), "PRIVATE-SECRET");
            var exporter = new DebugBundleExporter(new SqliteAuditTrailReader(), logs);
            var destination = Path.Combine(root, "bundle.zip");
            var result = await exporter.ExportAsync(destination, new(path, false, now.AddHours(-1), now.AddHours(1)));
            Assert.True(result.Bytes > 0);
            using var zip = ZipFile.OpenRead(destination);
            Assert.Null(zip.GetEntry("series/event-series.ost"));
            foreach (var entry in zip.Entries)
            {
                using var input = new StreamReader(entry.Open()); var contents = await input.ReadToEndAsync();
                foreach (var privateValue in new[] { "PRIVATE-SERIES", "PRIVATE-PLACE", "PRIVATE-ORGANIZER", "PRIVATE-SECRET", "PRIVATE-PERSON" })
                { Assert.DoesNotContain(privateValue, contents, StringComparison.Ordinal); }
            }
            using var manifestInput = zip.GetEntry("manifest.json")!.Open();
            using var manifest = await JsonDocument.ParseAsync(manifestInput);
            foreach (var entry in manifest.RootElement.GetProperty("Entries").EnumerateArray())
            {
                using var input = zip.GetEntry(entry.GetProperty("Name").GetString()!)!.Open();
                Assert.Equal(entry.GetProperty("Sha256").GetString(), Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(input)));
            }
            Assert.Contains(result.Notices, n => n.Contains("Unsupported", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CompleteBundleUsesVerifiedBackupAndCancelledExportPreservesExistingDestination()
    {
        var root = AuditFolder();
        try
        {
            var path = Path.Combine(root, "series.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var series = await workspace.CreateAsync(path, new("Synthetic saved series", "Test", "Test", new(2026, 10, 3), new(2026, 10, 3), "FIN", "2026/27"));
            var now = DateTimeOffset.UtcNow;
            var exporter = new DebugBundleExporter(new SqliteAuditTrailReader(), Path.Combine(root, "no-logs"));
            var destination = Path.Combine(root, "bundle.zip");
            await exporter.ExportAsync(destination, new(path, true, now.AddHours(-1), now, "Synthetic issue"));
            var restored = Path.Combine(root, "restored.ost");
            using (var zip = ZipFile.OpenRead(destination)) { zip.GetEntry("series/event-series.ost")!.ExtractToFile(restored); }
            await using var reopened = await new SqliteSeriesFileStore().OpenAsync(restored);
            Assert.Equal(series.Id, (await reopened.ReadAsync()).Id);
            var before = await File.ReadAllBytesAsync(destination);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(destination, new(path, true, now.AddHours(-1), now), cancel.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(root, ".debug-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string AuditFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "ost-audit-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }

    [Fact]
    public async Task LockedPublicationPreservesPriorZipAndWrongSeriesIdentityCannotBeAttached()
    {
        var root = AuditFolder();
        try
        {
            var source = Path.Combine(root, "source.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(source, new("Synthetic", "Test", "Test", new(2026, 10, 3), new(2026, 10, 3), "FIN", "2026/27"));
            var destination = Path.Combine(root, "bundle.zip");
            byte[] original = [1, 2, 3, 4]; await File.WriteAllBytesAsync(destination, original);
            var now = DateTimeOffset.UtcNow;
            var exporter = new DebugBundleExporter(new SqliteAuditTrailReader(), Path.Combine(root, "missing"));
            var options = new DebugBundleOptions(source, false, now.AddHours(-1), now);
            using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = await Record.ExceptionAsync(() => exporter.ExportAsync(destination, options));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(destination, options with { IncludeSeries = true, ExpectedSeriesId = Guid.NewGuid() }));
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(root, ".debug-*"));
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task ReviewAndSaveCreatesActualZipWithoutSeriesAndDescriptionIsOptIn()
    {
        var root = AuditFolder();
        try
        {
            var destination = Path.Combine(root, "bundle.zip");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new AuditFileDialogs { ExportPath = destination },
                recentSeriesStore: new(root), uiPreferencesStore: new(root),
                debugBundleExporter: new(new SqliteAuditTrailReader(), Path.Combine(root, "missing")));
            vm.ReviewDebugBundleCommand.Execute(null);
            vm.TimingStartRole.AlgeUsername = "PRIVATE-ACCOUNT";
            vm.TimingStartRole.ReplayPath = "C:\\PRIVATE-PATH";
            vm.TimingStartRole.Firmware = "PRIVATE-FIRMWARE";
            vm.DebugIssueDescription = "Synthetic unselected text";
            await vm.SaveDebugBundleCommand.ExecuteAsync(null);
            Assert.False(vm.IsDebugBundleBusy); Assert.True(vm.HasSavedDebugBundle);
            Assert.Contains("Saved", vm.DebugBundleStatus, StringComparison.Ordinal);
            using (var zip = ZipFile.OpenRead(destination))
            {
                Assert.Null(zip.GetEntry("attachments/issue-description.txt"));
                using var configuration = new StreamReader(zip.GetEntry("configuration-summary.json")!.Open());
                Assert.DoesNotContain("PRIVATE-", await configuration.ReadToEndAsync(), StringComparison.Ordinal);
            }
            vm.IncludeDebugDescription = true;
            await vm.SaveDebugBundleCommand.ExecuteAsync(null);
            using var included = ZipFile.OpenRead(destination);
            Assert.NotNull(included.GetEntry("attachments/issue-description.txt"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class AuditFileDialogs : IFileDialogs
    {
        public string? OpenPath { get; set; }
        public string? ExportPath { get; set; }
        public Exception? DebugPickerError { get; set; }
        public int DebugPickerCalls { get; private set; }
        public Task<string?> ChooseNewAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> ChooseOpenAsync() => Task.FromResult(OpenPath);
        public Task<string?> ChooseBackupAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmRemoveAsync(string competitionName) => Task.FromResult(false);
        public Task<bool> ConfirmDiscardChangesAsync(int changeCount) => Task.FromResult(false);
        public Task<string?> ChooseStartListExportAsync(string suggestedName, bool print) => Task.FromResult<string?>(null);
        public Task<string?> ChooseResultXmlExportAsync(string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> ChoosePdfExportAsync(string title, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> ChooseDebugBundleAsync()
        {
            DebugPickerCalls++;
            return DebugPickerError is { } error ? Task.FromException<string?>(error) : Task.FromResult(ExportPath);
        }
    }

    [AvaloniaFact]
    public async Task BundleDialogShowsDefaultScopeAndKeepsSaveReachable()
    {
        var root = AuditFolder();
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new AuditFileDialogs(), recentSeriesStore: new(root), uiPreferencesStore: new(root));
            vm.ReviewDebugBundleCommand.Execute(null);
            var owner = new Window(); owner.Show();
            var dialog = new DebugBundleDialog { DataContext = vm };
            var closed = dialog.ShowDialog(owner);
            try
            {
                dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
                Assert.NotNull(dialog.FindControl<ComboBox>("DebugLogWindowBox")!.SelectedItem);
                Assert.Equal(24, vm.DebugLogWindow.Hours);
                var include = dialog.FindControl<CheckBox>("IncludeDebugSeriesCheckBox")!;
                Assert.False(include.IsChecked); Assert.False(include.IsEffectivelyEnabled);
                var save = dialog.FindControl<Button>("SaveDebugBundleButton")!;
                Assert.True(save.IsEffectivelyVisible); Assert.True(save.IsEffectivelyEnabled);
                WithinWindowWidth(dialog, save);
                CaptureSettingsWindow(dialog, 720, 520, "audit-bundle");
            }
            finally { dialog.Close(); await closed; owner.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BundleCleanupFailureReportsSensitiveTemporaryCopyAndDoesNotPublish()
    {
        if (!OperatingSystem.IsWindows()) { return; } // Windows denies deletion of a read-only file.
        var root = AuditFolder();
        try
        {
            var source = Path.Combine(root, "source.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            await workspace.CreateAsync(source, new("Synthetic", "Test", "Test", new(2026, 10, 3), new(2026, 10, 3), "FIN", "2026/27"));
            var destination = Path.Combine(root, "bundle.zip");
            byte[] original = [1, 2, 3]; await File.WriteAllBytesAsync(destination, original);
            var now = DateTimeOffset.UtcNow;
            var exporter = new DebugBundleExporter(new ReadOnlySnapshotReader(), Path.Combine(root, "missing"));
            var error = await Assert.ThrowsAsync<DebugBundleCleanupException>(() => exporter.ExportAsync(destination, new(source, true, now.AddHours(-1), now)));
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            var retained = Assert.Single(error.Paths);
            Assert.True(File.Exists(retained)); Assert.Contains(retained, error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(root, ".debug-bundle-*"));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(root)) { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(root, true);
        }
    }

    private sealed class ReadOnlySnapshotReader : IAuditTrailReader
    {
        private readonly SqliteAuditTrailReader _inner = new();
        public IReadOnlyList<string> Sources(AuditArea area) => _inner.Sources(area);
        public Task<AuditPage> ReadAsync(string path, AuditQuery query, CancellationToken ct = default) => _inner.ReadAsync(path, query, ct);
        public Task<AuditDetail> ReadDetailAsync(string path, AuditReference reference, CancellationToken ct = default) => _inner.ReadDetailAsync(path, reference, ct);
        public Task<AuditSeriesSummary> ReadSummaryAsync(string path, CancellationToken ct = default) => _inner.ReadSummaryAsync(path, ct);
        public async Task CreateSupportSnapshotAsync(string path, string destination, CancellationToken ct = default)
        {
            await _inner.CreateSupportSnapshotAsync(path, destination, ct);
            File.SetAttributes(destination, FileAttributes.ReadOnly);
        }
    }

    [AvaloniaFact]
    public async Task ThirtyOneDayBundleUsesOneClockReadingAndCreatesZip()
    {
        var root = AuditFolder();
        try
        {
            var destination = Path.Combine(root, "bundle.zip");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new AuditFileDialogs { ExportPath = destination },
                recentSeriesStore: new(root), uiPreferencesStore: new(root), timeProvider: new AdvancingBundleClock(),
                debugBundleExporter: new(new SqliteAuditTrailReader(), Path.Combine(root, "missing")));
            vm.ReviewDebugBundleCommand.Execute(null);
            vm.DebugLogWindow = vm.DebugLogWindows.Single(w => w.Hours == 744);
            await vm.SaveDebugBundleCommand.ExecuteAsync(null);
            Assert.True(File.Exists(destination), vm.DebugBundleStatus);
            using var zip = ZipFile.OpenRead(destination);
            using var input = zip.GetEntry("manifest.json")!.Open();
            using var manifest = await JsonDocument.ParseAsync(input);
            var from = manifest.RootElement.GetProperty("From").GetDateTimeOffset();
            var until = manifest.RootElement.GetProperty("Until").GetDateTimeOffset();
            Assert.Equal(TimeSpan.FromDays(31), until - from);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class AdvancingBundleClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Increment(ref _ticks), TimeSpan.Zero);
    }

    [AvaloniaTheory]
    [InlineData(24)]
    [InlineData(744)]
    public async Task SaveZipButtonCreatesReviewedZipAtDialogSelectionAndKeepsStatusVisible(int hours)
    {
        var root = AuditFolder();
        try
        {
            var destination = Path.Combine(root, "selected.zip");
            var picker = new AuditFileDialogs { ExportPath = destination };
            var mainPicker = new AuditFileDialogs { ExportPath = Path.Combine(root, "wrong-owner.zip") };
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, mainPicker, recentSeriesStore: new(root), uiPreferencesStore: new(root),
                timeProvider: new AdvancingBundleClock(), debugBundleExporter: new(new SqliteAuditTrailReader(), Path.Combine(root, "missing")));
            vm.ReviewDebugBundleCommand.Execute(null);
            vm.DebugLogWindow = vm.DebugLogWindows.Single(w => w.Hours == hours);
            vm.IncludeDebugDescription = true;
            vm.DebugIssueDescription = string.Join('\n', Enumerable.Repeat("Synthetic issue description line", 100));
            var owner = new Window(); owner.Show();
            var dialog = new DebugBundleDialog(picker) { DataContext = vm };
            var closed = dialog.ShowDialog(owner);
            try
            {
                dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                dialog.FindControl<Button>("SaveDebugBundleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotNull(vm.SaveDebugBundleCommand.ExecutionTask);
                await vm.SaveDebugBundleCommand.ExecutionTask;
                Assert.True(File.Exists(destination), vm.DebugBundleStatus);
                Assert.Equal(1, picker.DebugPickerCalls); Assert.Equal(0, mainPicker.DebugPickerCalls);
                Assert.False(File.Exists(mainPicker.ExportPath));
                using var zip = ZipFile.OpenRead(destination);
                Assert.NotNull(zip.GetEntry("manifest.json")); Assert.NotNull(zip.GetEntry("attachments/issue-description.txt"));
                dialog.UpdateLayout(); Dispatcher.UIThread.RunJobs(); dialog.UpdateLayout();
                var status = dialog.FindControl<TextBlock>("DebugBundleStatusText")!;
                Assert.Contains("Saved", status.Text, StringComparison.Ordinal);
                var position = status.TranslatePoint(default, dialog)!.Value;
                Assert.True(position.Y >= 0 && position.Y + status.Bounds.Height <= dialog.Bounds.Height,
                    "Export status must remain visible without scrolling through the issue description.");
            }
            finally { dialog.Close(); await closed; owner.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveZipButtonReportsPickerCancellationOrFailureWithoutClosingDialog(bool fail)
    {
        var root = AuditFolder();
        try
        {
            var picker = new AuditFileDialogs { DebugPickerError = fail ? new IOException("Synthetic picker failure") : null };
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new AuditFileDialogs(), recentSeriesStore: new(root), uiPreferencesStore: new(root));
            vm.ReviewDebugBundleCommand.Execute(null);
            var owner = new Window(); owner.Show();
            var dialog = new DebugBundleDialog(picker) { DataContext = vm };
            var closed = dialog.ShowDialog(owner);
            try
            {
                dialog.FindControl<Button>("SaveDebugBundleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(fail ? "Could not open the save dialog" : "cancelled", vm.DebugBundleStatus, StringComparison.Ordinal);
                Assert.False(vm.IsDebugBundleBusy); Assert.True(dialog.IsVisible);
                Assert.Empty(Directory.GetFiles(root, "*.zip"));
            }
            finally { dialog.Close(); await closed; owner.Close(); }
        }
        finally { Directory.Delete(root, true); }
    }
}

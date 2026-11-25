using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

// There is no connect step. The open view decides what is received:
// - Timing (with a run): impulses are received, journalled and timed. Leaving the view stops it, so no impulse is ever
//   attributed without the operator seeing it.
// - Settings → Timing devices: signals are only shown. Settings can always be edited and take effect at once.
// - Any other view: nothing is received.
// Every change goes through one serialized chain, so a device is always released before it is opened again.
public sealed partial class MainViewModel
{
    internal const int TimingDevicesTab = 1;
    private static readonly TimeSpan s_settingsDelay = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_releaseDelay = TimeSpan.FromMilliseconds(500);
    private TimingSignalMonitor? _signalMonitor;
    // B Clock devices are monitored and released on their own, so a slow B never delays A.
    private TimingSignalMonitor? _backupSignalMonitor;
    // The devices that the running chain of B work (_backupClockTask) may still hold. A waits only for its own device.
    private readonly List<CaptureOptions> _backupDevices = [];
    private string _signalMonitorKey = "";
    private string _signalMonitorProblem = "";
    private DispatcherTimer? _signalMonitorTimer;
    private DispatcherTimer? _timingSettingsTimer;
    private bool _timingConfigured;
    private bool _loadingTimingSettings;
    private bool _timingSettingsChanged;
    private bool _openingTimingRun;
    private bool _timingListeningEnded;
    private long _timingRetryAt = long.MaxValue;

    // Why nothing is received in the open Timing view; empty while timing works.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimingProblem))]
    private string _timingProblem = "";
    [ObservableProperty] private bool _canSetTrainingAside;
    // Whether the shown device settings are in use, or what is wrong with them.
    [ObservableProperty] private string _timingSettingsStatus = "";
    public bool HasTimingProblem => TimingProblem.Length > 0;
    internal Task TimingListeningTask { get; private set; } = Task.CompletedTask;
    private bool IsTimingDevicesOpen => IsSettingsSection && SelectedSettingsTab == TimingDevicesTab;

    partial void OnActiveSectionChanged(WorkspaceSection value) => UpdateTimingListening();

    partial void OnSelectedSettingsTabChanged(int value) => UpdateTimingListening();

    private void UpdateTimingListening()
    {
        if (!_timingListeningEnded) { TimingListeningTask = UpdateTimingListeningAsync(TimingListeningTask); }
    }

    private async Task UpdateTimingListeningAsync(Task previous)
    {
        await previous;
        if (_timingListeningEnded) { return; }
        try
        {
            if (!IsTimingDevicesOpen)
            {
                CommitTimingSettings();
                await StopSignalMonitorAsync();
            }
            if (!IsTimingSection && workspace.Timing?.IsActive == true) { await StopTimingAsync(leaving: true); }
            if (IsTimingSection && !_openingTimingRun) { await StartTimingAsync(); }
            if (IsTimingDevicesOpen)
            {
                _timingConfigured = true; // the shown devices are the ones in use
                _timingSettingsChanged |= HasTimingMigrationNotes; // reviewed here, stored on leaving the tab
                await RefreshSignalMonitorAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The chain must survive any failure (for example of the password store): the next view change tries again.
            SetTimingProblem("Timing could not start. Check the device settings.");
            SetStatus("The timing devices could not be opened or released: " + ex.Message, error: true);
        }
    }

    // Receives timing for the open run. Never throws: whatever prevents it is shown in the view and can be corrected there
    // or in Settings.
    internal async Task StartTimingAsync()
    {
        if (_timingList is null || IsTimingBusy || workspace.Timing is not { } timing) { return; }
        _timingRetryAt = long.MaxValue;
        // A previous stop that could not finish (for example storage trouble) completes first.
        if (timing.IsStopping) { await StopTimingAsync(); }
        if (timing.IsStopping)
        {
            SetTimingProblem("Earlier times are still being saved. Timing starts again when they are.");
            RetryTimingLater();
            return;
        }
        if (timing.IsActive) { SetTimingProblem(""); return; }
        if (!_timingConfigured) { SetTimingProblem("No timing devices are chosen yet. Choose them in Timing devices."); return; }
        IsTimingBusy = true;
        try
        {
            EnsureDeskClean();
            if (!DateOnly.TryParseExact(TimingDeviceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { throw new DomainValidationException("Device date must use YYYY-MM-DD."); }
            // Primary timing is validated on its own: an incomplete or invalid B Clock never prevents A from starting.
            var primary = new TimingRoleConfiguration(PrimaryTimingRoles.Select(x => x.ToAssignment()).ToArray());
            primary.Validate();
            var since = AlgeReceiveFrom(timing);
            var capture = primary.PrimaryCapture(date, since);
            if (timing.HasTrainingInput && !capture.Any(x => x.Options.Simulation))
            {
                SetTimingProblem("This run has training times. Real timing is not received until they are set aside.", canSetAside: true);
                return;
            }
            if (!_backupClockTask.IsCompleted && capture.Any(a => _backupDevices.Any(b => SameLocalTimingEndpoint(a.Options, b))))
            {
                SetTimingProblem("The device was used by the B Clock and is being released. Timing starts in a moment.");
                RetryTimingLater(s_releaseDelay);
                return;
            }
            try { foreach (var device in capture) { ValidateAuthoritativeTimingEndpoint(device.Options); } }
            catch (DomainValidationException ex)
            {
                SetTimingProblem(ex.Message);
                RetryTimingLater(); // another capture may release the device
                return;
            }
            var inputs = new List<TimingSourceInput>();
            try
            {
                foreach (var device in capture)
                {
                    inputs.Add(new(TimingSourceFactory.Create(device.Connection, device.Options, _timingHttp, AlgePassword, () => _simulator = new(), live: true),
                        device.Options with { ContinuesClock = true }));
                }
                await timing.FollowStartOrderAsync(FollowTimingOrder);
                await HoldTimingChannelsAsync(timing);
                await timing.StartAsync(inputs, TimingOperator);
            }
            catch
            {
                // Sources created before a failure are not owned by capture; close them so no device stays open.
                if (!timing.IsActive) { foreach (var input in inputs) { await input.Source.DisposeAsync(); } }
                throw;
            }
            ConfigureTimingCheckpoints();
            if (_current is not null) { _current = await workspace.ReadAsync(); }
            SetTimingProblem("");
            RefreshTiming();
            SetStatus("Timing is on HOLD. Check the race, then resume each timing position before assigning impulses.");
            // B Clock follows only after A is capturing. It runs detached and reports through B Clock status only.
            StartBackupClock(date, since);
        }
        catch (DomainValidationException ex) { SetTimingProblem(ex.Message); }
        catch (Exception ex) when (ex is SeriesFileException or SeriesConflictException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetTimingProblem(ex is SeriesFileException or SeriesConflictException ? ex.Message : "Timing could not start. Check the device settings.");
            RetryTimingLater();
        }
        finally { IsTimingBusy = false; }
    }

    internal async Task StopTimingAsync(bool leaving = false)
    {
        if (workspace.Timing is not { IsActive: true } timing) { return; }
        _timingRetryAt = long.MaxValue;
        var onCourse = timing.Snapshot is { } snapshot ? RaceFlow.OnCourse(snapshot).Count : 0;
        IsTimingBusy = true;
        await GuardAsync(async () =>
        {
            await timing.StopAsync();
            if (leaving && onCourse > 0)
            { SetStatus($"Timing is off: {onCourse} competitor(s) on course. Impulses are not received until Timing is open again.", error: true); }
        });
        RefreshTiming();
        if (!timing.IsActive) { StopBackupClock(); }
        else { RetryTimingLater(); } // the stop waits for storage; an open Timing view starts again afterwards
        IsTimingBusy = false;
    }

    // Called before B work is added to the chain. Devices of finished work are forgotten.
    private void AddBackupDevices(IEnumerable<CaptureOptions> devices)
    {
        if (_backupClockTask.IsCompleted) { _backupDevices.Clear(); }
        _backupDevices.AddRange(devices);
    }

    private void RetryTimingLater(TimeSpan? delay = null) => _timingRetryAt = Environment.TickCount64 + (long)(delay ?? s_retryDelay).TotalMilliseconds;

    internal void RetryTimingStart()
    {
        if (!IsTimingSection || Environment.TickCount64 < _timingRetryAt) { return; }
        _timingRetryAt = long.MaxValue;
        UpdateTimingListening();
    }

    private void SetTimingProblem(string text, bool canSetAside = false)
    {
        TimingProblem = text;
        CanSetTrainingAside = canSetAside;
    }

    // Training times stay unchanged with the earlier start list revision; real timing continues on a new one.
    [RelayCommand]
    private async Task SetTrainingAsideAsync()
    {
        if (!CanSetTrainingAside || _timingList is not { } list || TimingCompetition is not { } competition) { return; }
        var run = TimingRun;
        var saved = false;
        await GuardAsync(async () =>
        {
            EnsureDeskClean();
            await workspace.SetTrainingAsideAsync(list.Id, (await workspace.ReadAsync()).Revision, TimingOperator, DateTimeOffset.UtcNow);
            if (_current is not null) { _current = await workspace.ReadAsync(); }
            saved = true;
        });
        if (saved) { await OpenTimingRunAsync(new(competition, run)); }
    }

    [RelayCommand]
    private void ShowTimingDevices()
    {
        SelectedSettingsTab = TimingDevicesTab;
        ShowSettings();
    }

    // Edited device settings are used at once. Typing is collected for a moment, so a half-typed port is not opened.
    private void OnTimingSettingChanged()
    {
        if (_loadingTimingSettings) { return; }
        _timingConfigured = true;
        _timingSettingsChanged = true;
        StartTimingSettingsTimer();
    }

    // A corrected password opens the account again. Passwords are never part of the monitor key.
    private void OnAlgePasswordChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AlgeAccountEditor.Password) || sender is not AlgeAccountEditor { Password.Length: > 0 }) { return; }
        _signalMonitorKey = "";
        StartTimingSettingsTimer();
    }

    private void StartTimingSettingsTimer()
    {
        _timingSettingsTimer ??= new DispatcherTimer(s_settingsDelay, DispatcherPriority.Background, (_, _) =>
        {
            CommitTimingSettings();
            if (IsTimingDevicesOpen) { UpdateTimingListening(); }
        });
        _timingSettingsTimer.Stop();
        _timingSettingsTimer.Start();
    }

    private void CommitTimingSettings()
    {
        _timingSettingsTimer?.Stop();
        if (_timingSettingsChanged) { SaveTimingSettings(); }
    }

    // Saves valid settings on this computer. Invalid ones stay on screen with the reason and keep the last saved file.
    internal bool SaveTimingSettings()
    {
        _timingSettingsChanged = false;
        try
        {
            var configuration = CurrentTimingConfiguration();
            configuration.Validate();
            timingPreferencesStore?.Save(configuration);
            TimingMigrationNotes = "";
            TimingSettingsStatus = "Timing settings saved on this computer.";
            RefreshBackupClockStatus();
            return true;
        }
        catch (Exception ex) when (ex is DomainValidationException or IOException or UnauthorizedAccessException)
        {
            TimingSettingsStatus = "Not saved · " + ex.Message;
            return false;
        }
    }

    private static bool IsTimingSetting(string? property) => property is nameof(TimingRoleEditor.Source) or nameof(TimingRoleEditor.UsesLeaderConnection)
        or nameof(TimingRoleEditor.Channel) or nameof(TimingRoleEditor.UsbId) or nameof(TimingRoleEditor.Firmware) or nameof(TimingRoleEditor.Port)
        or nameof(TimingRoleEditor.BaudRate) or nameof(TimingRoleEditor.AlgeDeviceId) or nameof(TimingRoleEditor.AlgeUsername) or nameof(TimingRoleEditor.ReplayPath);

    private async Task RefreshSignalMonitorAsync()
    {
        // The B Clock releases its devices on its own, and a capture that is still saving its last input keeps them. Waiting
        // here would also hold up the return to Timing, so look again shortly.
        if (!_backupClockTask.IsCompleted || workspace.Timing?.IsActive == true || workspace.Auxiliary?.IsActive == true) { await StopSignalMonitorAsync(); StartTimingSettingsTimer(); return; }
        var problem = "";
        var sources = new List<TimingCaptureSource>();
        var backup = new List<TimingCaptureSource>();
        try
        {
            var configuration = CurrentTimingConfiguration();
            var date = DateOnly.FromDateTime(DateTime.Today);
            new TimingRoleConfiguration(configuration.Assignments.Where(x => x.Role.IsPrimary).ToArray()).Validate();
            sources.AddRange(configuration.PrimaryCapture(date, DateTimeOffset.UtcNow, "monitor"));
            // A B Clock that is not independent of primary timing is reported and not opened.
            if (configuration.HasBackupClock)
            {
                configuration.Validate();
                backup.AddRange(configuration.BackupCapture(date, DateTimeOffset.UtcNow, "monitor"));
            }
        }
        catch (DomainValidationException ex) { problem = ex.Message; }
        // Simulator and replay sources have no device to check.
        sources.RemoveAll(x => x.Options.Simulation);
        backup.RemoveAll(x => x.Options.Simulation);
        var key = string.Join("\n", sources.Concat(backup).Select(x => $"{x.Connection.ConnectionKey}|{x.Options.Endpoint}"));
        if ((_signalMonitor is null && _backupSignalMonitor is null) || key != _signalMonitorKey)
        {
            await StopSignalMonitorAsync();
            if (!_backupClockTask.IsCompleted) { StartTimingSettingsTimer(); return; }
            TimingSignalMonitor? Monitor(List<TimingCaptureSource> devices)
            {
                var inputs = new List<TimingSourceInput>();
                foreach (var device in devices)
                {
                    try { inputs.Add(new(TimingSourceFactory.Create(device.Connection, device.Options, _timingHttp, AlgePassword, live: true), device.Options)); }
                    catch (DomainValidationException ex) { problem = ex.Message; }
                }
                return inputs.Count > 0 ? new(inputs, new AlgeDecoderFactory()) : null;
            }
            _signalMonitor = Monitor(sources);
            _backupSignalMonitor = Monitor(backup);
            _signalMonitorKey = problem.Length == 0 ? key : "";
        }
        _signalMonitorProblem = problem;
        _signalMonitorTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => ShowSignalMonitor());
        _signalMonitorTimer.Start();
        ShowSignalMonitor();
    }

    private void ShowSignalMonitor()
    {
        if (!IsTimingDevicesOpen || workspace.Timing?.IsActive == true) { return; }
        TimingConnection = _signalMonitorProblem.Length > 0 ? "Not receiving · " + _signalMonitorProblem
            : _signalMonitor is null && _backupSignalMonitor is null ? "No device to check. Simulator and replay input is received in Timing."
            : string.Join(" · ", new[] { _signalMonitor?.Status, _backupSignalMonitor is { } b ? "B: " + b.Status : null }.Where(x => x is not null)) + " · signals are shown here, not recorded";
        RefreshTimingRoleSignals();
    }

    private async Task StopSignalMonitorAsync()
    {
        _signalMonitorTimer?.Stop();
        _signalMonitorKey = "";
        if (_backupSignalMonitor is { } backup)
        {
            _backupSignalMonitor = null;
            AddBackupDevices(backup.Options);
            // The B Clock opens its devices only after this task.
            _backupClockTask = ReleaseAsync(_backupClockTask, backup);
        }
        if (_signalMonitor is { } monitor)
        {
            _signalMonitor = null;
            await monitor.DisposeAsync();
        }
        RefreshTimingRoleSignals();

        static async Task ReleaseAsync(Task previous, TimingSignalMonitor monitor)
        {
            try { await previous; } catch (Exception ex) when (ex is not OutOfMemoryException) { /* already reported */ }
            await monitor.DisposeAsync();
        }
    }

    // Closing the window releases every device.
    private async Task EndTimingListeningAsync()
    {
        await TimingListeningTask;
        CommitTimingSettings();
        await StopSignalMonitorAsync();
    }

    private void DisposeTimingListening()
    {
        _timingListeningEnded = true;
        CommitTimingSettings();
        _signalMonitorTimer?.Stop();
        foreach (var monitor in new[] { _signalMonitor, _backupSignalMonitor }) { _ = monitor?.DisposeAsync().AsTask(); }
        _signalMonitor = _backupSignalMonitor = null;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

// Live B Clock: an optional, independent auxiliary capture that follows the active A run.
// Strict isolation: B work runs detached from A connect, capture and run switching. Every B failure is caught and shown
// as B Clock status only. B observations go to the auxiliary tables and the read-only comparison, never to A timing.
public sealed partial class MainViewModel
{
    private int _backupClockGeneration;
    private Task _backupClockTask = Task.CompletedTask;
    private bool _backupClockExpected;
    private bool _backupClockConnecting;
    private string _backupClockError = "";
    private string _backupRunSwitchMessage = "";
    private bool _backupRunSwitchFailed;
    private DateOnly _backupClockDate;
    private DateTimeOffset? _backupClockSince;
    private BackupMonitorSnapshot? _backupComparison;
    [ObservableProperty] private string _backupClockStatusText = "B Clock · not configured";
    [ObservableProperty] private BackupClockHealth _backupClockHealth = BackupClockHealth.NotConfigured;
    [ObservableProperty] private bool _isBackupClockActive;
    public bool HasBackupClockProblem => BackupClockHealth is BackupClockHealth.MissingSignal
        or BackupClockHealth.TimeDifferenceWarning or BackupClockHealth.DeviceUnavailable;
    public bool CanRetryBackupClock => IsTimingConnected && _backupClockExpected;
    internal Task BackupClockTask => _backupClockTask;
    partial void OnBackupClockHealthChanged(BackupClockHealth value) => OnPropertyChanged(nameof(HasBackupClockProblem));

    // Called after A has started capturing. Never throws and never awaits B.
    private void StartBackupClock(DateOnly date, DateTimeOffset? since, string algePassword)
    {
        var generation = ++_backupClockGeneration;
        _backupClockError = ""; _backupRunSwitchMessage = ""; _backupRunSwitchFailed = false;
        _backupClockDate = date; _backupClockSince = since;
        TimingRoleConfiguration? configuration = null;
        try
        {
            if (BackupTimingRoles.Count != 0)
            {
                var candidate = CurrentTimingConfiguration();
                candidate.Validate();
                // A primary ALGE Results capture passes its bound; otherwise B reads the receive-from field itself.
                if (candidate.BackupStart!.Connection.Source == TimingSourceType.AlgeResults) { since ??= ReadAlgeReceiveFrom(); }
                configuration = candidate;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _backupClockError = "settings: " + ex.Message; }
        _backupClockExpected = BackupTimingRoles.Count != 0;
        if (configuration is null) { RefreshBackupClockStatus(); return; }
        _backupClockConnecting = true;
        RefreshBackupClockStatus();
        var previous = _backupClockTask;
        _backupClockTask = ConnectBackupClockAsync(previous, generation, configuration, date, since, algePassword);
    }

    private async Task ConnectBackupClockAsync(Task previous, int generation, TimingRoleConfiguration configuration,
        DateOnly date, DateTimeOffset? since, string algePassword)
    {
        // Yield first so the A connect path completes before any B work starts.
        await Task.Yield();
        ITimingSource? source = null;
        var error = "";
        try
        {
            try { await previous; } catch (Exception ex) when (ex is not OutOfMemoryException) { /* earlier B errors were already reported */ }
            if (generation != _backupClockGeneration || workspace.Auxiliary is not { } auxiliary
                || workspace.Timing is not { IsActive: true, ListId: { } listId }) { return; }
            if (auxiliary.State(AuxiliaryTimingRole.B).IsActive) { await auxiliary.StopAsync(AuxiliaryTimingRole.B); }
            var connection = configuration.BackupStart!.Connection;
            var options = configuration.BackupCaptureOptions(date, connection.Source == TimingSourceType.AlgeResults ? since : null);
            ValidateAuxiliaryTimingEndpoint(options, AuxiliaryTimingRole.B);
            source = TimingSourceFactory.Create(connection, options, _timingHttp, algePassword, RememberAlgePassword);
            if (generation != _backupClockGeneration) { return; }
            await auxiliary.StartAsync(listId, AuxiliaryTimingRole.B, source, options, TimingOperator, live: true);
            source = null; // owned by the auxiliary capture from here on
            if (generation != _backupClockGeneration || workspace.Timing?.IsActive != true)
            { await auxiliary.StopAsync(AuxiliaryTimingRole.B); return; }
            if (workspace.Timing?.ListId is { } current && current != listId) { QueueLiveBackupRunSwitch(auxiliary, current); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { error = "connect failed: " + ex.Message; }
        finally
        {
            if (source is not null)
            {
                try { await source.DisposeAsync(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* the unused B source failed to close; A is unaffected */ }
            }
            if (generation == _backupClockGeneration)
            {
                _backupClockConnecting = false;
                if (error.Length != 0) { _backupClockError = error; }
                RefreshBackupClockStatus();
            }
        }
    }

    // Stops B after A disconnects. Detached so a slow B source cannot hold the A disconnect.
    private void StopBackupClock()
    {
        _backupClockGeneration++;
        _backupClockExpected = false; _backupClockConnecting = false; _backupClockError = "";
        _backupRunSwitchMessage = ""; _backupRunSwitchFailed = false;
        var previous = _backupClockTask;
        _backupClockTask = StopBackupClockAsync(previous, _backupClockGeneration);
        RefreshBackupClockStatus();
    }

    private async Task StopBackupClockAsync(Task previous, int generation)
    {
        try
        {
            try { await previous; } catch (Exception ex) when (ex is not OutOfMemoryException) { /* already reported */ }
            if (workspace.Auxiliary is { } auxiliary && auxiliary.State(AuxiliaryTimingRole.B).IsActive)
            { await auxiliary.StopAsync(AuxiliaryTimingRole.B); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (generation == _backupClockGeneration) { _backupClockError = "disconnect failed: " + ex.Message; } }
        if (generation == _backupClockGeneration) { RefreshBackupClockStatus(); }
    }

    private async Task EndBackupClockForCloseAsync()
    {
        _backupClockGeneration++;
        try { await _backupClockTask; } catch (Exception ex) when (ex is not OutOfMemoryException) { /* close continues with StopAllAsync */ }
    }

    [RelayCommand]
    private void RetryBackupClock()
    {
        if (!CanRetryBackupClock) { return; }
        var state = workspace.Auxiliary?.State(AuxiliaryTimingRole.B);
        if (state is { IsActive: true } && !string.IsNullOrWhiteSpace(state.Fault))
        {
            // A connected B with a storage fault keeps its raw input queued; retry storage instead of reconnecting.
            workspace.Auxiliary!.RetryStorage(AuxiliaryTimingRole.B);
            RefreshBackupClockStatus();
            return;
        }
        var password = Mt1Password;
        if (password.Length == 0 && UsesAlgeResultsAccount && OperatingSystem.IsWindows()) { password = AlgeCredential.Read() ?? ""; }
        StartBackupClock(_backupClockDate, _backupClockSince, password);
        if (UsesAlgeResultsAccount) { Mt1Password = ""; }
        SetStatus("Reconnecting B Clock. A timing continues unchanged.");
    }

    [RelayCommand]
    private void DisconnectBackupClock()
    {
        if (!_backupClockExpected) { return; }
        var generation = ++_backupClockGeneration;
        _backupClockConnecting = false;
        _backupClockError = "disconnected by operator";
        var previous = _backupClockTask;
        _backupClockTask = StopBackupClockAsync(previous, generation);
        RefreshBackupClockStatus();
        SetStatus("B Clock disconnected. A timing continues unchanged.");
    }

    private void ResetBackupClock()
    {
        _backupClockGeneration++;
        _backupClockExpected = false; _backupClockConnecting = false; _backupClockError = "";
        _backupRunSwitchMessage = ""; _backupRunSwitchFailed = false; _backupComparison = null;
        RefreshBackupClockStatus();
    }

    private void RefreshBackupClockStatus()
    {
        var state = workspace.Auxiliary?.State(AuxiliaryTimingRole.B);
        IsBackupClockActive = state is { IsActive: true, Live: true };
        BackupClockStatus status;
        if (!IsTimingConnected || !_backupClockExpected)
        {
            status = IsTimingConnected || BackupTimingRoles.Count == 0
                ? BackupClockStatus.Evaluate(false, false, null, null)
                : new(BackupClockHealth.NotConfigured, "B Clock · connects with timing");
        }
        else if (_backupClockConnecting && !IsBackupClockActive) { status = new(BackupClockHealth.WaitingForB, "B Clock · connecting…"); }
        else
        {
            var fault = !string.IsNullOrWhiteSpace(state?.Fault) ? state!.Fault
                : _backupClockError.Length != 0 ? _backupClockError
                : _backupRunSwitchFailed ? _backupRunSwitchMessage : null;
            status = BackupClockStatus.Evaluate(true, IsBackupClockActive, fault, _backupComparison);
        }
        BackupClockHealth = status.Health;
        BackupClockStatusText = status.Text;
        OnPropertyChanged(nameof(CanRetryBackupClock));
    }

    private void QueueLiveBackupRunSwitch(AuxiliaryTimingWorkspace auxiliary, Guid listId)
    {
        _backupRunSwitchMessage = "B is changing runs. Backup comparison is unavailable until the new run is connected.";
        _backupRunSwitchFailed = false;
        // The helper catches its own failures. Optional backup storage or a fragmented B message must never hold up A.
        _ = SwitchLiveBackupRunAsync(auxiliary, listId);
    }

    private async Task SwitchLiveBackupRunAsync(AuxiliaryTimingWorkspace auxiliary, Guid listId)
    {
        string error = "";
        try { await auxiliary.SwitchRunAsync(AuxiliaryTimingRole.B, listId); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { error = "B did not change runs. Check the B Clock connection: " + ex.Message; }
        if (!ReferenceEquals(workspace.Auxiliary, auxiliary) || workspace.Timing?.ListId != listId) { return; }
        _backupRunSwitchMessage = error; _backupRunSwitchFailed = error.Length != 0;
        RefreshBackupMonitor();
    }

    private void ValidateAuthoritativeTimingEndpoint(CaptureOptions options)
    {
        if (workspace.Auxiliary is not { } auxiliary) { return; }
        foreach (var role in Enum.GetValues<AuxiliaryTimingRole>())
        {
            var state = auxiliary.State(role);
            if (state is { IsActive: true, Options: { } active } && SameLocalTimingEndpoint(options, active))
            { throw new DomainValidationException($"The selected device is already connected as {role}. Choose a different physical device with its own port or device ID."); }
        }
    }

    private void ValidateAuxiliaryTimingEndpoint(CaptureOptions options, AuxiliaryTimingRole role)
    {
        if (workspace.Timing is { IsActive: true, LastCaptureOptions: { } activeA } && SameLocalTimingEndpoint(options, activeA))
        { throw new DomainValidationException("The selected device is already connected as A. Choose a different physical device with its own port or device ID."); }
        if (workspace.Auxiliary is not { } auxiliary) { return; }
        foreach (var other in Enum.GetValues<AuxiliaryTimingRole>().Where(x => x != role))
        {
            var state = auxiliary.State(other);
            if (state is { IsActive: true, Options: { } active } && SameLocalTimingEndpoint(options, active))
            { throw new DomainValidationException($"The selected device is already connected as {other}. Disconnect that role before reusing its device."); }
        }
    }

    internal static bool SameLocalTimingEndpoint(CaptureOptions left, CaptureOptions right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        var leftUsb = left.Device.Contains("Timy", StringComparison.Ordinal) && left.Device.Contains("USB", StringComparison.Ordinal);
        var rightUsb = right.Device.Contains("Timy", StringComparison.Ordinal) && right.Device.Contains("USB", StringComparison.Ordinal);
        if (leftUsb && rightUsb)
        {
            var leftId = left.Endpoint.Replace("Timy USB", "", StringComparison.Ordinal).Trim();
            var rightId = right.Endpoint.Replace("Timy USB", "", StringComparison.Ordinal).Trim();
            return leftId.Length == 0 || rightId.Length == 0 || string.Equals(leftId, rightId, StringComparison.OrdinalIgnoreCase);
        }
        if (left.Device.Contains("ALGE Results", StringComparison.Ordinal) && right.Device.Contains("ALGE Results", StringComparison.Ordinal))
        {
            var leftDevices = new[] { left.StartDeviceId?.Trim(), left.FinishDeviceId?.Trim() }.Where(x => !string.IsNullOrEmpty(x));
            var rightDevices = new[] { right.StartDeviceId?.Trim(), right.FinishDeviceId?.Trim() }.Where(x => !string.IsNullOrEmpty(x));
            return leftDevices.Intersect(rightDevices, StringComparer.Ordinal).Any();
        }
        return left.Device.Contains("serial", StringComparison.OrdinalIgnoreCase)
            && right.Device.Contains("serial", StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Endpoint.Trim(), right.Endpoint.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

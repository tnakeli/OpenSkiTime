using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed record AuxiliaryRunChoice(Guid ListId, Guid CompetitionId, int Run, string Label);

public sealed partial class MainViewModel
{
    private readonly AuxiliaryPreferencesStore _auxiliaryPreferencesStore = new();
    private bool _auxiliarySettingsLoaded;
    private DispatcherTimer? _auxiliaryUiTimer;
    public ObservableCollection<AuxiliaryRunChoice> AuxiliaryRuns { get; } = [];
    public IReadOnlyList<string> AuxiliarySources { get; } = ["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Replay file"];
    public IReadOnlyList<AuxiliaryTimingRole> AuxiliaryRoles { get; } = Enum.GetValues<AuxiliaryTimingRole>();
    [ObservableProperty] private AuxiliaryRunChoice? _auxiliaryRun;
    [ObservableProperty] private AuxiliaryTimingRole _auxiliaryRole = AuxiliaryTimingRole.B;
    [ObservableProperty] private bool _auxiliaryLive = true;
    [ObservableProperty] private string _auxiliarySource = "Timy 2/3 · USB";
    [ObservableProperty] private string _auxiliaryPort = "";
    [ObservableProperty] private string _auxiliaryUsbId = "";
    [ObservableProperty] private int _auxiliaryBaud = 38400;
    [ObservableProperty] private int _auxiliaryStartChannel;
    [ObservableProperty] private int _auxiliaryFinishChannel = 1;
    [ObservableProperty] private string _auxiliaryDeviceDate = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    [ObservableProperty] private string _auxiliaryFirmware = "Not queried";
    [ObservableProperty] private int? _auxiliaryClockOffsetMinutes;
    [ObservableProperty] private string _auxiliaryStartDevice = "";
    [ObservableProperty] private string _auxiliaryFinishDevice = "";
    [ObservableProperty] private string _auxiliaryUsername = "";
    [ObservableProperty] private string _auxiliaryPassword = "";
    [ObservableProperty] private bool _auxiliaryRememberPassword;
    [ObservableProperty] private string _auxiliaryFromUtc = "";
    [ObservableProperty] private string _auxiliaryReplayPath = "";
    [ObservableProperty] private bool _isAuxiliaryBusy;
    [ObservableProperty] private bool _isAuxiliaryConnected;
    [ObservableProperty] private string _auxiliaryConnectionStatus = "Optional backup capture is disconnected.";
    [ObservableProperty] private string _auxiliaryRunSwitchError = "";
    public bool IsAuxiliaryUsb => AuxiliarySource == AuxiliarySources[0];
    public bool IsAuxiliarySerial => AuxiliarySource == AuxiliarySources[1];
    public bool IsAuxiliaryAlge => AuxiliarySource == AuxiliarySources[2];
    public bool IsAuxiliaryReplay => AuxiliarySource == AuxiliarySources[3];
    public bool IsAuxiliaryB => AuxiliaryRole == AuxiliaryTimingRole.B;
    public bool CanEditAuxiliary => !IsAuxiliaryBusy && !IsAuxiliaryConnected;
    public bool CanConnectAuxiliary => CanEditAuxiliary && workspace.Auxiliary is not null;
    partial void OnAuxiliarySourceChanged(string value)
    {
        foreach (var name in new[] { nameof(IsAuxiliaryUsb), nameof(IsAuxiliarySerial), nameof(IsAuxiliaryAlge), nameof(IsAuxiliaryReplay) }) { OnPropertyChanged(name); }
    }
    partial void OnAuxiliaryRoleChanged(AuxiliaryTimingRole value)
    { if (value != AuxiliaryTimingRole.B) { AuxiliaryLive = false; } OnPropertyChanged(nameof(IsAuxiliaryB)); RefreshAuxiliaryConnectionState(); }
    partial void OnAuxiliaryRunChanged(AuxiliaryRunChoice? value)
    {
        if (value is not null && Competitions.FirstOrDefault(x => x.Id == value.CompetitionId) is { } competition)
        { AuxiliaryDeviceDate = competition.Values.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
    }
    partial void OnIsAuxiliaryConnectedChanged(bool value) => NotifyAuxiliaryConnection();
    partial void OnIsAuxiliaryBusyChanged(bool value) => NotifyAuxiliaryConnection();
    private void NotifyAuxiliaryConnection()
    { OnPropertyChanged(nameof(CanEditAuxiliary)); OnPropertyChanged(nameof(CanConnectAuxiliary)); }

    private void EnsureAuxiliarySettingsLoaded()
    {
        if (_auxiliarySettingsLoaded) { return; }
        _auxiliarySettingsLoaded = true;
        try
        {
            if (_auxiliaryPreferencesStore.Load() is not { } p) { return; }
            if (AuxiliarySources.Contains(p.Source)) { AuxiliarySource = p.Source; }
            AuxiliaryPort = p.Port; AuxiliaryUsbId = p.UsbId; AuxiliaryBaud = p.Baud;
            AuxiliaryStartChannel = p.StartChannel; AuxiliaryFinishChannel = p.FinishChannel;
            AuxiliaryFirmware = p.Firmware; AuxiliaryStartDevice = p.StartDevice; AuxiliaryFinishDevice = p.FinishDevice;
            AuxiliaryUsername = p.Username;
            BackupStartWarningMilliseconds = Math.Clamp(p.BackupStartWarningMilliseconds, 1, 10000);
            BackupFinishWarningMilliseconds = Math.Clamp(p.BackupFinishWarningMilliseconds, 1, 10000);
            BackupMissingGraceSeconds = Math.Clamp(p.BackupMissingGraceSeconds, 1, 300);
            AuxiliaryClockOffsetMinutes = p.AuxiliaryClockOffsetMinutes is >= -840 and <= 840 ? p.AuxiliaryClockOffsetMinutes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AuxiliaryConnectionStatus = "Auxiliary settings could not be loaded: " + ex.Message; }
    }

    [RelayCommand]
    private async Task RefreshAuxiliaryRunsAsync()
    {
        EnsureAuxiliarySettingsLoaded();
        await GuardAsync(async () =>
        {
            var previous = AuxiliaryRun?.ListId;
            var choices = new List<AuxiliaryRunChoice>();
            foreach (var competition in Competitions)
            {
                var lists = await workspace.ReadStartListsAsync(competition.Id);
                foreach (var group in lists.Revisions.GroupBy(x => x.Plan.RunNumber).OrderBy(x => x.Key))
                {
                    var list = group.MaxBy(x => x.Revision)!;
                    choices.Add(new(list.Id, competition.Id, group.Key, $"{competition.Values.ShortLabel} / Run {group.Key}"));
                }
            }
            AuxiliaryRuns.Clear(); foreach (var choice in choices) { AuxiliaryRuns.Add(choice); }
            AuxiliaryRun = AuxiliaryRuns.FirstOrDefault(x => x.ListId == previous)
                ?? AuxiliaryRuns.FirstOrDefault(x => x.ListId == _timingList?.Id) ?? AuxiliaryRuns.FirstOrDefault();
            RefreshTimingPorts(); RefreshAuxiliaryConnectionState();
        });
    }

    internal async Task SelectAuxiliaryReportRunAsync(Guid competitionId, int run)
    {
        await RefreshAuxiliaryRunsAsync();
        if (!IsAuxiliaryConnected) { AuxiliaryRun = AuxiliaryRuns.FirstOrDefault(x => x.CompetitionId == competitionId && x.Run == run); }
    }

    [RelayCommand]
    private void SaveAuxiliaryPreferences()
    {
        try
        {
            if (AuxiliaryClockOffsetMinutes is < -840 or > 840)
            { throw new DomainValidationException("Local clock UTC offset must be between -840 and 840 minutes, or left blank."); }
            _auxiliaryPreferencesStore.Save(new(AuxiliarySource, AuxiliaryPort, AuxiliaryUsbId, AuxiliaryBaud,
                AuxiliaryStartChannel, AuxiliaryFinishChannel, AuxiliaryFirmware, AuxiliaryStartDevice, AuxiliaryFinishDevice, AuxiliaryUsername,
                BackupStartWarningMilliseconds, BackupFinishWarningMilliseconds, BackupMissingGraceSeconds, AuxiliaryClockOffsetMinutes));
            AuxiliaryConnectionStatus = "Auxiliary connection settings saved. Passwords use Credential Manager only when requested.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DomainValidationException)
        { AuxiliaryConnectionStatus = "Settings were not saved: " + ex.Message; }
    }

    [RelayCommand]
    private async Task ConnectAuxiliaryAsync()
    {
        if (!CanConnectAuxiliary || workspace.Auxiliary is not { } auxiliary) { return; }
        IsAuxiliaryBusy = true;
        await GuardAsync(async () =>
        {
            var listId = AuxiliaryLive && IsAuxiliaryB ? _timingList?.Id : AuxiliaryRun?.ListId;
            if (listId is null) { throw new DomainValidationException("Choose a saved run before connecting backup timing."); }
            if (!DateOnly.TryParseExact(AuxiliaryDeviceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            { throw new DomainValidationException("Auxiliary device date must use YYYY-MM-DD."); }
            if (IsTimingConnected && IsAuxiliaryUsb && IsTimyUsb
                && (string.IsNullOrWhiteSpace(AuxiliaryUsbId) || string.IsNullOrWhiteSpace(TimyDeviceId)
                    || AuxiliaryUsbId.Trim() == TimyDeviceId.Trim()))
            { throw new DomainValidationException("For two Timy USB clocks, enter separate explicit device IDs for A and B."); }
            if (IsTimingConnected && IsAuxiliarySerial && IsMt1Serial
                && string.Equals(AuxiliaryPort.Trim(), TimingPort.Trim(), StringComparison.OrdinalIgnoreCase))
            { throw new DomainValidationException("A and auxiliary timing cannot use the same serial port."); }
            DateTimeOffset? since = null;
            if (IsAuxiliaryAlge)
            {
                if (string.IsNullOrWhiteSpace(AuxiliaryFromUtc)) { since = DateTimeOffset.UtcNow; }
                else if (DateTimeOffset.TryParse(AuxiliaryFromUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) { since = parsed; }
                else { throw new DomainValidationException("Enter a valid ALGE receive-from date/time in UTC."); }
            }
            var endpoint = IsAuxiliaryUsb ? "Timy USB " + AuxiliaryUsbId.Trim() : IsAuxiliarySerial ? AuxiliaryPort.Trim()
                : IsAuxiliaryAlge ? $"{AuxiliaryStartDevice.Trim()}/{AuxiliaryStartChannel};{AuxiliaryFinishDevice.Trim()}/{AuxiliaryFinishChannel}" : "Replay";
            var channel = AuxiliaryStartChannel;
            var options = new CaptureOptions(AuxiliarySource, endpoint, date, channel,
                IsAuxiliaryB ? AuxiliaryFinishChannel : channel, IsAuxiliaryReplay, AuxiliaryFirmware,
                AuxiliaryStartDevice.Trim(), IsAuxiliaryB ? AuxiliaryFinishDevice.Trim() : AuxiliaryStartDevice.Trim(), since)
                { BaudRate = AuxiliaryBaud, ComparisonUtcOffsetMinutes = AuxiliaryClockOffsetMinutes };
            AuxiliaryTimingValidation.Validate(AuxiliaryRole, options);
            ValidateAuxiliaryTimingEndpoint(options, AuxiliaryRole);
            ITimingSource source;
            if (IsAuxiliaryUsb) { source = new TimyUsbSource(AuxiliaryUsbId.Trim()); }
            else if (IsAuxiliarySerial) { source = new SerialTimingSource(AuxiliaryPort.Trim(), AuxiliaryBaud); }
            else if (IsAuxiliaryAlge)
            {
                var credential = new WindowsCredentialStore("OpenSkiTime.ALGE.Results.Password:" + AuxiliaryUsername.Trim());
                var password = AuxiliaryPassword;
                if (password.Length == 0 && OperatingSystem.IsWindows()) { password = credential.Read() ?? ""; }
                if (string.IsNullOrWhiteSpace(AuxiliaryUsername) || password.Length == 0)
                { throw new DomainValidationException("Enter the ALGE Results username and password."); }
                if (OperatingSystem.IsWindows()) { if (AuxiliaryRememberPassword) { credential.Save(password); } else { credential.Remove(); } }
                source = new AlgeResultsSource(_timingHttp, AuxiliaryUsername.Trim(), password, options);
                AuxiliaryPassword = "";
            }
            else
            {
                if (!File.Exists(AuxiliaryReplayPath)) { throw new DomainValidationException("Enter an existing raw ALGE ASCII file path."); }
                source = new ReplayFileTimingSource(AuxiliaryReplayPath);
            }
            await auxiliary.StartAsync(listId.Value, AuxiliaryRole, source, options, TimingOperator, AuxiliaryLive && IsAuxiliaryB);
            if (IsAuxiliaryB) { AuxiliaryRunSwitchError = ""; }
            RefreshAuxiliaryConnectionState();
            _auxiliaryUiTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshAuxiliaryConnectionState());
            _auxiliaryUiTimer.Start();
            SetStatus("Auxiliary capture connected. It cannot change race times or results.");
        });
        IsAuxiliaryBusy = false;
    }

    [RelayCommand]
    private async Task DisconnectAuxiliaryAsync()
    {
        IsAuxiliaryBusy = true;
        await GuardAsync(async () => { if (workspace.Auxiliary is { } auxiliary) { await auxiliary.StopAsync(AuxiliaryRole); } });
        if (AuxiliaryRole == AuxiliaryTimingRole.B && workspace.Auxiliary?.State(AuxiliaryTimingRole.B).IsActive != true)
        { AuxiliaryRunSwitchError = ""; }
        RefreshAuxiliaryConnectionState(); IsAuxiliaryBusy = false;
    }

    [RelayCommand]
    private void RetryAuxiliaryStorage() { workspace.Auxiliary?.RetryStorage(AuxiliaryRole); RefreshAuxiliaryConnectionState(); }

    internal void RefreshAuxiliaryConnectionState()
    {
        var state = workspace.Auxiliary?.State(AuxiliaryRole);
        IsAuxiliaryConnected = state?.IsActive == true;
        AuxiliaryConnectionStatus = state is null ? "Optional backup capture is disconnected."
            : $"{state.Connection} · {state.SavedPackets} saved packets · {state.Pending} pending"
                + (string.IsNullOrWhiteSpace(state.Fault) ? "" : " · " + state.Fault);
        NotifyAuxiliaryConnection();
        RefreshBackupMonitor();
    }

    private void QueueLiveBackupRunSwitch(AuxiliaryTimingWorkspace auxiliary, Guid listId)
    {
        AuxiliaryRunSwitchError = "B is changing runs. Backup comparison is unavailable until the new run is connected.";
        // The helper catches its own failures. Optional backup storage or a fragmented B message must never hold up A.
        _ = SwitchLiveBackupRunAsync(auxiliary, listId);
    }

    private async Task SwitchLiveBackupRunAsync(AuxiliaryTimingWorkspace auxiliary, Guid listId)
    {
        string error = "";
        try { await auxiliary.SwitchRunAsync(AuxiliaryTimingRole.B, listId); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { error = "B did not change runs. Check the backup connection: " + ex.Message; }
        if (!ReferenceEquals(workspace.Auxiliary, auxiliary) || workspace.Timing?.ListId != listId) { return; }
        AuxiliaryRunSwitchError = error; RefreshAuxiliaryConnectionState();
    }

    private void DisposeAuxiliaryUi() { _auxiliaryUiTimer?.Stop(); }

    private void ValidateAuthoritativeTimingEndpoint(CaptureOptions options)
    {
        if (workspace.Auxiliary is not { } auxiliary) { return; }
        foreach (var role in AuxiliaryRoles)
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
        foreach (var other in AuxiliaryRoles.Where(x => x != role))
        {
            var state = auxiliary.State(other);
            if (state is { IsActive: true, Options: { } active } && SameLocalTimingEndpoint(options, active))
            { throw new DomainValidationException($"The selected device is already connected as {other}. Disconnect that role before temporarily reusing its device."); }
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

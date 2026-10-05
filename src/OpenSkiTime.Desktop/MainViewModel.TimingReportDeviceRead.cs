using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

public sealed record TimingReportDeviceRow(string Time, string Position, string Kind, string RawText, string Provenance);

// "Timing device" input of the Read timing observations dialog. The device is read in dialog memory only:
// nothing is persisted to the series or to preferences, and live A / B capture and role settings are never changed.
public sealed partial class MainViewModel
{
    public const string ReportImageInputMode = "Image / OCR";
    public const string ReportDeviceInputMode = "Timing device";
    private const int ReportDeviceRowLimit = 2000;
    private const string ReportDeviceIdleStatus = "Choose the device and channels, then press Connect / Read. Nothing is saved until you press OK.";
    private TimingDeviceRead? _reportDeviceRead;
    private TimingConnection? _reportDeviceConnection;
    private Task? _reportDeviceMonitor;
    private Task _reportDeviceStopping = Task.CompletedTask;
    internal TimeSpan ReportDevicePollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    // Completes when the current device read has stopped and its observations were matched (tests and shutdown).
    internal Task ReportDeviceReadTask => _reportDeviceMonitor ?? Task.CompletedTask;

    public IReadOnlyList<string> ReportInputModes { get; } = [ReportImageInputMode, ReportDeviceInputMode];
    public IReadOnlyList<string> ReportDeviceSources { get; } = TimingSourceTypes.DeviceRead.Select(TimingSourceTypes.Label).ToArray();
    public IReadOnlyList<int> ReportDeviceChannels { get; } = Enumerable.Range(0, 9).ToArray();
    public IReadOnlyList<int> ReportDeviceBauds { get; } = [9600, 19200, 38400, 57600, 115200];
    public ObservableCollection<string> ReportDevicePorts { get; } = [];
    public ObservableCollection<TimingReportDeviceRow> ReportDeviceObservations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReportImageInput))]
    [NotifyPropertyChangedFor(nameof(IsReportDeviceInput))]
    private string _reportInputMode = ReportImageInputMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReportDeviceUsb))]
    [NotifyPropertyChangedFor(nameof(IsReportDeviceSerial))]
    [NotifyPropertyChangedFor(nameof(IsReportDeviceAlge))]
    [NotifyPropertyChangedFor(nameof(IsReportDeviceReplay))]
    private string _reportDeviceSource = TimingSourceTypes.TimyUsbLabel;
    [ObservableProperty] private string _reportDeviceUsbId = "";
    [ObservableProperty] private string _reportDevicePort = "";
    [ObservableProperty] private int _reportDeviceBaud = 38400;
    [ObservableProperty] private string _reportDeviceAlgeStartDevice = "";
    [ObservableProperty] private string _reportDeviceAlgeFinishDevice = "";
    [ObservableProperty] private string _reportDeviceAlgeUsername = "";
    [ObservableProperty] private string _reportDeviceAlgePassword = "";
    [ObservableProperty] private string _reportDeviceAlgeStatus = "";
    [ObservableProperty] private bool _isReportDeviceAlgeFetching;
    // Devices of the entered ALGE Results account, fetched on request. IDs can still be typed without a list.
    public ObservableCollection<AlgeResultsDevice> ReportDeviceAlgeDevices { get; } = [];
    public bool HasReportDeviceAlgeDevices => ReportDeviceAlgeDevices.Count != 0;
    public AlgeResultsDevice? SelectedReportDeviceAlgeStart
    {
        get => ReportDeviceAlgeDevices.FirstOrDefault(x => x.Id == ReportDeviceAlgeStartDevice.Trim());
        set { if (value is not null) { ReportDeviceAlgeStartDevice = value.Id; } }
    }
    public AlgeResultsDevice? SelectedReportDeviceAlgeFinish
    {
        get => ReportDeviceAlgeDevices.FirstOrDefault(x => x.Id == ReportDeviceAlgeFinishDevice.Trim());
        set { if (value is not null) { ReportDeviceAlgeFinishDevice = value.Id; } }
    }
    partial void OnReportDeviceAlgeStartDeviceChanged(string value) => OnPropertyChanged(nameof(SelectedReportDeviceAlgeStart));
    partial void OnReportDeviceAlgeFinishDeviceChanged(string value) => OnPropertyChanged(nameof(SelectedReportDeviceAlgeFinish));
    partial void OnReportDeviceAlgeUsernameChanged(string value)
    { ReportDeviceAlgeDevices.Clear(); ReportDeviceAlgeStatus = ""; OnPropertyChanged(nameof(HasReportDeviceAlgeDevices)); }

    [RelayCommand]
    private async Task FetchReportDeviceAlgeDevicesAsync()
    {
        if (IsReportDeviceAlgeFetching) { return; }
        var username = ReportDeviceAlgeUsername.Trim();
        var password = new AlgeAccountEditor(username) { Password = ReportDeviceAlgePassword }.PeekPassword();
        if (username.Length == 0 || password.Length == 0) { ReportDeviceAlgeStatus = "Enter the username and password first."; return; }
        IsReportDeviceAlgeFetching = true; ReportDeviceAlgeStatus = "Fetching devices…";
        try
        {
            var devices = await AlgeResultsSource.ListDevicesAsync(_timingHttp, username, password);
            ReportDeviceAlgeDevices.Clear(); foreach (var device in devices) { ReportDeviceAlgeDevices.Add(device); }
            ReportDeviceAlgeStatus = devices.Count == 0 ? "No devices in this account." : devices.Count.ToString(CultureInfo.InvariantCulture) + " device(s)";
            OnPropertyChanged(nameof(HasReportDeviceAlgeDevices));
            OnPropertyChanged(nameof(SelectedReportDeviceAlgeStart)); OnPropertyChanged(nameof(SelectedReportDeviceAlgeFinish));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or InvalidOperationException or KeyNotFoundException)
        { ReportDeviceAlgeStatus = "Devices could not be fetched: " + ex.Message; }
        finally { IsReportDeviceAlgeFetching = false; }
    }
    [ObservableProperty] private bool _reportDeviceRememberPassword;
    [ObservableProperty] private string _reportDeviceReplayPath = "";
    [ObservableProperty] private int _reportDeviceStartChannel;
    [ObservableProperty] private int _reportDeviceFinishChannel = 1;
    [ObservableProperty] private int _reportDeviceHandChannel;
    [ObservableProperty] private string _reportDeviceDate = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditReportDevice))]
    private bool _isReportDeviceReading;
    [ObservableProperty] private string _reportDeviceStatus = ReportDeviceIdleStatus;
    [ObservableProperty] private string _reportImportWarnings = "";

    public bool IsReportImageInput => ReportInputMode != ReportDeviceInputMode;
    public bool IsReportDeviceInput => ReportInputMode == ReportDeviceInputMode;
    private TimingSourceType ReportDeviceSourceType => TimingSourceTypes.Parse(ReportDeviceSource) ?? TimingSourceType.TimyUsb;
    public bool IsReportDeviceUsb => ReportDeviceSourceType == TimingSourceType.TimyUsb;
    public bool IsReportDeviceSerial => ReportDeviceSourceType == TimingSourceType.Mt1Serial;
    public bool IsReportDeviceAlge => ReportDeviceSourceType == TimingSourceType.AlgeResults;
    public bool IsReportDeviceReplay => ReportDeviceSourceType == TimingSourceType.ReplayFile;
    public bool IsReportDeviceB => ReportImageRole == TimingReportImageRole.B;
    public bool IsReportDeviceHand => !IsReportDeviceB;
    public bool CanEditReportDevice => !IsReportDeviceReading;

    private void NotifyReportDeviceRole() { OnPropertyChanged(nameof(IsReportDeviceB)); OnPropertyChanged(nameof(IsReportDeviceHand)); }

    partial void OnReportInputModeChanged(string value)
    {
        // Switching input discards the other input's in-memory evidence and any unaccepted proposals.
        _reportImageCancellation?.Cancel(); ResetReportDeviceRead(); ClearReportPreview();
        ReportImages.Clear(); SelectedReportImage = null;
        if (value == ReportDeviceInputMode)
        {
            PrepareReportDeviceDefaults();
            ReportImportStatus = "Read timestamps from a timing device into this dialog. Review the matched timestamps, then press OK.";
        }
        else { ReportImportStatus = "Open images, drop images here, or paste an image (Ctrl+V / Ctrl+C). Review the matched timestamps, then press OK."; }
    }

    private void PrepareReportDeviceDefaults()
    {
        if (_reportDraft is { } draft)
        {
            ReportDeviceDate = draft.Header.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (ReportDevicePorts.Count == 0) { RefreshReportDevicePorts(); }
    }

    [RelayCommand]
    private void RefreshReportDevicePorts()
    {
        try
        {
            var selected = ReportDevicePort ?? "";
            ReportDevicePorts.Clear();
            foreach (var port in SerialTimingSource.PortNames()) { ReportDevicePorts.Add(port); }
            ReportDevicePort = selected.Length != 0 ? selected : ReportDevicePorts.FirstOrDefault() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        { ReportDeviceStatus = "Serial ports could not be listed: " + ex.Message; }
    }

    // Builds the transient read configuration. Only fields of the selected source are used.
    internal (TimingConnection Connection, CaptureOptions Options, AuxiliaryTimingRole Role) BuildReportDeviceRead()
    {
        var source = TimingSourceTypes.Parse(ReportDeviceSource);
        if (source is not { } type || !TimingSourceTypes.DeviceRead.Contains(type))
        { throw new DomainValidationException("Choose a timing device source."); }
        if (!DateOnly.TryParseExact(ReportDeviceDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        { throw new DomainValidationException("Device date must use YYYY-MM-DD."); }
        var role = ReportImageRole switch
        {
            TimingReportImageRole.B => AuxiliaryTimingRole.B,
            TimingReportImageRole.HandStart => AuxiliaryTimingRole.HandStart,
            _ => AuxiliaryTimingRole.HandFinish
        };
        var b = role == AuxiliaryTimingRole.B;
        var startChannel = b ? ReportDeviceStartChannel : ReportDeviceHandChannel;
        var finishChannel = b ? ReportDeviceFinishChannel : ReportDeviceHandChannel;
        var alge = type == TimingSourceType.AlgeResults;
        var startDevice = ReportDeviceAlgeStartDevice.Trim();
        var finishDevice = b && ReportDeviceAlgeFinishDevice.Trim().Length != 0 ? ReportDeviceAlgeFinishDevice.Trim() : startDevice;
        DateTimeOffset? fromUtc = null;
        if (alge)
        {
            if (startDevice.Length == 0 || !startDevice.All(char.IsAsciiDigit) || !finishDevice.All(char.IsAsciiDigit))
            { throw new DomainValidationException("Enter the ALGE Results device ID (digits only)."); }
            // Read the whole device day: a bound 14 hours before the date covers every device clock setting. The decoder keeps
            // only triggers whose device time falls on the chosen device date.
            fromUtc = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddHours(-14);
        }
        if (type == TimingSourceType.Mt1Serial && (ReportDevicePort ?? "").Trim().Length == 0)
        { throw new DomainValidationException("Choose the MT1 COM port."); }
        if (type == TimingSourceType.ReplayFile && !File.Exists(ReportDeviceReplayPath.Trim()))
        { throw new DomainValidationException("Enter the path to an existing raw ALGE ASCII file."); }
        var connection = new TimingConnection(type)
        {
            UsbId = type == TimingSourceType.TimyUsb ? ReportDeviceUsbId.Trim() : "",
            Port = type == TimingSourceType.Mt1Serial ? (ReportDevicePort ?? "").Trim() : "",
            BaudRate = type == TimingSourceType.Mt1Serial ? ReportDeviceBaud : 38400,
            AlgeDeviceId = alge ? startDevice : "",
            AlgeUsername = alge ? ReportDeviceAlgeUsername.Trim() : "",
            ReplayPath = type == TimingSourceType.ReplayFile ? ReportDeviceReplayPath.Trim() : ""
        };
        connection.Validate("Timing device");
        var endpoint = alge ? $"{startDevice}/{startChannel};{finishDevice}/{finishChannel}" : connection.Endpoint;
        var options = new CaptureOptions(connection.SourceLabel, endpoint, date, startChannel, finishChannel,
            type == TimingSourceType.ReplayFile, "Not queried", alge ? startDevice : null, alge ? finishDevice : null, fromUtc)
        { BaudRate = connection.BaudRate };
        AuxiliaryTimingValidation.Validate(role, options);
        return (connection, options, role);
    }

    // A device endpoint used by live capture is never opened a second time from this dialog.
    private void ValidateReportDeviceEndpoint(CaptureOptions options)
    {
        if (workspace.Timing is { IsActive: true } timing && timing.ActiveCaptureOptions.Any(live => SameLocalTimingEndpoint(options, live)))
        { throw new DomainValidationException("This device is in use by live timing (A). Choose another device, or read it after live capture is disconnected."); }
        if (workspace.Auxiliary is not { } auxiliary) { return; }
        foreach (var role in Enum.GetValues<AuxiliaryTimingRole>())
        {
            var state = auxiliary.State(role);
            if (state.IsActive && state.AllOptions.Any(active => SameLocalTimingEndpoint(options, active)))
            {
                throw new DomainValidationException((role == AuxiliaryTimingRole.B ? "This device is in use by live B Clock capture."
                    : $"This device is in use by {role} capture.") + " Choose another device, or read it after that capture is disconnected.");
            }
        }
    }

    [RelayCommand]
    private async Task ReadReportDeviceAsync()
    {
        if (IsReportBusy || IsReportDeviceReading || _reportDraft is null) { return; }
        var password = ReportDeviceAlgePassword;
        ResetReportDeviceRead(); ClearReportPreview();
        IsReportDeviceReading = true; // Also blocks a second Connect / Read while the previous source is released.
        var started = false;
        try
        {
            await _reportDeviceStopping; // A previous read in this dialog releases its port before the next read opens it.
            await GuardAsync(() =>
            {
                var (connection, options, role) = BuildReportDeviceRead();
                ValidateReportDeviceEndpoint(options);
                var remember = ReportDeviceRememberPassword;
                var source = TimingSourceFactory.Create(connection, options, _timingHttp,
                    username => new AlgeAccountEditor(username) { Password = password, RememberPassword = remember }.TakePassword());
                TimingDeviceRead read;
                try { read = new TimingDeviceRead(source, role, options); }
                catch { _ = source.DisposeAsync().AsTask(); throw; }
                _reportDeviceRead = read; _reportDeviceConnection = connection;
                read.Start(); started = true;
                ReportDeviceStatus = $"Reading {connection.SourceLabel} · {connection.Summary} in this dialog only. Press Stop when the device has sent its timestamps.";
                ReportImportStatus = ReportDeviceStatus;
                _reportDeviceMonitor = MonitorReportDeviceReadAsync(read);
                return Task.CompletedTask;
            });
        }
        finally
        {
            if (!started)
            {
                IsReportDeviceReading = false;
                if (IsError) { ReportDeviceStatus = ReportImportStatus = StatusMessage; }
            }
        }
    }

    [RelayCommand]
    private async Task StopReportDeviceAsync()
    {
        if (_reportDeviceRead is not { } read) { return; }
        await read.StopAsync();
        await ReportDeviceReadTask;
    }

    private async Task MonitorReportDeviceReadAsync(TimingDeviceRead read)
    {
        var shown = -1;
        while (true)
        {
            var completed = await Task.WhenAny(read.Completion, Task.Delay(ReportDevicePollInterval)) == read.Completion;
            if (!ReferenceEquals(_reportDeviceRead, read)) { return; }
            ReportDeviceStatus = read.Status + $" · {read.PacketCount} packet(s) received in memory";
            if (!completed && read.PacketCount != shown)
            {
                shown = read.PacketCount;
                var live = await Task.Run(() => read.Decode(new AlgeDecoderFactory()));
                if (!ReferenceEquals(_reportDeviceRead, read)) { return; }
                ShowReportDeviceObservations(read, live);
            }
            if (completed) { break; }
        }
        IsReportDeviceReading = false;
        await PreviewReportDeviceAsync(read);
    }

    private void ShowReportDeviceObservations(TimingDeviceRead read, IReadOnlyList<AuxiliaryTimingObservation> observations)
    {
        var provenance = ReportDeviceProvenance(read);
        ReportDeviceObservations.Clear();
        foreach (var item in observations.TakeLast(ReportDeviceRowLimit))
        {
            var o = item.Observation;
            var time = o.DeviceTicks is { } ticks ? TimingReportXml.FormatStamp(new(ticks, o.Precision)) + (o.ClockId == "UTC" ? " UTC" : "") : "";
            var position = o.Channel switch { 0 => "Start", 1 => "Finish", null => "", _ => "Not mapped" };
            ReportDeviceObservations.Add(new(time, position, o.Kind.ToString(), o.Message, $"{provenance}:{o.Key}"));
        }
    }

    private static string ReportDeviceProvenance(TimingDeviceRead read) => DeviceEvidence.Provenance(read.Options.Device, read.Options.Endpoint);

    // Human-readable provenance for the accepted change reason, e.g. "device import (MT1 · USB / serial, COM3)".
    private string ReportDeviceImportDescription(TimingDeviceRead read)
    {
        var detail = _reportDeviceConnection is { Source: TimingSourceType.ReplayFile } replay
            ? Path.GetFileName(replay.ReplayPath) : read.Options.Endpoint;
        return $"device import ({read.Options.Device}, {detail})";
    }

    private async Task PreviewReportDeviceAsync(TimingDeviceRead read)
    {
        if (_reportDraft is null || !ReferenceEquals(_reportDeviceRead, read)) { return; }
        if (IsReportBusy)
        { ReportImportStatus = "The report is busy. Press Connect / Read again to match the device timestamps."; return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        try
        {
            await GuardAsync(async () =>
            {
                var observations = await Task.Run(() => read.Decode(new AlgeDecoderFactory()));
                if (!ReferenceEquals(_reportDeviceRead, read)) { return; }
                ShowReportDeviceObservations(read, observations);
                var provenance = ReportDeviceProvenance(read);
                var evidence = DeviceEvidence.ToEvidence(observations, provenance).Evidence;
                if (!await PreviewReportEvidenceCoreAsync(evidence, ReportDeviceImportDescription(read))) { return; }
                var impulses = observations.Count(x => x.Observation.Kind == ObservationKind.Impulse);
                var invalid = observations.Count(x => x.Observation.Kind == ObservationKind.Invalid);
                ReportImportStatus = $"{read.PacketCount} packet(s) read from {read.Options.Device} in memory; {impulses} impulse(s) decoded; "
                    + $"{ReportImportPreview.Count(x => x.CanAccept)} report timestamps matched. Check selected timestamps before pressing OK. Unmatched fields remain unchanged.";
                if (invalid > 0) { ReportImportStatus += $" {invalid} device line(s) were invalid or incomplete; review the raw lines."; }
                if (read.Truncated) { ReportImportStatus += " The read limit was reached; later device input was not read."; }
                if (read.Fault is { } fault) { ReportImportStatus += " Device read failed: " + fault; }
                ReportDeviceStatus = read.Status + $" · {read.PacketCount} packet(s) received in memory";
                SetStatus(ReportImportStatus);
            });
        }
        finally { IsReportBusy = false; }
        if (IsError) { ReportImportStatus = StatusMessage; }
    }

    // Stops the source and discards all packets read in this dialog. Nothing was persisted.
    private void ResetReportDeviceRead()
    {
        var read = _reportDeviceRead;
        _reportDeviceRead = null; _reportDeviceConnection = null; _reportDeviceMonitor = null;
        IsReportDeviceReading = false; ReportDeviceObservations.Clear(); ReportDeviceAlgePassword = "";
        ReportDeviceStatus = ReportDeviceIdleStatus;
        if (read is null) { return; }
        var previous = _reportDeviceStopping;
        _reportDeviceStopping = DisposeReportDeviceReadAsync(previous, read);
    }

    private static async Task DisposeReportDeviceReadAsync(Task previous, TimingDeviceRead read)
    {
        await previous;
        await read.DisposeAsync();
    }
}

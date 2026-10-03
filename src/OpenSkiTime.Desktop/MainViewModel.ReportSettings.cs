using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private readonly TimingReportDefaultsStore _reportDefaultsStore = reportDefaultsStore ?? new();
    private readonly FisTimingDeviceCache _timingDeviceCache = timingDeviceCache ?? new();
    private TimingReportDefaults? _reportDefaults;
    private bool _reportSettingsLoaded;
    private DateTimeOffset? _timingDeviceCatalogueRetrievedAt;
    public TimingReportDefaults ReportDefaults { get { EnsureReportSettingsLoaded(); return _reportDefaults ?? new(); } }
    [ObservableProperty] private int _selectedSettingsTab;
    [RelayCommand] private void ShowTimingReportSettings() { SelectedSettingsTab = 2; ShowSettings(); }
    public TimingReportPersonEditor ReportDefaultChief { get; } = new();
    public TimingReportPersonEditor ReportDefaultTimekeeper { get; } = new();
    public IReadOnlyList<string> ReportConnectionModes { get; } = ["Cable", "Radio", "LAN", "WLAN", "Mobile", "USB", "Other"];
    public ObservableCollection<TimingReportDeviceEditor> ReportDefaultDevices { get; } =
        [new("Timer A", 1), new("Timer B", 1), new("Start gate", 10), new("Start clock", 0), new("Finish cells A", 20), new("Finish cells B", 20),
            new("Start timer A (optional)", 1), new("Start timer B (optional)", 1)];
    public ObservableCollection<FisTimingDevice> TimingDeviceHomologations { get; } = [];
    [ObservableProperty] private TimingReportDeviceEditor? _selectedReportDefaultDevice;
    [ObservableProperty] private FisTimingDevice? _selectedTimingDeviceHomologation;
    [ObservableProperty] private bool _isTimingDeviceLookupBusy;
    [ObservableProperty] private string _timingDeviceCatalogueStatus = "No catalogue downloaded. Equipment can be entered manually.";
    [ObservableProperty] private string _reportDefaultsStatus = "Timing reports use the equipment and officials saved here.";
    [ObservableProperty] private string _reportDefaultConnectionA = "Cable";
    [ObservableProperty] private string _reportDefaultConnectionB = "Cable";
    [ObservableProperty] private string _reportDefaultVoice = "Radio";

    internal void EnsureReportSettingsLoaded()
    {
        if (_reportSettingsLoaded) { return; }
        _reportSettingsLoaded = true;
        EnsureAuxiliarySettingsLoaded();
        try { _reportDefaults = _reportDefaultsStore.Load(); LoadReportDefaultsEditors(_reportDefaults); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DomainValidationException)
        { ReportDefaultsStatus = "Defaults could not be loaded: " + ex.Message; }
        try { if (_timingDeviceCache.Load() is { } catalogue) { ShowTimingDeviceCatalogue(catalogue); } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DomainValidationException)
        { TimingDeviceCatalogueStatus = ex.Message; }
    }

    private void LoadReportDefaultsEditors(TimingReportDefaults d)
    {
        ReportDefaultChief.Load(d.ChiefOfTiming); ReportDefaultTimekeeper.Load(d.Timekeeper);
        var devices = new[] { d.TimerA, d.TimerB, d.StartDevice, d.StartClock, d.FinishCellsA, d.FinishCellsB, d.TimerStartA ?? new(), d.TimerStartB ?? new() };
        for (var i = 0; i < devices.Length; i++) { ReportDefaultDevices[i].Load(devices[i]); }
        ReportDefaultConnectionA = d.ConnectionA; ReportDefaultConnectionB = d.ConnectionB; ReportDefaultVoice = d.Voice;
        SelectedReportDefaultDevice ??= ReportDefaultDevices[0];
    }

    [RelayCommand]
    private void SaveTimingReportDefaults()
    {
        try
        {
            _reportDefaults = _reportDefaultsStore.Save(new() { ChiefOfTiming = ReportDefaultChief.Values,
                Timekeeper = ReportDefaultTimekeeper.Values, TimerA = ReportDefaultDevices[0].Values,
                TimerB = ReportDefaultDevices[1].Values, StartDevice = ReportDefaultDevices[2].Values,
                StartClock = ReportDefaultDevices[3].Values, FinishCellsA = ReportDefaultDevices[4].Values,
                FinishCellsB = ReportDefaultDevices[5].Values, ConnectionA = ReportDefaultConnectionA,
                TimerStartA = OptionalReportDevice(ReportDefaultDevices[6].Values),
                TimerStartB = OptionalReportDevice(ReportDefaultDevices[7].Values),
                ConnectionB = ReportDefaultConnectionB, Voice = ReportDefaultVoice });
            LoadReportDefaultsEditors(_reportDefaults);
            if (_reportDraft is not null)
            {
                _reportDraft = _reportDraft with { Defaults = _reportDefaults };
                NotifyReportDefaults(); ReportChanged();
            }
            ReportDefaultsStatus = "Saved. Timing reports use the equipment and officials from Settings.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DomainValidationException)
        { ReportDefaultsStatus = "Not saved: " + ex.Message; }
    }

    private static TimingReportDevice? OptionalReportDevice(TimingReportDevice value)
        => value.Brand.Length + value.Model.Length + value.Serial.Length + value.Homologation.Length == 0 ? null : value;

    [RelayCommand]
    private async Task RefreshTimingDeviceHomologationsAsync()
    {
        if (IsTimingDeviceLookupBusy) { return; }
        IsTimingDeviceLookupBusy = true;
        try
        {
            var devices = await new FisTimingDeviceClient(_informationHttp).GetAsync(_fisStore.ReadApiKey() ?? "");
            var catalogue = new FisTimingDeviceCatalogue(DateTimeOffset.UtcNow, devices.ToArray());
            _timingDeviceCache.Save(catalogue); ShowTimingDeviceCatalogue(catalogue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DomainValidationException
            or HttpRequestException or OperationCanceledException or PlatformNotSupportedException)
        { TimingDeviceCatalogueStatus = ex is OperationCanceledException ? "Lookup timed out. Cached equipment is still available." : ex.Message; }
        finally { IsTimingDeviceLookupBusy = false; }
    }

    private void ShowTimingDeviceCatalogue(FisTimingDeviceCatalogue catalogue)
    {
        _timingDeviceCatalogueRetrievedAt = catalogue.RetrievedAt;
        TimingDeviceHomologations.Clear();
        foreach (var device in catalogue.Devices) { TimingDeviceHomologations.Add(device); }
        TimingDeviceCatalogueStatus = $"{catalogue.Devices.Length} homologations cached · {catalogue.RetrievedAt:yyyy-MM-dd HH:mm} UTC. Check validity for the race season.";
    }

    [RelayCommand]
    private void ApplyTimingDeviceHomologation()
    {
        if (SelectedReportDefaultDevice is not { } editor || SelectedTimingDeviceHomologation is not { } device) { return; }
        if (editor.Category != device.CategoryId)
        { TimingDeviceCatalogueStatus = "Choose a homologation matching the selected equipment type."; return; }
        editor.Load(new(device.CompanyName, device.Name, editor.Serial, device.HomologationCode)
            { ValidUntilSeason = device.ValidUntilSeason, HomologationRetrievedAt = _timingDeviceCatalogueRetrievedAt });
        TimingDeviceCatalogueStatus = $"Applied to {editor.Label}. Check serial number and save defaults.";
    }
}

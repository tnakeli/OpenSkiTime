using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

// Training simulator of the Timing view: the simulation clock and B Clock test impulses (A impulses: SimulatePulseAsync).
// While the clock runs, a test impulse takes the PC's local time of day at the moment of the press, with full tick
// precision; the text box only displays that clock (tenths, refreshed at 10 Hz). Pausing freezes the exact time in the
// box, and typing a time pauses the clock, so the typed value is used and never overwritten by the running display.
public sealed partial class MainViewModel
{
    private readonly TimeProvider _simulationClock = timeProvider ?? TimeProvider.System;
    private DispatcherTimer? _simulationClockTimer;
    private string _simulationTime = "12:00:00.0000";
    private bool _isSimulationClockRunning = true;
    private SimulatorTimingSource? _backupSimulator;

    // Operator input. A changed value pauses the running clock first.
    public string SimulationTime
    {
        get => _simulationTime;
        set
        {
            if (value is null || value == _simulationTime) { return; }
            IsSimulationClockRunning = false;
            SetProperty(ref _simulationTime, value);
        }
    }

    public bool IsSimulationClockRunning
    {
        get => _isSimulationClockRunning;
        private set
        {
            if (!SetProperty(ref _isSimulationClockRunning, value)) { return; }
            OnPropertyChanged(nameof(SimulationClockToggleText));
            UpdateSimulationClockTimer();
        }
    }

    public string SimulationClockToggleText => IsSimulationClockRunning ? "⏸ Pause" : "▶ Live";
    public bool IsBackupTimingSimulator => BackupTimingRoles.Any(x => x.EffectiveSourceType == TimingSourceType.Simulator);
    public bool ShowTimingSimulatorControls => IsTimingSimulator || IsBackupTimingSimulator;

    [RelayCommand]
    private void ToggleSimulationClock()
    {
        if (!IsSimulationClockRunning)
        {
            IsSimulationClockRunning = true;
            RefreshSimulationClock();
            return;
        }
        var now = SimulationClockTicks();
        IsSimulationClockRunning = false;
        ShowSimulationTime(TimingTime.FormatTimeOfDay(now));
    }

    // B Clock test impulse on the live B simulator session. It feeds the auxiliary B capture only, never A timing.
    [RelayCommand]
    private async Task SimulateBackupPulseAsync(string channel)
    {
        await GuardAsync(async () =>
        {
            var state = workspace.Auxiliary?.State(AuxiliaryTimingRole.B);
            var options = state is { IsActive: true, Live: true }
                ? state.AllOptions.FirstOrDefault(x => x.Device == TimingSourceTypes.SimulatorLabel) : null;
            if (_backupSimulator is null || options is null)
            { throw new DomainValidationException("Connect timing with the B Clock simulator first. B connects with timing."); }
            var ticks = SimulationTicks();
            var position = channel switch
            {
                "start" => 0,
                "finish" => 1,
                _ => throw new DomainValidationException("Choose B Clock start or finish.")
            };
            // Pulses use the physical channel the live B simulator session maps to this role.
            var physical = options.Channel(position)
                ?? throw new DomainValidationException("This B Clock role is not assigned to the simulator in Settings.");
            await _backupSimulator.PulseAsync(physical, ticks);
        });
    }

    // The impulse time: the PC clock at the press while running, otherwise the typed device time.
    private long SimulationTicks()
    {
        if (IsSimulationClockRunning) { return SimulationClockTicks(); }
        if (!TimingTime.TryTimeOfDay(SimulationTime, out var ticks, out _))
        { throw new DomainValidationException("Enter simulator time as HH:mm:ss with 1–7 decimal places (for example 12:00:00.1234567)."); }
        return ticks;
    }

    private long SimulationClockTicks() => _simulationClock.GetLocalNow().TimeOfDay.Ticks;

    internal void RefreshSimulationClock()
    {
        if (!IsSimulationClockRunning) { return; }
        ShowSimulationTime(new TimeOnly(SimulationClockTicks()).ToString("HH:mm:ss.f", CultureInfo.InvariantCulture));
    }

    // Display update from the clock; unlike the operator setter it never pauses.
    private void ShowSimulationTime(string text)
    {
        if (text == _simulationTime) { return; }
        _simulationTime = text;
        OnPropertyChanged(nameof(SimulationTime));
    }

    // The display timer runs only while the clock runs and simulator controls are configured, and skips work outside
    // the Timing view.
    private void UpdateSimulationClockTimer()
    {
        if (IsSimulationClockRunning && ShowTimingSimulatorControls)
        {
            if (_simulationClockTimer is null)
            {
                _simulationClockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
                _simulationClockTimer.Tick += (_, _) => { if (IsTimingSection) { RefreshSimulationClock(); } };
            }
            if (!_simulationClockTimer.IsEnabled) { RefreshSimulationClock(); _simulationClockTimer.Start(); }
        }
        else { _simulationClockTimer?.Stop(); }
    }
}

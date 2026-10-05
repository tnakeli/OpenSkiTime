using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

// Training simulator clock of the Timing view.
// While the clock runs, a test impulse takes the PC's local time of day at the moment of the press, with full tick
// precision; the text box only displays that clock (tenths, refreshed at 10 Hz). Pausing freezes the exact time in the
// box, and typing a time pauses the clock, so the typed value is used and never overwritten by the running display.
public sealed partial class MainViewModel
{
    private readonly TimeProvider _simulationClock = timeProvider ?? TimeProvider.System;
    private DispatcherTimer? _simulationClockTimer;
    private string _simulationTime = "12:00:00.0000";
    private bool _isSimulationClockRunning = true;

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
    public bool ShowTimingSimulatorControls => IsTimingSimulator;

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

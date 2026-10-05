using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

// Operator-entered timestamps for impulses that never arrived. Double-clicking a START, INTERM or FINISH cell in the
// TIMESTAMPS grid opens this editor; saving commits one audited manual time before the new row is shown.
public sealed partial class MainViewModel
{
    public const string DefaultManualTimestampReason = "Missing impulse entered manually";
    [ObservableProperty] private bool _showManualTimestampEditor;
    [ObservableProperty] private int _manualTimestampChannel;
    [ObservableProperty] private string _manualTimestampText = "";
    [ObservableProperty] private string _manualTimestampReason = DefaultManualTimestampReason;
    [ObservableProperty] private string _manualTimestampError = "";
    public bool HasManualTimestampError => ManualTimestampError.Length > 0;
    partial void OnManualTimestampErrorChanged(string value) => OnPropertyChanged(nameof(HasManualTimestampError));
    public string ManualTimestampPosition => TimingPositionLabel(ManualTimestampChannel);
    partial void OnManualTimestampChannelChanged(int value) => OnPropertyChanged(nameof(ManualTimestampPosition));

    public static string TimingPositionLabel(int channel) => channel == 0 ? "Start" : channel == 1 ? "Finish" : $"Interm {channel - 1}";

    // channel: 0 start, 1 finish, 2.. intermediates. The clicked cell (when it holds a timestamp) only seeds the text.
    public bool BeginManualTimestamp(int channel, TimingTimestampCell? clicked = null)
    {
        var timing = workspace.Timing;
        if (timing?.Snapshot is not { } snapshot || _timingList is null)
        { SetStatus("Choose a timing run before entering a manual time.", error: true); return false; }
        if (channel is not (0 or 1) && (channel < 2 || channel >= 2 + TimingCheckpoints.Count))
        { SetStatus("Choose a start, intermediate or finish column.", error: true); return false; }
        var seed = clicked?.Review.Observation.DeviceTicks
            ?? (timing.IsActive ? timing.LiveDeviceTicks : null)
            ?? snapshot.Observations.LastOrDefault(x => x.Observation.Kind == ObservationKind.Impulse && x.Observation.DeviceTicks is not null)?.Observation.DeviceTicks;
        ManualTimestampChannel = channel;
        // Hundredths are the usual hand/backup resolution; the operator may type up to seven decimals.
        ManualTimestampText = seed is { } ticks ? TimingTime.FormatTimeOfDay(ticks)[..11] : "";
        ManualTimestampReason = string.IsNullOrWhiteSpace(TimingReason) ? DefaultManualTimestampReason : TimingReason.Trim();
        ManualTimestampError = "";
        ShowManualTimestampEditor = true;
        return true;
    }

    [RelayCommand]
    private void CancelManualTimestamp() { ShowManualTimestampEditor = false; ManualTimestampError = ""; }

    [RelayCommand]
    private async Task SaveManualTimestampAsync()
    {
        if (!ShowManualTimestampEditor || workspace.Timing is not { } timing || timing.ListId is not { } listId) { return; }
        try
        {
            var position = ManualTimestampPosition;
            var text = ManualTimestampText.Trim();
            var key = await timing.AddManualTimestampAsync(listId, ManualTimestampChannel, text, TimingOperator, ManualTimestampReason);
            ShowManualTimestampEditor = false;
            ManualTimestampError = "";
            RefreshTiming();
            var time = timing.Snapshot?.Observations.FirstOrDefault(x => x.Observation.Key == key)?.Observation.DeviceTicks;
            SetStatus($"Manual {position} {TimingTime.FormatTimeOfDay(time)} saved with correction history. Drag a competitor onto it to assign it.");
        }
        catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException)
        {
            // Keep the editor and the typed value so the operator can correct it.
            ManualTimestampError = ex.Message;
            SetStatus(ex.Message, error: true);
        }
    }
}

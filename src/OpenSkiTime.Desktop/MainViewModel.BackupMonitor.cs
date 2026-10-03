using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

public sealed record BackupMonitorRow(int Bib, string Position, string A, string B, string Difference, string Status);

public sealed partial class MainViewModel
{
    private readonly BackupMonitor _backupMonitor = new();
    private readonly BackupDisplayWindow _backupDisplay = new();
    private IReadOnlyList<AuxiliaryTimingObservation>? _backupObservationSource;
    private TimingObservation[] _backupObservations = [];
    private bool? _backupTargetUsesUtc;
    private string[] _backupClockWarnings = [];
    public ObservableCollection<BackupMonitorRow> BackupMonitorRows { get; } = [];
    [ObservableProperty] private bool _canShowBackup;
    [ObservableProperty] private bool _showBackupTimes;
    [ObservableProperty] private string _backupWarning = "";
    [ObservableProperty] private string _backupDisplayLabel = "B system · read-only";
    [ObservableProperty] private int _backupStartWarningMilliseconds = 1;
    [ObservableProperty] private int _backupFinishWarningMilliseconds = 10;
    [ObservableProperty] private int _backupMissingGraceSeconds = 5;
    public bool HasBackupWarning => BackupWarning.Length != 0;
    public string BackupButtonText => HasBackupWarning ? "⚠ Show B" : "Show B";
    partial void OnBackupWarningChanged(string value)
    { OnPropertyChanged(nameof(HasBackupWarning)); OnPropertyChanged(nameof(BackupButtonText)); }

    [RelayCommand]
    private void ShowBackup()
    {
        if (!CanShowBackup) { return; }
        _backupDisplay.Show(DateTimeOffset.UtcNow);
        RefreshBackupMonitor();
    }

    [RelayCommand]
    private void HideBackup() { _backupDisplay.Hide(); ShowBackupTimes = false; }

    internal void ResetBackupMonitor()
    {
        _backupMonitor.Reset(); _backupDisplay.Hide(); BackupMonitorRows.Clear();
        _backupObservationSource = null; _backupObservations = [];
        _backupTargetUsesUtc = null; _backupClockWarnings = [];
        ShowBackupTimes = false; CanShowBackup = false; BackupWarning = "";
    }

    internal void RefreshBackupMonitor()
    {
        var now = DateTimeOffset.UtcNow;
        var state = workspace.Auxiliary?.State(AuxiliaryTimingRole.B);
        var a = workspace.Timing?.Snapshot;
        var active = state is { IsActive: true, Live: true } && state.ListId == a?.ListId;
        CanShowBackup = active;
        if (!active)
        {
            ResetBackupMonitor();
            BackupWarning = state is { IsActive: true, Live: true } ? AuxiliaryRunSwitchError : "";
            return;
        }
        // Cloud polling/history recovery takes longer than directly attached devices.
        var cloud = state!.Options?.Device.Contains("ALGE Results", StringComparison.Ordinal) == true;
        var grace = Math.Max(cloud ? 10m : 1m, BackupMissingGraceSeconds);
        var policy = new BackupComparisonPolicy(
            (long)(Math.Clamp(BackupStartWarningMilliseconds, 0.01m, 60000m) * TimeSpan.TicksPerMillisecond),
            (long)(Math.Clamp(BackupFinishWarningMilliseconds, 0.01m, 60000m) * TimeSpan.TicksPerMillisecond),
            MissingGraceTicks: (long)(Math.Min(grace, 300m) * TimeSpan.TicksPerSecond));
        var assignedKeys = a!.Results.SelectMany(x => new[] { x.StartKey, x.FinishKey }).Where(x => x is not null).ToHashSet(StringComparer.Ordinal);
        var contexts = a.Observations.Where(x => assignedKeys.Contains(x.Observation.Key))
            .Select(x => AuxiliaryClockComparison.UsesUtc(x.Observation)).Distinct().ToArray();
        if (contexts.Length > 1)
        {
            _backupDisplay.Hide(); ShowBackupTimes = false; CanShowBackup = false;
            BackupWarning = "A capture contains both UTC and local clock contexts. Review its source settings before comparing B.";
            return;
        }
        var targetUsesUtc = contexts.Length == 1 ? contexts[0]
            : workspace.Timing?.LastCaptureOptions?.Device.Contains("ALGE Results", StringComparison.Ordinal) == true;
        if (!ReferenceEquals(_backupObservationSource, state.Observations) || _backupTargetUsesUtc != targetUsesUtc)
        {
            _backupObservationSource = state.Observations;
            _backupTargetUsesUtc = targetUsesUtc;
            var normalized = AuxiliaryClockComparison.Normalize(targetUsesUtc, state.Observations);
            _backupObservations = normalized.Observations.ToArray();
            _backupClockWarnings = normalized.Warnings.ToArray();
        }
        var comparison = _backupMonitor.Update(state.SessionId, a, _backupObservations, now, policy);
        BackupWarning = state.Fault ?? string.Join(" · ", _backupClockWarnings.Concat(comparison.Warnings.Take(3)))
            + (comparison.Warnings.Count > 3 ? $" · +{comparison.Warnings.Count - 3} more" : "");
        ShowBackupTimes = _backupDisplay.IsVisible(now);
        if (!ShowBackupTimes) { return; }
        BackupDisplayLabel = $"B system · read-only · {_backupDisplay.RemainingSeconds(now)} s";
        var rows = comparison.Rows.GroupBy(x => x.Bib)
            .OrderByDescending(g => g.Where(x => !x.IsElapsed).Max(x => x.ATicks ?? 0)).Take(20)
            .SelectMany(g => g.OrderBy(x => x.Position == "Start" ? 0 : x.Position == "Finish" ? 1 : 2))
            .Select(x => new BackupMonitorRow(x.Bib, x.Position, FormatBackupTicks(x.ATicks, x.IsElapsed),
                FormatBackupTicks(x.BTicks, x.IsElapsed), x.DifferenceTicks is { } delta
                    ? (delta / (decimal)TimeSpan.TicksPerSecond).ToString("+0.0000000;-0.0000000;0.0000000", CultureInfo.InvariantCulture) : "—",
                x.Warning.Length != 0 ? x.Warning : x.BTicks is null ? "Waiting / unmatched" : "Observed")).ToArray();
        if (!BackupMonitorRows.SequenceEqual(rows))
        { BackupMonitorRows.Clear(); foreach (var row in rows) { BackupMonitorRows.Add(row); } }
    }

    private static string FormatBackupTicks(long? ticks, bool elapsed) => ticks is not { } value ? "—"
        : elapsed ? (value / (decimal)TimeSpan.TicksPerSecond).ToString("0.0000000", CultureInfo.InvariantCulture)
        : TimingTime.FormatTimeOfDay(value);
}

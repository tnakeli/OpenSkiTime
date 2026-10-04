using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

// Settings → Timing devices: role-based timing configuration (Start, Finish, Intermediate N, B Clock Start/Finish).
public sealed partial class MainViewModel
{
    private ObservableCollection<TimingRoleEditor>? _primaryTimingRoles;
    private ObservableCollection<TimingRoleEditor>? _backupTimingRoles;
    [ObservableProperty] private int? _backupClockUtcOffsetMinutes;
    [ObservableProperty] private string _timingMigrationNotes = "";

    public ObservableCollection<TimingRoleEditor> PrimaryTimingRoles => _primaryTimingRoles ??= CreateRoleCollection(DefaultPrimaryRoles());
    public ObservableCollection<TimingRoleEditor> BackupTimingRoles => _backupTimingRoles ??= CreateRoleCollection([]);
    public TimingRoleEditor TimingStartRole => PrimaryTimingRoles[0];
    public TimingRoleEditor TimingFinishRole => PrimaryTimingRoles[1];
    public IReadOnlyList<TimingRoleEditor> TimingIntermediateRoles => PrimaryTimingRoles.Where(x => x.Role.Kind == TimingRoleKind.Intermediate).ToArray();
    public bool HasTimingIntermediateRoles => PrimaryTimingRoles.Count > 2;
    public bool HasBackupClockRoles => BackupTimingRoles.Count > 0;
    public bool HasNoBackupClockRoles => !HasBackupClockRoles;
    public bool HasTimingMigrationNotes => TimingMigrationNotes.Length != 0;
    public bool UsesAlgeResultsAccount => PrimaryTimingRoles.Concat(BackupTimingRoles).Any(x => x.IsAlgeResults);
    public bool IsTimingSimulator => PrimaryTimingRoles.FirstOrDefault()?.SourceType == TimingSourceType.Simulator;
    public string TimingDeviceHelp => PrimaryTimingRoles.FirstOrDefault()?.SourceType switch
    {
        TimingSourceType.TimyUsb => "Timy PC Timer mode · install the ALGE USB driver once. Native USB uses the vendor library.",
        TimingSourceType.Mt1Serial => "Choose the MT1 virtual COM port. Each role uses its own channel on this device.",
        TimingSourceType.AlgeResults => "Timekeeper account required. Start and finish device IDs may be the same. Receive-from uses UTC; empty means connect time.",
        TimingSourceType.ReplayFile => "Replays a raw ALGE ASCII file as training data.",
        _ => "Training data only. Use a separate test event file; it cannot be mixed with real timing in one run."
    };

    partial void OnTimingMigrationNotesChanged(string value) => OnPropertyChanged(nameof(HasTimingMigrationNotes));

    private ObservableCollection<TimingRoleEditor> CreateRoleCollection(IEnumerable<TimingRoleEditor> editors)
    {
        // Initial editors are added without change notifications: the collection is created lazily by a property getter.
        var collection = new ObservableCollection<TimingRoleEditor>(editors);
        foreach (var editor in collection) { editor.PropertyChanged += OnTimingRoleEditorChanged; }
        collection.CollectionChanged += OnTimingRolesChanged;
        return collection;
    }

    private static TimingRoleEditor[] DefaultPrimaryRoles()
    {
        var start = new TimingRoleEditor(TimingRole.Start) { Channel = 0 };
        return [start, new TimingRoleEditor(TimingRole.Finish, start) { Channel = 1 }];
    }

    private void OnTimingRolesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var editor in e.OldItems?.OfType<TimingRoleEditor>() ?? []) { editor.PropertyChanged -= OnTimingRoleEditorChanged; editor.Detach(); }
        foreach (var editor in e.NewItems?.OfType<TimingRoleEditor>() ?? []) { editor.PropertyChanged += OnTimingRoleEditorChanged; }
        NotifyTimingRoles();
    }

    private void OnTimingRoleEditorChanged(object? sender, PropertyChangedEventArgs e) => NotifyTimingRoles();

    private void NotifyTimingRoles()
    {
        foreach (var name in new[] { nameof(TimingIntermediateRoles), nameof(HasTimingIntermediateRoles), nameof(HasBackupClockRoles),
            nameof(HasNoBackupClockRoles), nameof(UsesAlgeResultsAccount), nameof(IsTimingSimulator), nameof(TimingDeviceHelp) }) { OnPropertyChanged(name); }
    }

    public void LoadTimingPreferences()
    {
        if (timingPreferencesStore?.LoadWithNotes() is not { } loaded) { return; }
        ApplyTimingConfiguration(loaded.Configuration, primary: true, backup: true);
        TimingMigrationNotes = string.Join(Environment.NewLine, loaded.MigrationNotes);
    }

    // Replaces the Settings editors with a configuration. Primary and B groups can be applied independently.
    private void ApplyTimingConfiguration(TimingRoleConfiguration configuration, bool primary, bool backup)
    {
        if (configuration.Assignments.Select(x => x.Connection).FirstOrDefault(x => x.Source == TimingSourceType.AlgeResults
            && x.AlgeUsername.Trim().Length != 0) is { } alge) { Mt1Username = alge.AlgeUsername.Trim(); }
        if (primary && configuration.Start is { } start)
        {
            var editors = DefaultPrimaryRoles();
            editors[0].Load(start);
            if (configuration.Finish is { } finish) { editors[1].Load(finish); }
            var all = editors.ToList();
            foreach (var intermediate in configuration.Intermediates)
            {
                var editor = new TimingRoleEditor(intermediate.Role, editors[0]);
                editor.Load(intermediate); all.Add(editor);
            }
            ReplaceRoles(PrimaryTimingRoles, all);
        }
        if (backup) { ApplyBackupConfiguration(configuration); }
    }

    private void ApplyBackupConfiguration(TimingRoleConfiguration configuration)
    {
        if (configuration.BackupStart is { } backupStart)
        {
            var leader = new TimingRoleEditor(TimingRole.BackupStart);
            leader.Load(backupStart);
            var follower = new TimingRoleEditor(TimingRole.BackupFinish, leader) { Channel = 1 };
            if (configuration.BackupFinish is { } backupFinish) { follower.Load(backupFinish); }
            ReplaceRoles(BackupTimingRoles, [leader, follower]);
        }
        else { ReplaceRoles(BackupTimingRoles, []); }
        BackupStartWarningMilliseconds = configuration.BackupWarnings.StartWarningMilliseconds;
        BackupFinishWarningMilliseconds = configuration.BackupWarnings.FinishWarningMilliseconds;
        BackupMissingGraceSeconds = configuration.BackupWarnings.MissingSignalWaitSeconds;
        BackupClockUtcOffsetMinutes = configuration.BackupClockUtcOffsetMinutes;
    }

    private static void ReplaceRoles(ObservableCollection<TimingRoleEditor> target, IReadOnlyList<TimingRoleEditor> editors)
    {
        // Remove one by one so replaced editors are unsubscribed (Clear raises Reset without old items).
        while (target.Count > 0) { target.RemoveAt(target.Count - 1); }
        foreach (var editor in editors) { target.Add(editor); }
    }

    // The configuration currently shown in Settings. It is validated before saving or connecting.
    public TimingRoleConfiguration CurrentTimingConfiguration()
    {
        var username = Mt1Username.Trim();
        return new(PrimaryTimingRoles.Concat(BackupTimingRoles).Select(x => x.ToAssignment(username)).ToArray())
        {
            BackupWarnings = new(BackupStartWarningMilliseconds, BackupFinishWarningMilliseconds, BackupMissingGraceSeconds),
            BackupClockUtcOffsetMinutes = BackupClockUtcOffsetMinutes
        };
    }

    private void EnsureTimingSettingsEditable()
    {
        if (!CanChangeTimingDevice) { throw new DomainValidationException("Disconnect capture before changing settings."); }
    }

    [RelayCommand] private async Task AddTimingIntermediateAsync() => await GuardAsync(() =>
    {
        EnsureTimingSettingsEditable();
        var used = PrimaryTimingRoles.Select(x => x.Channel).ToHashSet();
        var channel = Enumerable.Range(0, 9).Where(x => !used.Contains(x)).Select(x => (int?)x).FirstOrDefault()
            ?? throw new DomainValidationException("All channels C0–C8 are already assigned on this device.");
        PrimaryTimingRoles.Add(new TimingRoleEditor(TimingRole.Intermediate(PrimaryTimingRoles.Count - 1), TimingStartRole) { Channel = channel });
        return Task.CompletedTask;
    });

    [RelayCommand] private async Task RemoveTimingIntermediateAsync() => await GuardAsync(() =>
    {
        EnsureTimingSettingsEditable();
        if (HasTimingIntermediateRoles) { PrimaryTimingRoles.RemoveAt(PrimaryTimingRoles.Count - 1); }
        return Task.CompletedTask;
    });

    [RelayCommand] private async Task AddBackupClockAsync() => await GuardAsync(() =>
    {
        EnsureTimingSettingsEditable();
        if (HasBackupClockRoles) { return Task.CompletedTask; }
        var leader = new TimingRoleEditor(TimingRole.BackupStart) { Channel = 0 };
        ReplaceRoles(BackupTimingRoles, [leader, new TimingRoleEditor(TimingRole.BackupFinish, leader) { Channel = 1 }]);
        return Task.CompletedTask;
    });

    [RelayCommand] private async Task ClearBackupClockAsync() => await GuardAsync(() =>
    {
        EnsureTimingSettingsEditable();
        ReplaceRoles(BackupTimingRoles, []);
        return Task.CompletedTask;
    });

    [RelayCommand] private async Task SaveTimingPreferencesAsync() => await GuardAsync(() =>
    {
        EnsureTimingSettingsEditable();
        var configuration = CurrentTimingConfiguration();
        configuration.Validate();
        timingPreferencesStore?.Save(configuration);
        TimingMigrationNotes = "";
        RefreshBackupClockStatus();
        SetStatus("Timing settings saved on this computer.");
        return Task.CompletedTask;
    });

    private DateTimeOffset ReadAlgeReceiveFrom()
    {
        if (string.IsNullOrWhiteSpace(Mt1FromUtc)) { return DateTimeOffset.UtcNow; }
        if (DateTimeOffset.TryParse(Mt1FromUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        { return parsed; }
        throw new DomainValidationException("Enter a valid receive-from date/time in UTC, e.g. 2026-09-27 10:00:00.");
    }
}

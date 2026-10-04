using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

// Settings → Timing devices: role-based timing configuration (Start, Finish, Intermediate N, B Clock Start/Finish).
public sealed partial class MainViewModel
{
    private ObservableCollection<TimingRoleEditor>? _primaryTimingRoles;
    private ObservableCollection<TimingRoleEditor>? _backupTimingRoles;
    private readonly Dictionary<string, string> _algeSessionPasswords = new(StringComparer.Ordinal);
    public ObservableCollection<AlgeAccountEditor> AlgeAccounts { get; } = [];
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
    public bool UsesAlgeResultsAccount => AlgeAccounts.Count != 0;
    public bool IsTimingSimulator => PrimaryTimingRoles.FirstOrDefault()?.SourceType == TimingSourceType.Simulator;
    public string TimingDeviceHelp => PrimaryTimingRoles.FirstOrDefault()?.SourceType switch
    {
        TimingSourceType.TimyUsb => "Timy PC Timer mode · install the ALGE USB driver once. Native USB uses the vendor library.",
        TimingSourceType.Mt1Serial => "Choose the MT1 virtual COM port. Each role uses its own channel on this device.",
        TimingSourceType.AlgeResults => "Each ALGE Results role has its own account and device ID; different clubs may use different accounts. Timekeeper role required.",
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
        RefreshAlgeAccounts();
        foreach (var name in new[] { nameof(TimingIntermediateRoles), nameof(HasTimingIntermediateRoles), nameof(HasBackupClockRoles),
            nameof(HasNoBackupClockRoles), nameof(UsesAlgeResultsAccount), nameof(IsTimingSimulator), nameof(TimingDeviceHelp) }) { OnPropertyChanged(name); }
    }

    // One account row per distinct ALGE Results username used by any role. Typed passwords survive role edits.
    private void RefreshAlgeAccounts()
    {
        var usernames = PrimaryTimingRoles.Concat(BackupTimingRoles).Where(x => x.IsAlgeResults)
            .Select(x => x.ToConnection().AlgeUsername).Where(x => x.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
        if (!AlgeAccounts.Select(x => x.Username).SequenceEqual(usernames))
        {
            var existing = AlgeAccounts.ToDictionary(x => x.Username, StringComparer.Ordinal);
            AlgeAccounts.Clear();
            foreach (var username in usernames) { AlgeAccounts.Add(existing.GetValueOrDefault(username) ?? new AlgeAccountEditor(username)); }
            OnPropertyChanged(nameof(UsesAlgeResultsAccount));
        }
        ShareAlgeDevices();
    }

    // Each ALGE Results role row offers the fetched devices of its own account.
    private void ShareAlgeDevices()
    {
        foreach (var editor in PrimaryTimingRoles.Concat(BackupTimingRoles))
        {
            var username = editor.IsAlgeResults ? editor.ToConnection().AlgeUsername : "";
            editor.SetAlgeDevices(AlgeAccounts.FirstOrDefault(x => x.Username == username)?.Devices.ToArray() ?? []);
        }
    }

    [RelayCommand]
    private async Task FetchAlgeDevicesAsync(AlgeAccountEditor? account)
    {
        if (account is null || account.IsFetching) { return; }
        var password = account.PeekPassword();
        if (password.Length == 0) { account.Status = "Enter the password first."; return; }
        account.IsFetching = true; account.Status = "Fetching devices…";
        try
        {
            var devices = await AlgeResultsSource.ListDevicesAsync(_timingHttp, account.Username, password);
            account.Devices.Clear(); foreach (var device in devices) { account.Devices.Add(device); }
            account.Status = devices.Count == 0 ? "No devices in this account." : devices.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " device(s) · choose them in the role rows";
            ShareAlgeDevices();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException or InvalidOperationException or KeyNotFoundException)
        { account.Status = "Devices could not be fetched: " + ex.Message; }
        finally { account.IsFetching = false; }
    }

    // Passwords are taken once per connection and kept in memory only while timing is connected, so B Clock retries work.
    private string AlgePassword(string username)
    {
        if (_algeSessionPasswords.TryGetValue(username, out var known) && known.Length != 0) { return known; }
        var account = AlgeAccounts.FirstOrDefault(x => x.Username == username) ?? new AlgeAccountEditor(username);
        var password = account.TakePassword();
        _algeSessionPasswords[username] = password;
        return password;
    }

    private void ForgetAlgeSessionPasswords() => _algeSessionPasswords.Clear();

    public void LoadTimingPreferences()
    {
        if (timingPreferencesStore?.LoadWithNotes() is not { } loaded) { return; }
        ApplyTimingConfiguration(loaded.Configuration, primary: true, backup: true);
        TimingMigrationNotes = string.Join(Environment.NewLine, loaded.MigrationNotes);
    }

    // Replaces the Settings editors with a configuration. Primary and B groups can be applied independently.
    private void ApplyTimingConfiguration(TimingRoleConfiguration configuration, bool primary, bool backup)
    {
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
        return new(PrimaryTimingRoles.Concat(BackupTimingRoles).Select(x => x.ToAssignment()).ToArray())
        {
            BackupWarnings = new(BackupStartWarningMilliseconds, BackupFinishWarningMilliseconds, BackupMissingGraceSeconds)
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
}

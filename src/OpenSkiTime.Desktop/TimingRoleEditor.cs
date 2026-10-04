using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

// One editable timing role row in Settings: Role -> Source -> Connection -> Channel.
// Only the connection fields of the chosen source are shown and used. Rows other than Start / B Clock Start default
// to the connection of their group's leading row, so a shared device is entered once and each row adds only its channel
// (and its ALGE Results device ID).
public sealed partial class TimingRoleEditor : ObservableObject
{
    private string _port = "";

    public TimingRoleEditor(TimingRole role, TimingRoleEditor? leader = null)
    {
        Role = role;
        Leader = leader;
        Sources = (role.IsBackup ? TimingSourceTypes.Backup : TimingSourceTypes.Primary).Select(TimingSourceTypes.Label).ToArray();
        _source = Sources[0];
        _usesLeaderConnection = leader is not null;
        if (leader is not null) { leader.PropertyChanged += OnLeaderChanged; }
    }

    public TimingRole Role { get; }
    public TimingRoleEditor? Leader { get; }
    public string Label => Role.Label;
    public IReadOnlyList<string> Sources { get; }
    public bool HasLeader => Leader is not null;
    public string SameConnectionLabel => Leader is null ? "" : "Same as " + Leader.Label;
    public string LeaderSourceLabel => Leader?.Source ?? "";

    [ObservableProperty] private string _source;
    [ObservableProperty] private bool _usesLeaderConnection;
    [ObservableProperty] private int _channel;
    [ObservableProperty] private string _usbId = "";
    [ObservableProperty] private string _firmware = "Not queried";
    [ObservableProperty] private int _baudRate = 38400;
    [ObservableProperty] private string _algeDeviceId = "";
    [ObservableProperty] private string _replayPath = "";

    // A port list control must not clear the configured port.
    public string Port
    {
        get => _port;
        set { if (value is not null) { SetProperty(ref _port, value); } }
    }
    public TimingSourceType? SourceType => TimingSourceTypes.Parse(Source);
    public TimingSourceType? EffectiveSourceType => UsesLeaderConnection && Leader is not null ? Leader.EffectiveSourceType : SourceType;
    public bool ShowOwnConnection => !UsesLeaderConnection || Leader is null;
    public bool ShowsLeaderConnection => !ShowOwnConnection;
    public bool IsTimyUsb => ShowOwnConnection && SourceType == TimingSourceType.TimyUsb;
    public bool IsSerial => ShowOwnConnection && SourceType == TimingSourceType.Mt1Serial;
    public bool IsReplay => ShowOwnConnection && SourceType == TimingSourceType.ReplayFile;
    public bool IsSimulator => ShowOwnConnection && SourceType == TimingSourceType.Simulator;
    // ALGE Results device IDs belong to each role, also when the account connection is shared.
    public bool IsAlgeResults => EffectiveSourceType == TimingSourceType.AlgeResults;

    partial void OnSourceChanged(string value) => NotifyConnection();
    partial void OnUsesLeaderConnectionChanged(bool value) => NotifyConnection();

    private void OnLeaderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Source) or nameof(EffectiveSourceType))
        { OnPropertyChanged(nameof(IsAlgeResults)); OnPropertyChanged(nameof(EffectiveSourceType)); OnPropertyChanged(nameof(LeaderSourceLabel)); }
    }

    private void NotifyConnection()
    {
        foreach (var name in new[] { nameof(SourceType), nameof(EffectiveSourceType), nameof(ShowOwnConnection), nameof(ShowsLeaderConnection),
            nameof(IsTimyUsb), nameof(IsSerial), nameof(IsReplay), nameof(IsSimulator), nameof(IsAlgeResults) }) { OnPropertyChanged(name); }
    }

    public void Detach() { if (Leader is not null) { Leader.PropertyChanged -= OnLeaderChanged; } }

    // The connection this row would use on its own. Fields that do not belong to the source are normalized.
    public TimingConnection OwnConnection(string algeUsername)
    {
        ArgumentNullException.ThrowIfNull(algeUsername);
        var source = SourceType ?? throw new DomainValidationException($"{Label}: choose a timing source.");
        return new TimingConnection(source)
        {
            UsbId = source == TimingSourceType.TimyUsb ? UsbId.Trim() : "",
            Firmware = source == TimingSourceType.TimyUsb && Firmware.Trim().Length != 0 ? Firmware.Trim() : "Not queried",
            Port = source == TimingSourceType.Mt1Serial ? Port.Trim() : "",
            BaudRate = source == TimingSourceType.Mt1Serial ? BaudRate : 38400,
            AlgeDeviceId = source == TimingSourceType.AlgeResults ? AlgeDeviceId.Trim() : "",
            AlgeUsername = source == TimingSourceType.AlgeResults ? algeUsername.Trim() : "",
            ReplayPath = source == TimingSourceType.ReplayFile ? ReplayPath.Trim() : ""
        };
    }

    public TimingConnection ToConnection(string algeUsername)
    {
        if (!UsesLeaderConnection || Leader is null) { return OwnConnection(algeUsername); }
        var shared = Leader.ToConnection(algeUsername);
        return shared with { AlgeDeviceId = shared.Source == TimingSourceType.AlgeResults ? AlgeDeviceId.Trim() : "" };
    }

    public TimingSourceAssignment ToAssignment(string algeUsername) => new(Role, ToConnection(algeUsername), Channel);

    public void Load(TimingSourceAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var c = assignment.Connection;
        Source = Sources.Contains(c.SourceLabel) ? c.SourceLabel : Sources[0];
        UsbId = c.UsbId; Firmware = c.Firmware; Port = c.Port; BaudRate = c.BaudRate;
        AlgeDeviceId = c.AlgeDeviceId; ReplayPath = c.ReplayPath; Channel = assignment.Channel;
        if (Leader is not null)
        {
            var leader = Leader.ToConnection(c.AlgeUsername) with { AlgeDeviceId = "" };
            UsesLeaderConnection = SourceType is not null && OwnConnection(c.AlgeUsername) with { AlgeDeviceId = "" } == leader;
        }
    }
}

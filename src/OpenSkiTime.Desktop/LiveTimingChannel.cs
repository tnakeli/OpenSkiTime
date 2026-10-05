using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.LiveTiming;

namespace OpenSkiTime.Desktop;

public sealed partial class LiveTimingChannel(MainViewModel owner, string label, PublisherKind kind) : ObservableObject
{
    private PublisherHealth _health = new(PublisherState.Stopped);
    internal PublisherKind Kind { get; } = kind;
    public string Label { get; } = label;
    public bool IsFis => Kind is PublisherKind.FisTcp or PublisherKind.FisHttps;
    public bool IsStandalone => !IsFis;
    public int? WorkerProcessId { get; private set; }
    public string IndicatorColor => Status == "Stopped" ? "#DCE6E9" : Status == "Running" ? "#49BA91" : "#EF7777";
    public string Summary => $"{Label} · {Status}";
    public string StatusToolTip => Summary + "\n" + Detail + (Expiration.Length > 0 ? "\n" + Expiration : "");
    [ObservableProperty] private string _status = "Stopped";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _publicUrl = "";
    [ObservableProperty] private string _expiration = "";
    [ObservableProperty] private bool _busy;
    partial void OnStatusChanged(string value)
    { OnPropertyChanged(nameof(IndicatorColor)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(StatusToolTip)); }
    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(StatusToolTip));
    internal void Update(LiveChannelHealth channel)
    {
        _health = channel.Health; WorkerProcessId = channel.ProcessId;
        Status = _health.State.ToString(); PublicUrl = _health.PublicUrl ?? "";
        Expiration = _health.ExpiresAt is { } expires ? "Expires " + expires.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        var last = _health.LastSuccessfulPublish ?? _health.LastConnected;
        Detail = _health.Endpoint + (last is { } at ? "\nLast OK " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "")
            + ((_health.Error ?? _health.Notice) is { } message ? "\n" + message : "");
        OnPropertyChanged(nameof(StatusToolTip)); OnPropertyChanged(nameof(WorkerProcessId));
    }
    [RelayCommand] private Task StartAsync() => owner.StartLiveChannelAsync(this);
    [RelayCommand] private Task StopAsync() => owner.ControlLiveChannelAsync(this, "stop");
    [RelayCommand] private Task RefreshAsync() => owner.ControlLiveChannelAsync(this, "refresh");
    [RelayCommand] private Task DeleteAsync() => owner.ControlLiveChannelAsync(this, "delete");
    [RelayCommand] private Task DeleteAllDataAsync() => owner.ControlLiveChannelAsync(this, "delete-all");
}

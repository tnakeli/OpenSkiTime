using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Client;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class LiveTimingChannel(MainViewModel owner, string label, PublisherKind kind) : ObservableObject
{
    internal PublisherProcess Process { get; set; } = new();
    internal PublisherKind Kind { get; } = kind;
    internal PublisherOptions? Options { get; set; }
    internal string? CredentialTarget { get; set; }
    internal string? SavedToken { get; set; }
    public string Label { get; } = label;
    public string Summary
    {
        get
        {
            var health = Process.Health;
            var last = health.LastSuccessfulPublish ?? health.LastConnected;
            var label = Kind == PublisherKind.Local ? "Local" : Kind == PublisherKind.Cloud ? "Cloud" : "FIS";
            var summary = $"{label} · {Status}" + (last is { } at ? " · " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "");
            return health.Error is null ? summary : summary + " · " + (health.State == PublisherState.Reconnecting ? "Check network" : "Restart / settings");
        }
    }
    public bool IsFis => Kind is PublisherKind.FisTcp or PublisherKind.FisHttps;
    public bool IsStandalone => !IsFis;
    public int? WorkerProcessId => Process.ProcessId;
    [ObservableProperty] private string _status = "Stopped";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _publicUrl = "";
    [ObservableProperty] private string _expiration = "";
    [ObservableProperty] private bool _busy;
    internal void Update()
    {
        var h = Process.Health;
        Status = h.State.ToString(); PublicUrl = h.PublicUrl ?? "";
        Expiration = h.ExpiresAt is { } expires ? "Expires " + expires.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        var last = h.LastSuccessfulPublish ?? h.LastConnected;
        Detail = (last is null ? "" : "Last OK " + last.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · ") + (h.Error ?? h.Endpoint);
        OnPropertyChanged(nameof(Summary));
    }
    [RelayCommand] private Task StartAsync() => owner.StartLiveChannelAsync(this);
    [RelayCommand] private Task StopAsync() => owner.ControlLiveChannelAsync(this, "stop");
    [RelayCommand] private Task RefreshAsync() => owner.ControlLiveChannelAsync(this, "refresh");
    [RelayCommand] private Task DeleteAsync() => owner.ControlLiveChannelAsync(this, "delete");
    [RelayCommand] private Task DeleteAllDataAsync() => owner.ControlLiveChannelAsync(this, "delete-all");
    [RelayCommand] private Task CopyUrlAsync() => MainViewModel.CopyLiveUrlAsync(PublicUrl);
    [RelayCommand] private void OpenUrl()
    {
        if (Uri.TryCreate(PublicUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
    }
}

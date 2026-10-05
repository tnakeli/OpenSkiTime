using System.Globalization;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenSkiTime.LiveTiming.ControlPanel;

public sealed partial class PanelViewModel(LiveControlSession session, PanelConnection? connection = null) : ObservableObject
{
    public PanelChannel[] Channels { get; } = [new("LOCAL", PublisherKind.Local), new("CLOUD", PublisherKind.Cloud), new("FIS", PublisherKind.FisHttps)];
    [ObservableProperty] private string _competition = "Waiting for a timing run";
    private string _operationError = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    public bool HasError => !string.IsNullOrEmpty(Error);
    [ObservableProperty] private string _localEndpoint = "http://localhost:5078";
    [ObservableProperty] private string _cloudEndpoint = "https://live.openskiti.me";
    [ObservableProperty] private string _fisHttpsEndpoint = "https://livedata.fis-ski.com/al/";
    [ObservableProperty] private string _fisTcpHost = "live.fis-ski.com";
    [ObservableProperty] private int _fisTcpPort = 1550;
    [ObservableProperty] private string _fisPassword = "";
    [ObservableProperty] private bool _fisUseTcp;
    [ObservableProperty] private string _timeZone = TimeZoneInfo.Local.Id;
    [ObservableProperty] private bool _busy;
    public Window? Window { get; set; }
    public void Initialize(LiveConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        LocalEndpoint = settings.LocalEndpoint; CloudEndpoint = settings.CloudEndpoint; FisHttpsEndpoint = settings.FisHttpsEndpoint;
        FisTcpHost = settings.FisTcpHost; FisTcpPort = settings.FisTcpPort; FisPassword = settings.FisPassword; FisUseTcp = settings.FisUseTcp; TimeZone = settings.TimeZone;
    }
    private LiveConnectionSettings Settings() => new(LocalEndpoint, CloudEndpoint, FisHttpsEndpoint, FisTcpHost, FisTcpPort, FisPassword, FisUseTcp, TimeZone);
    public void Update()
    {
        var state = session.ReadState(); Error = connection is null && session.Snapshot is null
            ? "Open a saved timing run in OpenSkiTime and use its Live timing button to connect this panel."
            : state.Error.Length > 0 ? state.Error : _operationError;
        Competition = session.Snapshot is { } snapshot ? $"{snapshot.Competition.Name}  /  Run {snapshot.CurrentRun}" : "Waiting for a timing run";
        foreach (var channel in Channels) { channel.Update(state.Channels.Single(x => x.Kind == channel.Kind)); }
    }
    [RelayCommand]
    private async Task ControlAsync(PanelChannelAction action)
    {
        Busy = true; _operationError = "";
        try
        {
            ControlPanelInput? prepared = null;
            if (action.Action is ControlPanelAction.Start or ControlPanelAction.Refresh && connection is not null)
            { prepared = await connection.PrepareAsync(Settings()); }
            var result = await Task.Run(() => session.ApplyAsync(new(Guid.NewGuid(), action.Action, prepared?.CompetitionId,
                prepared?.Snapshot, action.Kind, Settings())));
            _operationError = result.Error; Update();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or LiveValidationException)
        { _operationError = "Could not obtain current timing state. Check the selected run and time zone, then try again."; Error = _operationError; }
        finally { Busy = false; }
    }
    [RelayCommand]
    private async Task CopyUrlAsync(PanelChannel channel)
    {
        if (Window?.Clipboard is { } clipboard && !string.IsNullOrEmpty(channel.PublicUrl)) { await clipboard.SetTextAsync(channel.PublicUrl); }
    }
    [RelayCommand]
    private static void OpenUrl(PanelChannel channel)
    {
        if (Uri.TryCreate(channel.PublicUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
    }
}
public sealed record PanelChannelAction(PublisherKind Kind, ControlPanelAction Action);
public sealed partial class PanelChannel(string label, PublisherKind kind) : ObservableObject
{
    public string Label { get; } = label;
    public PublisherKind Kind { get; } = kind;
    public bool IsStandalone => Kind is PublisherKind.Local or PublisherKind.Cloud;
    public bool HasPublicUrl => IsStandalone && !string.IsNullOrEmpty(PublicUrl);
    public PanelChannelAction Start { get; } = new(kind, ControlPanelAction.Start);
    public PanelChannelAction Stop { get; } = new(kind, ControlPanelAction.Stop);
    public PanelChannelAction Refresh { get; } = new(kind, ControlPanelAction.Refresh);
    public PanelChannelAction Delete { get; } = new(kind, ControlPanelAction.Delete);
    public PanelChannelAction DeleteAll { get; } = new(kind, ControlPanelAction.DeleteAll);
    [ObservableProperty] private string _status = "Stopped";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPublicUrl))] private string _publicUrl = "";
    [ObservableProperty] private string _expiration = "";
    [ObservableProperty] private string _indicatorColor = "#DCE6E9";
    public void Update(LiveChannelHealth channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var h = channel.Health;
        Status = h.State.ToString(); PublicUrl = h.PublicUrl ?? "";
        Expiration = h.ExpiresAt is { } expiry ? "Expires " + expiry.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        Detail = h.Error ?? h.Notice ?? (h.LastSuccessfulPublish is { } last ? "Last published " + last.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "Ready to start");
        IndicatorColor = h.State == PublisherState.Stopped ? "#DCE6E9" : h.State == PublisherState.Running ? "#238067" : "#B94B4B";
    }
}

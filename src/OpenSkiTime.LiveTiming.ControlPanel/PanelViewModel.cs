using System.Globalization;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.LiveTiming.Radio;

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
    // Race radio: optional, off by default and configured per competition.
    public RadioChoice[] RadioLlmChoices { get; } = RadioPrices.Llm.Select(x => new RadioChoice(x.Provider, x.Model, x.Provider switch
    {
        RadioProviders.OpenAi => "OpenAI " + x.Model, RadioProviders.Gemini => "Gemini " + x.Model, _ => "Simulation (offline, no cost)"
    })).ToArray();
    public RadioChoice[] RadioTtsChoices { get; } =
    [
        new(RadioProviders.Azure, "neural", "Azure AI Speech · fi-FI neural"), new(RadioProviders.Google, "chirp3-hd", "Google Chirp 3: HD · fi-FI"),
        new(RadioProviders.GoogleGemini, "gemini-3.8-flash-tts", "Google Gemini 3.8 Flash TTS (Vertex AI)"),
        new(RadioProviders.ElevenLabs, "eleven_v4", "ElevenLabs Eleven v4"),
        new(RadioProviders.OpenAi, "gpt-4o-mini-tts", "OpenAI gpt-4o-mini-tts"), new(RadioProviders.Simulation, "simulation", "Simulation (offline tones, no cost)")
    ];
    [ObservableProperty] private bool _radioLocal;
    [ObservableProperty] private bool _radioCloud;
    [ObservableProperty] private decimal? _radioBudget = 5m;
    [ObservableProperty] private RadioChoice? _radioLlm;
    [ObservableProperty] private bool _radioCuration = true;
    [ObservableProperty] private RadioChoice? _radioTts;
    [ObservableProperty] private string _radioRegion = "westeurope";
    [ObservableProperty] private string _radioVoiceA = "";
    [ObservableProperty] private string _radioVoiceB = "";
    [ObservableProperty] private string _radioContext = "";
    [ObservableProperty] private string _radioNotes = "";
    [ObservableProperty] private string _radioLexicon = "";
    [ObservableProperty] private string _radioMusicFolder = "";
    [ObservableProperty] private string _radioMusic = "";
    [ObservableProperty] private string _radioStatus = "Off";
    [ObservableProperty] private string _radioDetail = "Enable the radio for Local or Cloud in Race radio settings.";
    [ObservableProperty] private string _radioSpend = "";
    [ObservableProperty] private string _radioIndicator = "#94A3B8";
    [ObservableProperty] private string _radioSaveStatus = "";
    [ObservableProperty] private bool _radioReady;
    // Not a competition ID, so the first update shows the defaults.
    private Guid? _radioCompetition = Guid.Empty;
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
        // Identity and settings are taken together: a competition whose settings are still loading is retried next tick.
        if (session.CompetitionId != _radioCompetition && (session.CompetitionId is null || session.RadioConfiguration is not null))
        { _radioCompetition = session.CompetitionId; LoadRadio(session.RadioConfiguration); }
        UpdateRadio(state.Radio ?? new RadioHealth(RadioState.Stopped));
    }
    private void LoadRadio(RadioConfiguration? configuration)
    {
        RadioReady = configuration is not null;
        var c = configuration ?? new RadioConfiguration();
        RadioLocal = c.LocalEnabled; RadioCloud = c.CloudEnabled; RadioBudget = c.BudgetUsd; RadioCuration = c.Curation;
        RadioLlm = RadioLlmChoices.FirstOrDefault(x => x.Provider == c.LlmProvider && (x.Model == c.LlmModel || c.LlmProvider == RadioProviders.Simulation)) ?? RadioLlmChoices[0];
        RadioTts = RadioTtsChoices.FirstOrDefault(x => x.Provider == c.TtsProvider) ?? RadioTtsChoices[0];
        RadioRegion = c.TtsRegion; RadioVoiceA = c.VoiceA; RadioVoiceB = c.VoiceB;
        RadioContext = c.EventContext; RadioNotes = c.CurationNotes; RadioLexicon = c.Lexicon; RadioMusicFolder = c.MusicFolder; RadioSaveStatus = "";
    }
    private void UpdateRadio(RadioHealth health)
    {
        var enabled = session.RadioConfiguration is { } c && (c.LocalEnabled || c.CloudEnabled);
        RadioStatus = !enabled ? "Off" : health.State switch
        {
            RadioState.Stopped => "Waiting", RadioState.BudgetExhausted => "Budget reached", _ => health.State.ToString()
        };
        RadioIndicator = !enabled || health.State == RadioState.Stopped ? "#94A3B8" : health.State == RadioState.Running ? "#16A34A" : "#DC2626";
        var last = health.LastClipAt is { } at ? $"Last clip {at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {health.LastClipHost}" : "No clips yet";
        RadioDetail = !enabled ? "Enable the radio for Local or Cloud in Race radio settings."
            : health.Error is { Length: > 0 } error ? error
            : health.State == RadioState.Stopped ? "Starts when an enabled Local or Cloud channel is running."
            : $"{last} · queue {health.QueueDepth.ToString(CultureInfo.InvariantCulture)}" + (health.Mode.Length > 0 ? " · " + health.Mode : "")
                + (health.Phase is { } phase ? " · " + phase : "");
        var folder = session.RadioConfiguration?.MusicFolder ?? "";
        RadioMusic = !enabled || folder.Length == 0 || health.State == RadioState.Stopped ? ""
            : health.MusicTracks > 0 ? $"Music: {health.MusicTracks.ToString(CultureInfo.InvariantCulture)} tracks"
            : "Music: no playable MP3 files in the music folder (see the radio log).";
        RadioSpend = health.BudgetUsd > 0 || health.SpentUsd > 0
            ? $"Spent {health.SpentUsd.ToString("0.000", CultureInfo.InvariantCulture)} of {health.BudgetUsd.ToString("0.00", CultureInfo.InvariantCulture)} USD"
            : "";
    }
    [RelayCommand]
    private async Task SaveRadioAsync()
    {
        var configuration = new RadioConfiguration
        {
            LocalEnabled = RadioLocal, CloudEnabled = RadioCloud, BudgetUsd = RadioBudget ?? 0m, Curation = RadioCuration,
            LlmProvider = RadioLlm?.Provider ?? RadioProviders.Simulation, LlmModel = RadioLlm?.Model ?? "simulation",
            TtsProvider = RadioTts?.Provider ?? RadioProviders.Simulation, TtsRegion = RadioRegion.Trim(),
            VoiceA = RadioVoiceA.Trim(), VoiceB = RadioVoiceB.Trim(), EventContext = RadioContext, CurationNotes = RadioNotes, Lexicon = RadioLexicon,
            MusicFolder = RadioMusicFolder.Trim()
        };
        if (_radioCompetition is not { } competition || competition == Guid.Empty) { RadioSaveStatus = "Open a saved timing run in OpenSkiTime first."; return; }
        var error = await Task.Run(() => session.SaveRadioAsync(competition, configuration));
        RadioSaveStatus = error.Length == 0 ? "Radio settings saved for this competition." : error;
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
public sealed record RadioChoice(string Provider, string Model, string Label)
{
    public override string ToString() => Label;
}
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
    [ObservableProperty] private string _indicatorColor = "#94A3B8";
    public void Update(LiveChannelHealth channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var h = channel.Health;
        Status = h.State.ToString(); PublicUrl = h.PublicUrl ?? "";
        Expiration = h.ExpiresAt is { } expiry ? "Expires " + expiry.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
        Detail = h.Error ?? h.Notice ?? (h.LastSuccessfulPublish is { } last ? "Last published " + last.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "Ready to start");
        IndicatorColor = h.State == PublisherState.Stopped ? "#94A3B8" : h.State == PublisherState.Running ? "#16A34A" : "#DC2626";
    }
}

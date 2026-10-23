using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenSkiTime.LiveTiming.Client;

namespace OpenSkiTime.LiveTiming.ControlPanel;

public sealed class LiveControlSession : IAsyncDisposable
{
    private sealed class ChannelState(PublisherKind kind)
    {
        public PublisherKind Kind { get; } = kind;
        public PublisherProcess Process { get; set; } = new();
        public PublisherOptions? Options { get; set; }
        public string? CredentialTarget { get; set; }
        public string? SavedToken { get; set; }
    }
    private readonly ChannelState[] _channels = [new(PublisherKind.Local), new(PublisherKind.Cloud), new(PublisherKind.FisHttps)];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _competitionId;
    private LiveSnapshot? _snapshot;
    private long _lastSnapshotVersion = -1;
    private string _error = "";
    private bool _disposed;
    private readonly RadioSettingsStore _radioStore;
    private RadioProcess _radio = new();
    private RadioCompetitionSettings? _radioSettings;
    private RadioTarget[] _radioTargets = [];
    private bool _radioStarted;
    private Task? _radioStart;
    private bool _radioRestart;
    private IDisposable? _radioOwner;
    private string _radioError = "";
    private DateTimeOffset _radioRetry;
    private decimal _radioSaved;
    private DateTimeOffset _radioSavedAt;
    public LiveControlSession(RadioSettingsStore? radioStore = null) => _radioStore = radioStore ?? new(RadioSettingsStore.DefaultPath);
    /// <summary>Radio provider keys; Windows Credential Manager unless a test supplies another store.</summary>
    public Func<string, ICredentialStore> CredentialStore { get; init; } = target => new WindowsCredentialStore(target);
    public LiveSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public Guid? CompetitionId => _competitionId;
    public RadioConfiguration? RadioConfiguration => Volatile.Read(ref _radioSettings)?.Configuration;
    public ControlPanelOutput ReadState(Guid requestId = default) => new(requestId,
        _channels.Select(x => new LiveChannelHealth(x.Kind, x.Process.Health, x.Process.ProcessId)).ToArray(), _error, Radio: RadioHealth());
    private RadioHealth RadioHealth()
    {
        var health = _radio.Health;
        return _radioError.Length > 0 ? health with { Error = _radioError } : health;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional live controls return a redacted error instead of crashing or exposing credentials.")]
    public async Task<ControlPanelOutput> ApplyAsync(ControlPanelInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { throw new IOException("Live timing control panel is closing."); }
            if (input.Snapshot is { } snapshot && snapshot.Version > _lastSnapshotVersion)
            {
                snapshot.Validate();
                if (input.CompetitionId is null) { throw new LiveValidationException("Competition identity missing."); }
                if (_competitionId != input.CompetitionId)
                {
                    await ResetAsync().ConfigureAwait(false); _competitionId = input.CompetitionId;
                    LoadRadio(input.CompetitionId.Value);
                }
                _snapshot = snapshot;
                _lastSnapshotVersion = snapshot.Version;
                foreach (var channel in _channels) { channel.Process.Offer(snapshot); }
                _radio.Offer(snapshot);
            }
            if (input.Action == ControlPanelAction.Reset) { await ResetAsync().ConfigureAwait(false); _competitionId = null; _snapshot = null; _error = ""; }
            if (input.Action is ControlPanelAction.State or ControlPanelAction.Activate or ControlPanelAction.Reset) { return ReadState(input.RequestId); }
            var selected = _channels.SingleOrDefault(x => x.Kind == input.Channel) ?? throw new LiveValidationException("Choose a live channel.");
            if (input.Action == ControlPanelAction.Start)
            {
                var state = _snapshot ?? throw new LiveValidationException("Open a saved timing run in OpenSkiTime first.");
                var settings = input.Settings ?? throw new LiveValidationException("Connection settings missing.");
                _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
                if (selected.Kind == PublisherKind.FisHttps && !state.Competition.IsFis)
                { throw new LiveValidationException("FIS Live Timing requires a FIS competition."); }
                var options = Options(selected.Kind, settings);
                if (selected.Options is not null && selected.Options != options)
                {
                    await selected.Process.DisposeAsync().ConfigureAwait(false);
                    selected.Process = new(); selected.SavedToken = null;
                }
                selected.Options = options;
                selected.Process.Offer(state);
                if (selected.Kind == PublisherKind.Cloud && OperatingSystem.IsWindows())
                {
                    selected.CredentialTarget = "OpenSkiTime.LiveTiming:" + _competitionId!.Value.ToString("N") + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Endpoint)))[..16];
                    if (selected.Process.ResumeSession is null && new WindowsCredentialStore(selected.CredentialTarget).Read() is { } saved)
                    { selected.Process.ResumeSession = JsonSerializer.Deserialize<LiveSession>(saved, LiveJson.Options); }
                    options = options with { PublisherKey = new WindowsCredentialStore(PublisherKeyCredential.Target(options.Endpoint)).Read() };
                }
                await selected.Process.StartAsync(Artifact("Worker"), options).ConfigureAwait(false);
            }
            else
            {
                var command = input.Action switch
                {
                    ControlPanelAction.Stop => "stop", ControlPanelAction.Refresh => "refresh",
                    ControlPanelAction.Delete => "delete", ControlPanelAction.DeleteAll => "delete-all",
                    _ => throw new LiveValidationException("Unknown live command.")
                };
                await selected.Process.CommandAsync(command).ConfigureAwait(false);
            }
            _error = "";
        }
        catch (LiveValidationException ex) { _error = ex.Message; }
        catch (Exception) { _error = "Live timing control failed. Check settings and installed components, then press Start. Timing capture continues."; }
        finally { _gate.Release(); }
        return ReadState(input.RequestId);
    }
    private static PublisherOptions Options(PublisherKind kind, LiveConnectionSettings settings)
    {
        if (kind is PublisherKind.Local or PublisherKind.Cloud || !settings.FisUseTcp)
        {
            var endpoint = kind == PublisherKind.Local ? settings.LocalEndpoint : kind == PublisherKind.Cloud ? settings.CloudEndpoint : settings.FisHttpsEndpoint;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            { throw new LiveValidationException("Enter an HTTP or HTTPS endpoint without credentials in the URL."); }
            if (kind == PublisherKind.Local && (!uri.IsLoopback || uri.Scheme != "http")) { throw new LiveValidationException("Local URL must use loopback HTTP."); }
            if (kind == PublisherKind.FisHttps && uri.Scheme != "https") { throw new LiveValidationException("FIS HTTPS URL must use HTTPS."); }
        }
        if (settings.FisTcpPort is < 1 or > 65535) { throw new LiveValidationException("TCP port must be between 1 and 65535."); }
        if (kind == PublisherKind.FisHttps && settings.FisUseTcp && (string.IsNullOrWhiteSpace(settings.FisTcpHost) || settings.FisTcpHost.Any(char.IsWhiteSpace)))
        { throw new LiveValidationException("Enter a FIS TCP host name."); }
        return kind switch
        {
            PublisherKind.Local => new(kind, settings.LocalEndpoint, LocalServerAssembly: Artifact("Server")),
            PublisherKind.Cloud => new(kind, settings.CloudEndpoint),
            _ => new(settings.FisUseTcp ? PublisherKind.FisTcp : PublisherKind.FisHttps, settings.FisUseTcp ? settings.FisTcpHost : settings.FisHttpsEndpoint, settings.FisPassword, settings.FisTcpPort)
        };
    }
    public void RetainCredentials()
    {
        if (!_gate.Wait(0)) { return; }
        try
        {
            foreach (var channel in _channels)
            {
                if (channel.CredentialTarget is not { } target || !OperatingSystem.IsWindows()) { continue; }
                try
                {
                    if (channel.Process.ResumeSession is { } session && session.PublisherToken != channel.SavedToken)
                    { new WindowsCredentialStore(target).Save(JsonSerializer.Serialize(session, LiveJson.Options)); channel.SavedToken = session.PublisherToken; }
                    else if (channel.Process.ResumeSession is null && channel.SavedToken is not null)
                    { new WindowsCredentialStore(target).Remove(); channel.SavedToken = null; }
                }
                catch (IOException) { _error = "Cloud credential could not be retained. Publishing continues; restart may create a new URL."; }
            }
        }
        finally { _gate.Release(); }
    }
    private async Task ResetAsync()
    {
        await ResetRadioAsync().ConfigureAwait(false);
        Volatile.Write(ref _radioSettings, null);
        foreach (var channel in _channels)
        {
            await channel.Process.DisposeAsync().ConfigureAwait(false);
            channel.Process = new(); channel.Options = null; channel.CredentialTarget = null; channel.SavedToken = null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            await ResetRadioAsync().ConfigureAwait(false);
            foreach (var channel in _channels) { await channel.Process.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _gate.Release(); }
    }
    private void LoadRadio(Guid competitionId)
    {
        var settings = _radioStore.Read(competitionId);
        Volatile.Write(ref _radioSettings, settings);
        _radioSaved = settings.SpentUsd; _radioError = ""; _radioRetry = default;
    }
    private async Task ResetRadioAsync()
    {
        SaveSpend(force: true);
        await _radio.DisposeAsync().ConfigureAwait(false);
        SaveSpend(force: true);
        _radio = new(); _radioStarted = false; _radioStart = null; _radioRestart = false; _radioTargets = []; _radioError = "";
        _radioOwner?.Dispose(); _radioOwner = null;
    }
    // Spend survives panel restarts, so the hard budget stop cannot be bypassed by reopening the panel.
    private void SaveSpend(bool force)
    {
        var spent = _radio.Health.SpentUsd;
        if (_competitionId is not { } id || spent <= _radioSaved) { return; }
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _radioSavedAt < TimeSpan.FromSeconds(5) && _radio.Health.State != RadioState.BudgetExhausted) { return; }
        try { _radioStore.SaveSpend(id, spent); _radioSaved = spent; _radioSavedAt = now; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _radioError = "Radio spend could not be saved. " + ex.Message; }
    }

    /// <summary>Saves this competition's radio settings and restarts the radio with them.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional radio settings report a redacted error instead of crashing.")]
    public async Task<string> SaveRadioAsync(Guid competitionId, RadioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_competitionId is not { } id) { return "Open a saved timing run in OpenSkiTime first."; }
            // The form belongs to the competition it was loaded for; the race may have changed meanwhile.
            if (id != competitionId) { return "The competition changed. Check the radio settings of the current competition and save again."; }
            configuration.Validate();
            _radioStore.Save(id, configuration);
            SaveSpend(force: true);
            Volatile.Write(ref _radioSettings, new RadioCompetitionSettings(configuration, _radioSaved));
            _radioError = ""; _radioRetry = default;
            // A start still connecting is stopped by the status loop once it has connected, never awaited under the lock.
            if (_radioStart is { IsCompleted: false }) { _radioRestart = true; return ""; }
            if (_radio.IsConnected) { await _radio.StopAsync().ConfigureAwait(false); }
            _radioStarted = false; _radioTargets = [];
            return "";
        }
        catch (LiveValidationException ex) { return ex.Message; }
        catch (IOException ex) { return ex.Message; }
        catch (Exception) { return "Radio settings could not be saved. Timing capture and live publishing continue."; }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Runs on the panel status loop. Radio runs while it is enabled for a channel whose publisher is running with a
    /// session. It never delays the publishers, and its failure leaves them untouched.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional radio control reports a redacted error instead of crashing.")]
    public async Task SyncRadioAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) { return; }
        try
        {
            if (_disposed || _competitionId is null || _snapshot is null || Volatile.Read(ref _radioSettings) is not { } settings) { return; }
            SaveSpend(force: false);
            var now = DateTimeOffset.UtcNow;
            var targets = RadioTargets.Select(settings.Configuration,
                _channels.Select(x => (x.Kind, x.Process.Health, x.Options?.Endpoint, x.Process.ResumeSession)), now);
            // Starting a worker can take seconds; it runs outside the panel lock so channel commands are never delayed.
            if (_radioStart is { IsCompleted: false }) { return; }
            if (_radioStart is { } started)
            {
                _radioStart = null;
                if (started.Exception is not null) { _radioStarted = false; _radioError = "Race radio could not start. Check installed components; timing capture and live publishing continue."; }
            }
            if (_radioRestart)
            {
                // Settings were saved while this start was connecting: stop it, then start again with the saved settings.
                _radioRestart = false;
                if (_radio.IsConnected) { await _radio.StopAsync().ConfigureAwait(false); }
                _radioStarted = false; _radioTargets = [];
            }
            if (_radioStarted && !_radio.IsConnected) { _radioStarted = false; }
            if (targets.Length == 0)
            {
                // Also stops a worker that is still generating although no flag says so (for example after a failed save).
                if (_radioStarted || (_radio.IsConnected && _radio.Health.State is RadioState.Running or RadioState.Starting))
                { await _radio.StopAsync().ConfigureAwait(false); _radioStarted = false; }
                _radioTargets = [];
                _radioOwner?.Dispose(); _radioOwner = null;
                return;
            }
            if (!_radioStarted)
            {
                if (now < _radioRetry) { return; }
                _radioRetry = now + TimeSpan.FromSeconds(30);
                var config = settings.Configuration;
                // One radio per competition on this computer: two OpenSkiTime windows would otherwise share one budget twice.
                _radioOwner ??= _radioStore.TryOwn(_competitionId.Value)
                    ?? throw new LiveValidationException("Race radio for this competition is already running in another OpenSkiTime window on this computer.");
                // Under ownership, the saved spend is current: another window may have spent and saved since this one loaded it.
                _radioSaved = Math.Max(_radioSaved, _radioStore.Read(_competitionId.Value).SpentUsd);
                var llmKey = config.LlmProvider == RadioProviders.Simulation ? null : Key(config.LlmProvider);
                var ttsKey = config.TtsProvider == RadioProviders.Simulation ? null : Key(config.TtsProvider);
                var radio = _radio; var worker = Artifact("RadioWorker"); var options = new RadioWorkerOptions(config, _radioSaved, llmKey, ttsKey);
                _radioStart = Task.Run(() => radio.StartAsync(worker, options, targets));
                _radioStarted = true; _radioTargets = targets; _radioError = "";
            }
            else if (!targets.SequenceEqual(_radioTargets)) { await _radio.TargetsAsync(targets).ConfigureAwait(false); _radioTargets = targets; }
        }
        catch (LiveValidationException ex) { _radioError = ex.Message; }
        catch (PlatformNotSupportedException) { _radioError = "Race radio keys need Windows Credential Manager."; }
        catch (Exception) { _radioError = "Race radio could not start. Check installed components; timing capture and live publishing continue."; }
        finally { _gate.Release(); }
    }
    private string Key(string provider) => CredentialStore(RadioKeyCredential.Target(provider)).Read() is { Length: > 0 } key ? key
        : throw new LiveValidationException($"Save the {RadioKeyCredential.Label(provider)} key in OpenSkiTime Settings → Live timing to use the race radio.");

    private static string Artifact(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LiveTiming", name, $"OpenSkiTime.LiveTiming.{name}.dll");
        if (!File.Exists(path)) { throw new IOException("Live timing component missing."); }
        return path;
    }
}

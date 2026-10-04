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
    public LiveSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public ControlPanelOutput ReadState(Guid requestId = default) => new(requestId,
        _channels.Select(x => new LiveChannelHealth(x.Kind, x.Process.Health, x.Process.ProcessId)).ToArray(), _error);

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
                { await ResetAsync().ConfigureAwait(false); _competitionId = input.CompetitionId; }
                _snapshot = snapshot;
                _lastSnapshotVersion = snapshot.Version;
                foreach (var channel in _channels) { channel.Process.Offer(snapshot); }
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
            foreach (var channel in _channels) { await channel.Process.DisposeAsync().ConfigureAwait(false); }
        }
        finally { _gate.Release(); }
    }
    private static string Artifact(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LiveTiming", name, $"OpenSkiTime.LiveTiming.{name}.dll");
        if (!File.Exists(path)) { throw new IOException("Live timing component missing."); }
        return path;
    }
}

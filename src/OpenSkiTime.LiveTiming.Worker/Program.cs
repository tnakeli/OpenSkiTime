using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Publishing;

if (args.Length != 2 || args[0] != "--pipe") { Console.Error.WriteLine("Worker requires a private --pipe connection."); return 2; }
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var lifetime = new CancellationTokenSource();
await pipe.ConnectAsync(15000, lifetime.Token);
using var reader = new StreamReader(pipe, leaveOpen: true);
await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
var commands = Channel.CreateBounded<WorkerInput>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
LiveSnapshot? latest = null;
var input = Task.Run(async () =>
{
    try
    {
        while (await reader.ReadLineAsync(lifetime.Token) is { } line)
        {
            if (line.Length > 2 * 1024 * 1024) { throw new IOException("Oversized worker input."); }
            var value = JsonSerializer.Deserialize<WorkerInput>(line, LiveJson.Options) ?? throw new IOException("Missing worker input.");
            if (value.Snapshot is not null) { value.Snapshot.Validate(); Volatile.Write(ref latest, value.Snapshot); }
            if (value.Command != "state") { await commands.Writer.WriteAsync(value, lifetime.Token); }
        }
    }
    finally { lifetime.Cancel(); commands.Writer.TryComplete(); }
}, lifetime.Token);
StandalonePublisher? standalone = null;
FisPublisher? fis = null;
Process? localServer = null;
PublisherOptions? options = null;
var health = new PublisherHealth(PublisherState.Stopped);
var running = false; var refresh = true; LiveSnapshot? published = null;
var nextAttempt = DateTimeOffset.MinValue; var nextHealth = DateTimeOffset.MinValue; var retries = 0;
var log = new ProtocolLog();
log.Write("Publisher process started");
var localSigningKey = "";
var localPublisherKey = ""; // Private to this worker and its managed loopback server unless the caller supplies one.
try
{
    while (!lifetime.IsCancellationRequested)
    {
        try
        {
            if (commands.Reader.TryRead(out var command))
            {
                switch (command.Command)
                {
                    case "start":
                        if (options is null)
                        {
                            options = command.Options ?? throw new LiveValidationException("Publisher options missing.");
                            localSigningKey = options.LocalSigningKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                            localPublisherKey = options.Kind == PublisherKind.Local && options.PublisherKey is { Length: > 0 } supplied ? supplied : LivePublisherKey.Generate();
                            health = health with { Endpoint = options.Endpoint, State = PublisherState.Starting, Error = null };
                            await SendHealth();
                            if (options.Kind == PublisherKind.Local)
                            {
                                localServer = StartLocalServer(options, localSigningKey, localPublisherKey);
                                standalone = new(options.Endpoint, options.ResumeSession, log.Write, localPublisherKey);
                            }
                            else if (options.Kind == PublisherKind.Cloud) { standalone = new(options.Endpoint, options.ResumeSession, log.Write, options.PublisherKey); }
                            else
                            {
                                IFisLiveTimingTransport transport = options.Kind == PublisherKind.FisTcp
                                    ? new FisTcpTransport(options.Endpoint, options.TcpPort, log.Write)
                                    : new FisHttpsTransport(options.Endpoint, log.Write);
                                fis = new(transport, options.FisPassword);
                            }
                        }
                        else if (options.Kind == PublisherKind.Local && localServer?.HasExited != false)
                        { localServer?.Dispose(); localServer = StartLocalServer(options, localSigningKey, localPublisherKey); }
                        running = true; refresh = true; nextAttempt = DateTimeOffset.MinValue; retries = 0;
                        health = health with { State = PublisherState.Starting, Error = null }; log.Write("Publisher start");
                        break;
                    case "stop":
                        running = false; health = health with { State = PublisherState.Stopped, Error = null };
                        await SendHealth(); log.Write("Publisher stopped");
                        if (standalone is not null) { await standalone.PauseAsync(lifetime.Token); }
                        break;
                    case "refresh":
                        refresh = true; nextAttempt = DateTimeOffset.MinValue;
                        if (options?.Kind == PublisherKind.Local && localServer?.HasExited != false)
                        { localServer?.Dispose(); localServer = StartLocalServer(options, localSigningKey, localPublisherKey); }
                        running = true; break;
                    case "delete":
                    case "delete-all":
                        running = false;
                        if (standalone is not null) { await standalone.DeleteAsync(command.Command == "delete-all", lifetime.Token); }
                        health = health with { State = PublisherState.Stopped, PublicUrl = null, ExpiresAt = null, Error = null };
                        log.Write("Session deleted and all session runtime data cleared"); break;
                    case "shutdown": lifetime.Cancel(); break;
                    default: throw new LiveValidationException("Unknown worker command.");
                }
                await SendHealth();
            }
            var now = DateTimeOffset.UtcNow;
            if (running && now >= nextAttempt)
            {
                if (options?.Kind == PublisherKind.Local && localServer?.HasExited == true)
                {
                    health = health with { State = PublisherState.Reconnecting, Error = "Local server exited; restarting and restoring snapshot." }; await SendHealth();
                    localServer.Dispose(); localServer = StartLocalServer(options, localSigningKey, localPublisherKey); refresh = true;
                }
                var state = Volatile.Read(ref latest);
                if (state is not null && (refresh || !ReferenceEquals(state, published)))
                {
                    var sending = state with { Paused = false };
                    if (standalone is not null) { await standalone.PublishAsync(sending, refresh, lifetime.Token); }
                    if (fis is not null) { await fis.PublishAsync(sending, refresh, lifetime.Token); }
                    published = state; refresh = false; retries = 0;
                    health = health with { State = PublisherState.Running, Error = null, LastConnected = now,
                        LastSuccessfulPublish = DateTimeOffset.UtcNow, LastEvent = state.UpdatedAt,
                        PublicUrl = standalone?.Session?.PublicUrl, ExpiresAt = standalone?.Session?.ExpiresAt };
                    log.Write("Authoritative state published"); nextHealth = now.AddSeconds(5); await SendHealth();
                }
                else if (now >= nextHealth && published is not null)
                {
                    if (standalone is not null)
                    { await standalone.HealthAsync(lifetime.Token); health = health with { State = PublisherState.Running, Error = null, LastConnected = now }; }
                    // FIS recommends keepalive after 5–10 minutes of inactivity (v53 p75).
                    if (fis is not null && now - health.LastSuccessfulPublish >= TimeSpan.FromMinutes(5))
                    { await fis.KeepAliveAsync(lifetime.Token); health = health with { State = PublisherState.Running, Error = null, LastSuccessfulPublish = now, LastConnected = now }; }
                    nextHealth = now.AddSeconds(5); await SendHealth();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or LiveValidationException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            if (lifetime.IsCancellationRequested) { break; }
            // Never expose raw exception messages: an endpoint/server response could echo a credential.
            var permanent = ex is LiveValidationException || (ex is HttpRequestException request && request.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest);
            health = health with { State = running && !permanent ? PublisherState.Reconnecting : PublisherState.Error,
                // LiveValidationException text is authored by OpenSkiTime, never echoed from a server response.
                Error = ex is LiveValidationException validation ? validation.Message
                    : permanent ? "Configuration or authentication rejected. Check settings and restart." : "Publish/health failed. Check endpoint and network; retrying." };
            if (permanent) { running = false; }
            refresh = true; fis?.Disconnect();
            nextAttempt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 1 << Math.Min(retries++, 5)));
            log.Write(health.Error); await SendHealth();
        }
        await Task.Delay(100, lifetime.Token);
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
finally
{
    lifetime.Cancel(); standalone?.Dispose(); if (fis is not null) { await fis.DisposeAsync(); }
    if (localServer is not null) { if (!localServer.HasExited) { localServer.Kill(entireProcessTree: true); } localServer.Dispose(); }
    log.Dispose();
    try { await input; } catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or LiveValidationException) { }
}
return 0;

Task SendHealth() => writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerOutput(health, standalone is null ? options?.ResumeSession : standalone.Session), LiveJson.Options));
static Process StartLocalServer(PublisherOptions options, string signingKey, string publisherKey)
{
    var endpoint = new Uri(options.Endpoint);
    if (!endpoint.IsLoopback || endpoint.Scheme != "http") { throw new LiveValidationException("Managed local server must use loopback HTTP."); }
    var assembly = options.LocalServerAssembly ?? throw new LiveValidationException("Local server artifact missing.");
    var executable = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
    var useAppHost = File.Exists(executable);
    var start = new ProcessStartInfo(useAppHost ? executable : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(assembly)! };
    if (!useAppHost) { start.ArgumentList.Add(assembly); }
    start.ArgumentList.Add("--urls"); start.ArgumentList.Add(options.Endpoint);
    start.Environment["LiveTiming__SigningKey"] = signingKey;
    start.Environment["LiveTiming__PublisherKeys"] = "local:" + LivePublisherKey.Hash(publisherKey);
    start.Environment["LiveTiming__PublicBaseUrl"] = options.Endpoint;
    start.Environment["Logging__LogLevel__Default"] = "Warning";
    return Process.Start(start) ?? throw new IOException("Local server process could not start.");
}

internal sealed class ProtocolLog : IDisposable
{
    private readonly string _path;
    private StreamWriter? _writer;
    public ProtocolLog()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime", "LiveTimingLogs");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, $"publisher-{Environment.ProcessId}.log");
    }
    public void Write(string text)
    {
        try
        {
            if (_writer is not null && _writer.BaseStream.Length > 4 * 1024 * 1024)
            { _writer.Dispose(); _writer = null; File.Move(_path, _path + ".previous", true); }
            _writer ??= new StreamWriter(_path, append: true) { AutoFlush = true };
            _writer.WriteLine($"{DateTimeOffset.UtcNow:O} {text}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _writer?.Dispose(); _writer = null; }
    }
    public void Dispose() => _writer?.Dispose();
}

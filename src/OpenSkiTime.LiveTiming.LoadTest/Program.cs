using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using OpenSkiTime.LiveTiming.LoadTest;

// Usage: --origin <https://server> (--session <id> | --control <blob container SAS URL>) [--viewers 900] [--ramp-seconds 120]
//        [--minutes 40] [--no-page] [--label <name>] [--output <folder>] [--whoami <URL answering with the caller's address>]
// With --control, several generators started ahead of the race wait for the blob session.txt (the session ID), stop when
// the blob stop appears and upload their results under results/<label>/. Every run stops after --minutes or on
// Ctrl+C/SIGTERM and then writes report.md, states.csv.gz and attempts.csv.gz.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine("Unexpected argument " + args[i]); return 2; }
    options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
}
Uri? control = options.TryGetValue("control", out var controlText) ? new Uri(controlText) : null;
var session = Guid.Empty;
if (!options.TryGetValue("origin", out var originText) || !Uri.TryCreate(originText, UriKind.Absolute, out var origin)
    || (control is null && !(options.TryGetValue("session", out var sessionText) && Guid.TryParse(sessionText, out session))))
{
    Console.Error.WriteLine("Usage: --origin <https://server> (--session <id> | --control <container SAS URL>) [--viewers 900] [--ramp-seconds 120] [--minutes 40] [--no-page] [--label <name>] [--output <folder>] [--whoami <URL>]");
    return 2;
}
int Number(string name, int fallback) => options.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
var viewers = Number("viewers", 900);
var ramp = TimeSpan.FromSeconds(Number("ramp-seconds", 120));
var duration = TimeSpan.FromMinutes(Number("minutes", 40));
var label = options.GetValueOrDefault("label", Environment.MachineName);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = int.MaxValue,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    AutomaticDecompression = DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(30),
}) { Timeout = TimeSpan.FromSeconds(60) };

// Server limits apply per client address; with several generators each prints its public address to show the split.
async Task WhoAmI() { if (options.TryGetValue("whoami", out var whoami)) { Console.WriteLine($"{label} public address " + (await http.GetStringAsync(new Uri(whoami))).Trim()); } }
Uri Blob(string name) => new UriBuilder(control!) { Path = control!.AbsolutePath.TrimEnd('/') + "/" + name }.Uri;
async Task<string?> ReadBlob(string name)
{
    using var response = await http.GetAsync(Blob(name));
    return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
}
await WhoAmI();
if (control is not null)
{
    Console.WriteLine($"{label} waiting for session.txt");
    while (!stop.IsCancellationRequested && !Guid.TryParse((await ReadBlob("session.txt"))?.Trim(), out session))
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stop.Token); }
        catch (OperationCanceledException) { return 1; }
    }
    await WhoAmI();
    _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); if (await ReadBlob("stop") is not null) { await stop.CancelAsync(); } }
            catch (OperationCanceledException) { return; }
            catch (HttpRequestException) { /* Checked again in five seconds. */ }
        }
    });
}
stop.CancelAfter(duration);

var recorder = new LoadRecorder();
var started = DateTime.UtcNow;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{started:HH:mm:ss} {label}: {viewers} viewers on {origin} session {session}, ramp {ramp.TotalSeconds:0} s, at most {duration.TotalMinutes:0} min"));
var tasks = new List<Task>();
var status = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
    try { while (await timer.WaitForNextTickAsync(stop.Token)) { Console.WriteLine(recorder.Status()); } }
    catch (OperationCanceledException) { }
});
for (var i = 0; i < viewers && !stop.IsCancellationRequested; i++)
{
    var viewer = new Viewer(i, origin, session, http, recorder, !options.ContainsKey("no-page"));
    tasks.Add(Task.Run(() => viewer.RunAsync(stop.Token)));
    try { await Task.Delay(ramp / viewers, stop.Token); }
    catch (OperationCanceledException) { }
}
await Task.WhenAll(tasks);
await status;
var ended = DateTime.UtcNow;
Console.WriteLine(recorder.Status());

var run = recorder.Snapshot();
var report = LoadReport.Markdown(run, origin, session, viewers, started, ended);
var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
{
    ["report.md"] = Encoding.UTF8.GetBytes(report),
    ["states.csv.gz"] = Gzip(run.StatesCsv()),
    ["attempts.csv.gz"] = Gzip(run.AttemptsCsv()),
};
Console.WriteLine("----- report.md -----");
Console.WriteLine(report);
if (options.TryGetValue("output", out var output))
{
    Directory.CreateDirectory(output);
    foreach (var (name, bytes) in files) { await File.WriteAllBytesAsync(Path.Combine(output, name), bytes); }
}
if (control is not null)
{
    foreach (var (name, bytes) in files)
    {
        using var content = new ByteArrayContent(bytes);
        using var request = new HttpRequestMessage(HttpMethod.Put, Blob($"results/{label}/{name}")) { Content = content };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        using var response = await http.SendAsync(request);
        Console.WriteLine($"Uploaded {name}: {(int)response.StatusCode}");
    }
}
return 0;

static byte[] Gzip(string text)
{
    using var memory = new MemoryStream();
    using (var zip = new GZipStream(memory, CompressionLevel.Optimal)) { zip.Write(Encoding.UTF8.GetBytes(text)); }
    return memory.ToArray();
}

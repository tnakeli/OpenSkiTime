using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace OpenSkiTime.LiveTiming.Tests;

internal static class ProcessFixture
{
    public static string Host => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    public static string Artifact(string project)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        // The SDK artifacts layout allows validation while the normal desktop build is open.
        var sibling = Path.Combine(directory.Parent?.Parent?.FullName ?? directory.FullName,
            "OpenSkiTime.LiveTiming." + project, directory.Name, $"OpenSkiTime.LiveTiming.{project}.dll");
        if (File.Exists(sibling)) { return sibling; }
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,"OpenSkiTime.LiveTiming.slnx"))) { directory = directory.Parent; }
        if (directory is null) { throw new FileNotFoundException("Live timing build artifacts were not found. Build the solution before running process tests."); }
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(directory.FullName,"src", "OpenSkiTime.LiveTiming." + project,"bin",config,"net10.0",$"OpenSkiTime.LiveTiming.{project}.dll");
    }
    public static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback,0); listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    public static async Task Until(Func<bool> condition, int seconds = 20)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) { await Task.Delay(50,timeout.Token); }
    }
}
internal sealed class ServerProcess(int maxSessions = 100) : IAsyncDisposable
{
    private Process? _process;
    private readonly string _key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public string Endpoint { get; } = $"http://127.0.0.1:{ProcessFixture.Port()}";
    public string SigningKey => _key;
    public async Task Start()
    {
        var assembly = ProcessFixture.Artifact("Server");
        var start = new ProcessStartInfo(ProcessFixture.Host) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(assembly)!, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(assembly); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(Endpoint);
        start.Environment["LiveTiming__SigningKey"] = _key; start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["LiveTiming__MaxSessions"] = maxSessions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_,_) => { }; _process.ErrorDataReceived += (_,_) => { };
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            try { using var response = await client.GetAsync(Endpoint + "/health",timeout.Token); if (response.IsSuccessStatusCode) { return; } }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!timeout.IsCancellationRequested) { }
            if (_process.HasExited) { throw new IOException("Server process failed to start."); }
            await Task.Delay(100,timeout.Token);
        }
    }
    public async Task Kill()
    {
        if (_process is null) { return; }
        if (!_process.HasExited) { _process.Kill(true); await _process.WaitForExitAsync(); }
        _process.Dispose(); _process = null;
    }
    public async ValueTask DisposeAsync() => await Kill();
}

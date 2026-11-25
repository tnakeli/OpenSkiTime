using System.Diagnostics;
using System.Text;
using OpenSkiTime.Application;

namespace OpenSkiTime.Devices;

// The vendor library runs in a small isolated Windows host (.NET Framework) that forwards both original SDK byte/text
// fields, not parsed timing lines. No vendor binary is redistributed with this repository or downloaded by the
// application: the operator installs ALGE's driver and copies ALGE's library to SdkDirectory (docs/timing.md).
// With retry (live capture), a missing library or a stopped host is reported and tried again until capture stops, so
// timing starts by itself once the problem is fixed. A device read does not retry.
public sealed class TimyUsbSource(string? deviceId = null, string? sdkDirectory = null, bool retry = false, TimeSpan? retryDelay = null) : ITimingSource
{
    // ALGE's current library (2026.10.1) and the mixed-mode x64 library it replaced.
    private static readonly string[] s_libraries = ["AlgeTimyUsb.dll", "AlgeTimyUsb.x64.dll"];
    private int _stream;
    public static string SdkDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime", "timy-usb");

    public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receive); ArgumentNullException.ThrowIfNull(status);
        var reported = "";
        while (true)
        {
            // Receiving again ends the reported problem, so the next interruption is journalled even if it is the same one.
            try { await ReceiveOnceAsync(packet => { reported = ""; return receive(packet); }, status, ct); return; }
            catch (Exception ex) when (retry && !ct.IsCancellationRequested && ex is IOException or System.ComponentModel.Win32Exception)
            {
                status("Not receiving · " + ex.Message + " Retrying…");
                // One notice per problem: the journal must not fill up while the operator fixes it.
                if (reported != ex.Message)
                {
                    reported = ex.Message;
                    await receive(new("transport-status", "Timy USB", "failure", Encoding.UTF8.GetBytes("Timy USB is not receiving. " + ex.Message)));
                }
                await Task.Delay(retryDelay ?? TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private async Task ReceiveOnceAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { throw new IOException("Timy USB requires 64-bit Windows. Use the MT1 serial adapter on other platforms."); }
        var folder = sdkDirectory ?? SdkDirectory;
        if (!s_libraries.Any(x => File.Exists(Path.Combine(folder, x))))
        { throw new IOException($"ALGE's USB library is missing. Copy AlgeTimyUsb.dll to {folder} (see the installation guide)."); }
        var host = Path.Combine(AppContext.BaseDirectory, "TimyUsbHost.exe");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = folder
        };
        foreach (var argument in new[] { folder, deviceId ?? "" })
        { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start) ?? throw new IOException("The Timy USB host could not start.");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var watchdogDone = new CancellationTokenSource();
        var forcedStop = false;
        var watchdog = Task.Run(async () =>
        {
            try
            {
                var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = ct.Register(() => stopped.TrySetResult());
                await stopped.Task.WaitAsync(watchdogDone.Token);
                await Task.Delay(TimeSpan.FromSeconds(10), watchdogDone.Token);
                if (!process.HasExited) { forcedStop = true; process.Kill(entireProcessTree: true); }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
        using var stop = ct.Register(() =>
        {
            try { if (!process.HasExited) { process.StandardInput.WriteLine("STOP"); process.StandardInput.Flush(); } }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
        });
        try
        {
            // STOP asks the helper to finish; keep reading already delivered byte events until EOF.
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                var parts = line.Split('\t', 3);
                if (parts.Length != 3) { continue; }
                if (parts[0] == "T")
                { await receive(new("alge-timy-sdk/v1", "Timy:" + parts[1], _stream.ToString(System.Globalization.CultureInfo.InvariantCulture), Convert.FromBase64String(parts[2]))); }
                else if (parts[0] == "S") { status(parts[2]); }
                else if (parts[0] == "C") { _stream++; status("Connected · Timy " + parts[1]); }
                else if (parts[0] == "D")
                {
                    status("Timy disconnected · reconnect USB to resume");
                    await receive(new("transport-status", "Timy:" + parts[1], "disconnect", Encoding.UTF8.GetBytes("Timy USB disconnected. Recover impulses from device memory/backup; review clock continuity.")));
                }
                else if (parts[0] == "E") { throw new IOException(parts[2]); }
            }
            if (forcedStop)
            { await receive(new("transport-status", "Timy", "shutdown", Encoding.UTF8.GetBytes("USB helper did not stop cleanly. Check device memory/backup for missing impulses."))); }
            if (!ct.IsCancellationRequested)
            {
                await process.WaitForExitAsync(ct);
                _ = await stderr; // Never put a full native exception / arbitrary device content into diagnostics.
                throw new IOException("Timy USB host stopped. Check the ALGE driver, USB library and Windows application-control notification.");
            }
        }
        finally
        {
            watchdogDone.Cancel();
            await watchdog;
            if (!process.HasExited)
            {
                try { process.StandardInput.WriteLine("STOP"); process.StandardInput.Flush(); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); }
            }
            _ = await stderr;
        }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

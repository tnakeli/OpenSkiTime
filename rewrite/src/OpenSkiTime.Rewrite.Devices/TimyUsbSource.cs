using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OpenSkiTime.Rewrite.Application;

namespace OpenSkiTime.Rewrite.Devices;

// The vendor's mixed-mode .NET Framework DLL cannot load in .NET 10. A small isolated Windows host
// forwards both original SDK byte/text fields, not parsed timing lines. No vendor binary is redistributed with this repository.
public sealed class TimyUsbSource(string? deviceId = null, string? sdkDirectory = null) : ITimingSource
{
    public static string SdkDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime", "timy-usb");
    public static bool IsSdkInstalled => File.Exists(Path.Combine(SdkDirectory, "AlgeTimyUsb.x64.dll"));

    public static async Task InstallSdkAsync(HttpClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Timy native USB currently requires Windows. MT1 serial capture remains portable."); }
        using var response = await client.GetAsync("https://alge-timing.com/alge/download/software/AlgeTimyUsbDLLExample.zip", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > 40_000_000) { throw new IOException("The Timy SDK download exceeds the size limit."); }
            output.Write(buffer, 0, read);
        }
        output.Position = 0;
        using var zip = new ZipArchive(output);
        Directory.CreateDirectory(SdkDirectory);
        foreach (var name in new[] { "AlgeTimyUsb.x64.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
        {
            var expected = name.StartsWith("Alge", StringComparison.Ordinal) ? "Dependencies/" + name : "Dependencies/x64/" + name;
            var entry = zip.Entries.SingleOrDefault(e => e.FullName.Replace('\\', '/') == expected)
                ?? throw new IOException("The vendor USB package has changed. Check the ALGE download before installing.");
            if (entry.Length > 20_000_000) { throw new IOException("Unexpected USB component size."); }
            var destination = Path.Combine(SdkDirectory, name);
            var temporary = destination + ".tmp";
            await using (var stream = entry.Open())
            await using (var target = File.Create(temporary)) { await stream.CopyToAsync(target, ct); }
            File.Move(temporary, destination, true);
        }
    }

    public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receive); ArgumentNullException.ThrowIfNull(status);
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) { throw new IOException("Timy USB requires 64-bit Windows. Use the MT1 serial adapter on other platforms."); }
        var folder = sdkDirectory ?? SdkDirectory;
        if (!File.Exists(Path.Combine(folder, "AlgeTimyUsb.x64.dll")))
        { throw new IOException("Set up the Timy USB library in device settings first, and install the ALGE USB driver."); }
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
        var stream = 0;
        try
        {
            // STOP asks the helper to finish; keep reading already delivered byte events until EOF.
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                var parts = line.Split('\t', 3);
                if (parts.Length != 3) { continue; }
                if (parts[0] == "T")
                { await receive(new("alge-timy-sdk/v1", "Timy:" + parts[1], stream.ToString(System.Globalization.CultureInfo.InvariantCulture), Convert.FromBase64String(parts[2]))); }
                else if (parts[0] == "S") { status(parts[2]); }
                else if (parts[0] == "C") { stream++; status("Connected · Timy " + parts[1]); }
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

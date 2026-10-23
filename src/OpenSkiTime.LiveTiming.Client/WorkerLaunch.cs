using System.Diagnostics;

namespace OpenSkiTime.LiveTiming.Client;

/// <summary>Starts a live timing worker with its private pipe name. Secrets travel only through the pipe.</summary>
internal static class WorkerLaunch
{
    public static ProcessStartInfo Create(string workerAssembly, string pipeName, string? dotnetHost)
    {
        var executable = Path.ChangeExtension(workerAssembly, OperatingSystem.IsWindows() ? ".exe" : null);
        var useAppHost = dotnetHost is null && File.Exists(executable);
        var host = dotnetHost ?? ResolveDotnetHost();
        var start = new ProcessStartInfo(useAppHost ? executable : host) { UseShellExecute = false, CreateNoWindow = true };
        if (!useAppHost) { start.ArgumentList.Add(workerAssembly); start.Environment["DOTNET_HOST_PATH"] = host; }
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        return start;
    }
    private static string ResolveDotnetHost()
    {
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitHost)) { return explicitHost; }
        var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtime.Parent?.Parent?.Parent;
        var path = root is null ? "" : Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(path) ? path : "dotnet";
    }
}

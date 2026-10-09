namespace OpenSkiTime.Desktop;

// Per-user files outside every event series: preferences, recent files, category presets and downloaded FIS caches.
// OPENSKITIME_LOCAL_DATA redirects them to another directory, so automated or demonstration runs never read or
// overwrite the operator's own FIS cache or preferences. Series files are unaffected.
public static class LocalDataDirectory
{
    public const string OverrideVariable = "OPENSKITIME_LOCAL_DATA";

    public static string Path => Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom
        ? System.IO.Path.GetFullPath(custom)
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime");
}

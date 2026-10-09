using System.Text.Json;

namespace OpenSkiTime.Desktop;

public sealed class RecentSeriesStore(string? localDataDirectory = null)
{
    private const int MaxEntries = 10;
    private readonly string _path = Path.Combine(localDataDirectory ?? LocalDataDirectory.Path,
        "recent-series.json");
    private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_path)) { return []; }
        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? [];
            return paths.Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                    && File.Exists(path))
                .Distinct(s_pathComparer).Take(MaxEntries).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Record(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var paths = new[] { fullPath }.Concat(Load())
            .Distinct(s_pathComparer).Take(MaxEntries).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(paths));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

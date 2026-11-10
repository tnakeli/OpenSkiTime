using System.Text.Json;

namespace OpenSkiTime.Desktop;

public sealed class UiPreferencesStore(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? LocalDataDirectory.Path, "ui-preferences.json");
    private sealed record Document(int Version, bool AdvancedMode);

    public bool Load()
    {
        if (!File.Exists(_path)) { return false; }
        var saved = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
        return saved is { Version: 1, AdvancedMode: true };
    }

    public void Save(bool advancedMode)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, new Document(1, advancedMode)); stream.Flush(flushToDisk: true); }
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

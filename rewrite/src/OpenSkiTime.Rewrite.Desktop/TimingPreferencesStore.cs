using System.Text.Json;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed record TimingPreferences(string Source, string Port, string UsbId, int Baud,
    int StartChannel, int FinishChannel, string IntermediateChannels, string Firmware);

// Machine preferences only. Secrets stay in Credential Manager, timing data in the event file.
public sealed class TimingPreferencesStore(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime"), "timing-settings.json");

    public TimingPreferences? Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<TimingPreferences>(File.ReadAllText(_path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(TimingPreferences settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, _path, overwrite: true);
    }
}

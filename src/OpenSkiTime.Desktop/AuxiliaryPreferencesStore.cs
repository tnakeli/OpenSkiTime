using System.Text.Json;

namespace OpenSkiTime.Desktop;

public sealed record AuxiliaryPreferences(string Source, string Port, string UsbId, int Baud, int StartChannel,
    int FinishChannel, string Firmware, string StartDevice, string FinishDevice, string Username,
    int BackupStartWarningMilliseconds = 1, int BackupFinishWarningMilliseconds = 10, int BackupMissingGraceSeconds = 5,
    int? AuxiliaryClockOffsetMinutes = null);

public sealed class AuxiliaryPreferencesStore(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime"), "auxiliary-settings.json");
    public AuxiliaryPreferences? Load() => File.Exists(_path)
        ? JsonSerializer.Deserialize<AuxiliaryPreferences>(File.ReadAllText(_path)) : null;
    public void Save(AuxiliaryPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, _path, overwrite: true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

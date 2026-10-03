using System.Text.Json;
using OpenSkiTime.Rewrite.Application;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed record FisTimingDeviceCatalogue(DateTimeOffset RetrievedAt, FisTimingDevice[] Devices);

public sealed class FisTimingDeviceCache(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime"), "fis-timing-devices.json");

    public FisTimingDeviceCatalogue? Load()
    {
        if (!File.Exists(_path)) { return null; }
        var value = JsonSerializer.Deserialize<FisTimingDeviceCatalogue>(File.ReadAllText(_path))
            ?? throw new IOException("Saved FIS equipment catalogue is unreadable. Refresh it in Settings.");
        if (value.Devices is null) { throw new IOException("Saved FIS equipment catalogue is incomplete. Refresh it in Settings."); }
        FisTimingDeviceClient.Validate(value.Devices);
        return value;
    }

    public void Save(FisTimingDeviceCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        FisTimingDeviceClient.Validate(catalogue.Devices);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(catalogue)); File.Move(temporary, _path, overwrite: true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}

using System.Text.Json;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed class CategoryRulePresetStore(string? localDataDirectory = null)
{
    private readonly string _path = Path.Combine(localDataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime"),
        "category-rules.json");

    public async Task SaveAsync(IReadOnlyList<CategoryRuleValues> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0) { throw new DomainValidationException("Add category rules before saving a reusable set."); }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Preset(1, rules)));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public async Task<IReadOnlyList<CategoryRuleValues>> LoadAsync()
    {
        if (!File.Exists(_path)) { throw new DomainValidationException("No saved category rule set was found on this computer."); }
        Preset? preset;
        try { preset = JsonSerializer.Deserialize<Preset>(await File.ReadAllTextAsync(_path)); }
        catch (JsonException) { throw new DomainValidationException("The saved category rule set cannot be read."); }
        if (preset is not { Version: 1, Rules: { Count: > 0 } })
        {
            throw new DomainValidationException("The saved category rule set is empty or unsupported.");
        }
        return preset.Rules;
    }

    private sealed record Preset(int Version, IReadOnlyList<CategoryRuleValues> Rules);
}

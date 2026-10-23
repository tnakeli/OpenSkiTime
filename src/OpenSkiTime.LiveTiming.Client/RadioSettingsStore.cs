using System.Text.Json;

namespace OpenSkiTime.LiveTiming.Client;

public sealed record RadioCompetitionSettings(RadioConfiguration Configuration, decimal SpentUsd);

/// <summary>
/// Per-competition radio settings and spend in this Windows user's local application data (live-radio.json), keyed by
/// competition ID. They are operator preferences tied to this computer's provider keys and spending, not race data: the
/// series file is unchanged (no schema change, see docs/development-database-policy.md) and a transferred series starts
/// with radio off. An unreadable file is never overwritten; saving reports an error instead.
/// </summary>
public sealed class RadioSettingsStore(string path)
{
    public const string FileName = "live-radio.json";
    private readonly Lock _gate = new();
    public string Path { get; } = path;
    /// <summary>The user's local OpenSkiTime folder, or OPENSKITIME_LOCAL_DATA for automated and demonstration runs.</summary>
    public static string DefaultPath => System.IO.Path.Combine(Environment.GetEnvironmentVariable("OPENSKITIME_LOCAL_DATA") is { Length: > 0 } custom
        ? System.IO.Path.GetFullPath(custom)
        : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime"), FileName);

    public RadioCompetitionSettings Read(Guid competitionId)
    {
        lock (_gate)
        {
            try { return Load().GetValueOrDefault(competitionId) ?? new(new RadioConfiguration(), 0m); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(new RadioConfiguration(), 0m); }
        }
    }
    public void Save(Guid competitionId, RadioConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        Update(competitionId, x => x with { Configuration = configuration });
    }
    /// <summary>Spend only grows, so a stale report can never lower the recorded total below what was charged.</summary>
    public void SaveSpend(Guid competitionId, decimal spent) => Update(competitionId, x => x with { SpentUsd = Math.Max(x.SpentUsd, spent) });

    private void Update(Guid competitionId, Func<RadioCompetitionSettings, RadioCompetitionSettings> change)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // Two OpenSkiTime instances of one Windows user share the file: read, change and replace under a file lock.
            using var exclusive = Lock();
            Dictionary<Guid, RadioCompetitionSettings> all;
            try { all = Load(); }
            catch (JsonException) { throw new IOException($"Radio settings in {Path} are unreadable. Rename the file to start again."); }
            all[competitionId] = change(all.GetValueOrDefault(competitionId) ?? new(new RadioConfiguration(), 0m));
            var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(all, LiveJson.Options));
                File.Move(temporary, Path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
    }
    private FileStream Lock()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 40) { Thread.Sleep(50); }
        }
    }
    private Dictionary<Guid, RadioCompetitionSettings> Load() => File.Exists(Path)
        ? JsonSerializer.Deserialize<Dictionary<Guid, RadioCompetitionSettings>>(File.ReadAllText(Path), LiveJson.Options) ?? []
        : [];
}

/// <summary>Credential Manager locations of radio provider keys. Never stored in the series, preferences or logs.</summary>
public static class RadioKeyCredential
{
    public static readonly string[] Providers = [RadioProviders.OpenAi, RadioProviders.Gemini, RadioProviders.Azure, RadioProviders.Google];
    public static string Target(string provider) => Providers.Contains(provider)
        ? "OpenSkiTime.LiveTiming.Radio:" + provider : throw new ArgumentException("Unknown radio provider.", nameof(provider));
    public static string Label(string provider) => provider switch
    {
        RadioProviders.OpenAi => "OpenAI", RadioProviders.Gemini => "Google Gemini API", RadioProviders.Azure => "Azure AI Speech",
        RadioProviders.Google => "Google Cloud Text-to-Speech", _ => provider
    };
    public static void Validate(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length is < 16 or > 512 || key.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        { throw new ArgumentException("Enter the complete API key from the provider (at least 16 characters, no spaces).", nameof(key)); }
    }
}

/// <summary>Where radio clips go: every enabled standalone channel that is running with a valid session.</summary>
public static class RadioTargets
{
    public static RadioTarget[] Select(RadioConfiguration configuration, IEnumerable<(PublisherKind Kind, PublisherHealth Health, string? Endpoint, LiveSession? Session)> channels, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(configuration); ArgumentNullException.ThrowIfNull(channels);
        return channels.Where(x => x.Kind is PublisherKind.Local or PublisherKind.Cloud && configuration.Enabled(x.Kind)
                && x.Health.State == PublisherState.Running && x.Endpoint is not null && x.Session is { } session && session.ExpiresAt > now)
            .Select(x => new RadioTarget(x.Kind, x.Endpoint!, x.Session!.SessionId, x.Session.PublisherToken)).ToArray();
    }
}

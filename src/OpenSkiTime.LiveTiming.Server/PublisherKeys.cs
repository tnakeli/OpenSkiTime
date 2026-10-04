using System.Security.Cryptography;
using System.Text;

namespace OpenSkiTime.LiveTiming.Server;

/// <summary>
/// Publisher keys allowed to create live sessions. Configuration holds only names and SHA-256 hashes,
/// so leaked server configuration cannot be used to publish.
/// </summary>
public sealed class PublisherKeys
{
    public const int MinimumKeyLength = 32;
    private readonly (string Name, byte[] Hash)[] _keys;
    public PublisherKeys(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _keys = Parse(config["LiveTiming:PublisherKeys"]);
    }
    public static (string Name, byte[] Hash)[] Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        { throw new InvalidOperationException("LiveTiming publisher keys are required (name:sha256hex entries separated by ';')."); }
        var keys = new List<(string Name, byte[] Hash)>();
        foreach (var entry in value.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.LastIndexOf(':');
            var name = separator > 0 ? entry[..separator].Trim() : "";
            var hex = separator > 0 ? entry[(separator + 1)..].Trim() : "";
            if (name.Length is 0 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-'))
            { throw new InvalidOperationException("Each publisher key needs a name of 1-64 letters, digits, spaces, '.', '_' or '-'."); }
            if (hex.Length != 64 || !hex.All(char.IsAsciiHexDigit))
            { throw new InvalidOperationException($"Publisher key '{name}' needs a 64-character SHA-256 hex hash."); }
            if (keys.Any(x => StringComparer.OrdinalIgnoreCase.Equals(x.Name, name)))
            { throw new InvalidOperationException($"Publisher key name '{name}' is duplicated."); }
            keys.Add((name, Convert.FromHexString(hex)));
        }
        if (keys.Count == 0) { throw new InvalidOperationException("At least one LiveTiming publisher key is required."); }
        return keys.ToArray();
    }
    /// <summary>Returns the publisher name for a valid key, comparing every entry in constant time.</summary>
    public string? Authenticate(string? key)
    {
        if (key is null || key.Length is < MinimumKeyLength or > 512) { return null; }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string? match = null;
        foreach (var entry in _keys)
        { if (CryptographicOperations.FixedTimeEquals(hash, entry.Hash)) { match = entry.Name; } }
        return match;
    }
}

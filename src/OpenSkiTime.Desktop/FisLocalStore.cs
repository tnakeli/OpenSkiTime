using System.Net;

namespace OpenSkiTime.Desktop;

// Credentials belong to the current Windows user. The original ZIP is kept outside event files and the repository.
public sealed class FisLocalStore
{
    private readonly ICredentialStore _credential;
    private readonly ICredentialStore _memberCredential;
    private readonly string _archivePath;

    // Two different FIS credentials: the public API key (api.fis-ski.com, X-Api-Key) and the FIS Member Section personal
    // access token (profile.fis-ski.com/api, bearer) that submits competition files. One does not work for the other.
    // An injected API credential without an injected member credential gets an in-memory token store, so a test never
    // reads the real Windows credential.
    public FisLocalStore(string? localDataDirectory = null, ICredentialStore? apiCredential = null, ICredentialStore? memberCredential = null)
    {
        _credential = apiCredential ?? new WindowsCredentialStore("OpenSkiTime.FIS.ApiKey");
        _memberCredential = memberCredential ?? (apiCredential is null ? new WindowsCredentialStore("OpenSkiTime.FIS.MemberToken", compactBlob: true) : new MemoryCredentialStore());
        var directory = localDataDirectory ?? LocalDataDirectory.Path;
        _archivePath = Path.Combine(directory, "fis-points-list.zip");
    }

    public byte[]? LoadList() => File.Exists(_archivePath) ? File.ReadAllBytes(_archivePath) : null;

    public async Task SaveListAsync(byte[] bytes)
    {
        var directory = Path.GetDirectoryName(_archivePath)!;
        Directory.CreateDirectory(directory);
        var temporary = _archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, _archivePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public bool HasApiKey() => _credential.Exists();
    public string? ReadApiKey() => _credential.Read();
    public void SaveApiKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _credential.Save(key.Trim());
    }
    public void RemoveApiKey() => _credential.Remove();

    public bool HasMemberToken() => _memberCredential.Exists();
    public string? ReadMemberToken() => _memberCredential.Read();
    public void SaveMemberToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        _memberCredential.Save(token.Trim());
    }
    public void RemoveMemberToken() => _memberCredential.Remove();

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private string? _value;
        public bool Exists() => _value is not null;
        public string? Read() => _value;
        public void Save(string key) => _value = key;
        public void Remove() => _value = null;
    }
}
public sealed class FisPointsDownloader(HttpClient client)
{
    public async Task<byte[]> DownloadAsync(DateOnly effectiveDate, string apiKey, CancellationToken cancellationToken = default)
    {
        var date = effectiveDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.fis-ski.com/data-feeds/fis-points-lists/normal/AL/download?effectiveDate={date}");
        request.Headers.Accept.ParseAdd("application/octet-stream");
        if (!request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", apiKey))
        {
            throw new IOException("The saved FIS API key cannot be sent as a request header.");
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new IOException($"FIS download failed (HTTP {(int)response.StatusCode}). Check the API key and date.");
        }
        if (response.Content.Headers.ContentLength is > 40_000_000)
        {
            throw new IOException("FIS download is unexpectedly large.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > 40_000_000)
            {
                throw new IOException("FIS download is unexpectedly large.");
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }
}

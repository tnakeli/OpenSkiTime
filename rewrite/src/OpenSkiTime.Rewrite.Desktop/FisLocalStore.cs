using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenSkiTime.Rewrite.Desktop;

// Credentials belong to the current Windows user. The original ZIP is kept outside event files and the repository.
public sealed class FisLocalStore
{
    private readonly string _credentialTarget = "OpenSkiTime.FIS.ApiKey";
    private readonly string _archivePath;

    public FisLocalStore(string? localDataDirectory = null)
    {
        var directory = localDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime");
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

    public bool HasApiKey()
    {
        RequireWindows();
        if (!CredRead(_credentialTarget, 1, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == 1168) { return false; }
            throw new IOException("Could not read the FIS credential from Windows Credential Manager.");
        }
        CredFree(pointer);
        return true;
    }

    public string? ReadApiKey()
    {
        RequireWindows();
        if (!CredRead(_credentialTarget, 1, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == 1168) { return null; }
            throw new IOException("Could not read the FIS credential from Windows Credential Manager.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void SaveApiKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireWindows();
        var clean = key.Trim();
        if (clean.Length == 0 || clean.Length > 1024 || clean.Any(char.IsControl))
        {
            throw new ArgumentException("Enter a FIS API key without control characters (up to 1024 characters).", nameof(key));
        }
        var blob = Marshal.StringToHGlobalUni(clean);
        try
        {
            var credential = new NativeCredential
            {
                Type = 1, TargetName = _credentialTarget, CredentialBlobSize = clean.Length * 2,
                CredentialBlob = blob, Persist = 2, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new IOException($"Could not save the FIS credential in Windows Credential Manager (Windows error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(blob); }
    }

    public void RemoveApiKey()
    {
        RequireWindows();
        if (!CredDelete(_credentialTarget, 1, 0) && Marshal.GetLastPInvokeError() != 1168)
        {
            throw new IOException("Could not remove the FIS credential from Windows Credential Manager.");
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("FIS credential storage currently requires Windows.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(nint credential);
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

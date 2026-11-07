using System.Runtime.InteropServices;
using System.Text;

namespace OpenSkiTime.LiveTiming.Client;

public interface ICredentialStore
{
    bool Exists();
    string? Read();
    void Save(string key);
    void Remove();
}

/// <summary>
/// Windows Credential Manager entry of the current user. A credential is stored as UTF-16 (up to 1024 characters) by default.
/// With <paramref name="compactBlob"/> it is stored as UTF-8 so that a long ASCII credential, such as a JWT access token,
/// fits the 2560-byte limit of a credential blob (up to 2560 characters).
/// </summary>
public sealed class WindowsCredentialStore(string target, bool compactBlob = false) : ICredentialStore
{
    public const int MaximumCompactLength = 2560;

    public bool Exists()
    {
        RequireWindows();
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == 1168) { return false; }
            throw new IOException("Could not read the credential from Windows Credential Manager.");
        }
        CredFree(pointer);
        return true;
    }

    public string? Read()
    {
        RequireWindows();
        if (!CredRead(target, 1, 0, out var pointer))
        {
            if (Marshal.GetLastPInvokeError() == 1168) { return null; }
            throw new IOException("Could not read the credential from Windows Credential Manager.");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            // An entry written as UTF-16 before the compact format (it contains NUL bytes) still reads correctly.
            try { return compactBlob && !bytes.Contains((byte)0) ? Encoding.UTF8.GetString(bytes) : Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireWindows();
        var clean = key;
        var limit = compactBlob ? MaximumCompactLength : 1024;
        if (clean.Length == 0 || clean.Length > limit || clean.Any(char.IsControl) || compactBlob && !clean.All(char.IsAscii))
        {
            throw new ArgumentException(compactBlob
                ? $"Enter a credential of ASCII characters without control characters (up to {limit} characters)."
                : $"Enter a credential without control characters (up to {limit} characters).", nameof(key));
        }
        var bytes = compactBlob ? Encoding.UTF8.GetBytes(clean) : Encoding.Unicode.GetBytes(clean);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = 1, TargetName = target, CredentialBlobSize = bytes.Length,
                CredentialBlob = blob, Persist = 2, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new IOException($"Could not save the credential in Windows Credential Manager (Windows error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Remove()
    {
        RequireWindows();
        if (!CredDelete(target, 1, 0) && Marshal.GetLastPInvokeError() != 1168)
        {
            throw new IOException("Could not remove the credential from Windows Credential Manager.");
        }
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("credential storage currently requires Windows.");
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

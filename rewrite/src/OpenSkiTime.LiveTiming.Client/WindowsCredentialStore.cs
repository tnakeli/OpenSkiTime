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

public sealed class WindowsCredentialStore(string target) : ICredentialStore
{
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
            try { return Encoding.Unicode.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void Save(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RequireWindows();
        var clean = key;
        if (clean.Length == 0 || clean.Length > 1024 || clean.Any(char.IsControl))
        {
            throw new ArgumentException("Enter a credential without control characters (up to 1024 characters).", nameof(key));
        }
        var blob = Marshal.StringToHGlobalUni(clean);
        try
        {
            var credential = new NativeCredential
            {
                Type = 1, TargetName = target, CredentialBlobSize = clean.Length * 2,
                CredentialBlob = blob, Persist = 2, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new IOException($"Could not save the credential in Windows Credential Manager (Windows error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(blob); }
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

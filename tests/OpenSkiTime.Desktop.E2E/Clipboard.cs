using System.Runtime.InteropServices;

namespace OpenSkiTime.Desktop.E2E;

// Places Unicode text on the Windows clipboard, as when the operator copies a table from Excel.
internal static class Clipboard
{
    public static void SetText(string text)
    {
        for (var attempt = 0; attempt < 20 && !OpenClipboard(IntPtr.Zero); attempt++) { Thread.Sleep(50); }
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var memory = GlobalAlloc(0x0002, (UIntPtr)bytes); // GMEM_MOVEABLE
            var target = GlobalLock(memory);
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            GlobalUnlock(memory);
            if (SetClipboardData(13, memory) == IntPtr.Zero) { throw new InvalidOperationException("Clipboard write failed."); } // CF_UNICODETEXT
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
}

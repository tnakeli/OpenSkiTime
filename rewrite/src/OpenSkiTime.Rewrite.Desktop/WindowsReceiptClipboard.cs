using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenSkiTime.Rewrite.Desktop;

// Windows bitmap clipboard formats are isolated here; no image is written to disk.
[SupportedOSPlatform("windows")]
internal static class WindowsReceiptClipboard
{
    public static (byte[] Bytes, string FileName)? ReadImage()
    {
        if (!OpenClipboard(IntPtr.Zero)) { throw new IOException("The clipboard is busy. Try pasting again."); }
        try
        {
            var png = ReadFormat(RegisterClipboardFormat("PNG"));
            if (png is not null) { return (png, "Clipboard.png"); }
            var dib = ReadFormat(17) ?? ReadFormat(8); // CF_DIBV5, CF_DIB
            return dib is null ? null : (ReceiptBitmapClipboard.EncodeBmp(dib), "Clipboard.bmp");
        }
        finally { CloseClipboard(); }
    }

    private static byte[]? ReadFormat(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) { return null; }
        var length = GlobalSize(handle).ToUInt64();
        if (length is 0 or > 20_000_000) { throw new IOException("Clipboard images must be at most 20 MB."); }
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) { throw new IOException("Could not access the clipboard image."); }
        try { var bytes = new byte[(int)length]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}

internal static class ReceiptBitmapClipboard
{
    internal static byte[] EncodeBmp(byte[] dib)
    {
        ArgumentNullException.ThrowIfNull(dib);
        if (dib.Length < 40) { throw new IOException("The clipboard bitmap header is incomplete."); }
        var header = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (header is not (40 or 108 or 124) || header > dib.Length)
        { throw new IOException("Unsupported clipboard bitmap header."); }
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        if (bits is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3 or 6))
        { throw new IOException("Unsupported clipboard bitmap encoding."); }
        var colors = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if (colors == 0 && bits <= 8) { colors = 1u << bits; }
        var masks = header == 40 ? compression == 3 ? 12u : compression == 6 ? 16u : 0u : 0u;
        var offset = 14ul + header + masks + colors * 4ul;
        if (offset >= (ulong)dib.Length + 14) { throw new IOException("The clipboard bitmap pixels are missing."); }
        var bmp = new byte[checked(dib.Length + 14)];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), (uint)offset);
        dib.CopyTo(bmp, 14);
        return bmp;
    }
}

using System.Buffers.Binary;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public class ReceiptClipboardTests
{
    [AvaloniaFact]
    public void WindowsDibClipboardImageDecodesInMemoryWithoutChangingItsPixels()
    {
        var dib = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 24);
        dib[40] = 255;
        var original = dib.ToArray();
        var bmp = ReceiptBitmapClipboard.EncodeBmp(dib);
        Assert.Equal(original, dib);
        Assert.Equal(original, bmp[14..]);
        Assert.Equal(54u, BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10)));
        using var stream = new MemoryStream(bmp);
        using var bitmap = new Bitmap(stream);
        Assert.Equal(1, bitmap.PixelSize.Width); Assert.Equal(1, bitmap.PixelSize.Height);
        Assert.Throws<IOException>(() => ReceiptBitmapClipboard.EncodeBmp([0, 1, 2]));
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), uint.MaxValue);
        Assert.Throws<IOException>(() => ReceiptBitmapClipboard.EncodeBmp(dib));
    }
}

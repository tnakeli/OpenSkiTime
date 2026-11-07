using ClientStore = OpenSkiTime.LiveTiming.Client.WindowsCredentialStore;
using Xunit;

namespace OpenSkiTime.Desktop.Tests;

// Uses the real Windows Credential Manager with a unique, removed target. Windows only.
public sealed class CompactCredentialStoreTests
{
    private static string Target() => "OpenSkiTime.Test.Credential." + Guid.NewGuid().ToString("N");

    // A JWT-like ASCII token: base64url characters and two dots, longer than the 1024 characters of a UTF-16 entry.
    private static string LongToken(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var chars = Enumerable.Range(0, length).Select(i => alphabet[i * 7 % alphabet.Length]).ToArray();
        chars[40] = '.'; chars[length - 345] = '.';
        return new string(chars);
    }

    [Fact]
    public void CompactStoreKeepsALongAsciiTokenIntact()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var target = Target();
        var store = new ClientStore(target, compactBlob: true);
        try
        {
            var token = LongToken(2000);
            store.Save(token);
            Assert.True(store.Exists());
            Assert.Equal(token, store.Read());
            var longest = LongToken(ClientStore.MaximumCompactLength);
            store.Save(longest);
            Assert.Equal(longest, store.Read());
        }
        finally { store.Remove(); }
        Assert.False(store.Exists());
    }

    [Fact]
    public void StoresRejectCredentialsBeyondTheirLimitsWithoutChangingTheSavedOne()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var target = Target();
        var utf16 = new ClientStore(target);
        var compact = new ClientStore(target, compactBlob: true);
        try
        {
            utf16.Save("synthetic-short");
            Assert.Throws<ArgumentException>(() => utf16.Save(LongToken(1025)));
            Assert.Equal("synthetic-short", utf16.Read());
            compact.Save("synthetic-compact");
            Assert.Throws<ArgumentException>(() => compact.Save(LongToken(ClientStore.MaximumCompactLength + 1)));
            Assert.Throws<ArgumentException>(() => compact.Save("tökén"));
            Assert.Equal("synthetic-compact", compact.Read());
        }
        finally { utf16.Remove(); }
    }

    [Fact]
    public void CompactStoreStillReadsAnEntryWrittenAsUtf16()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var target = Target();
        var legacy = new ClientStore(target);
        try
        {
            legacy.Save("synthetic-legacy-token");
            Assert.Equal("synthetic-legacy-token", new ClientStore(target, compactBlob: true).Read());
        }
        finally { legacy.Remove(); }
    }
}

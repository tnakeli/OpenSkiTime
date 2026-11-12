using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using OpenSkiTime.LiveTiming.Harness;
using OpenSkiTime.LiveTiming.Radio.Providers;
using OpenSkiTime.LiveTiming.Server;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// Server-wide limits of the session store must not let one publisher or session crowd out the others.
public sealed class SessionStoreLimitTests
{
    private static SessionStore Store(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["LiveTiming:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        foreach (var (key, value) in settings) { values[key] = value; }
        return new(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), TimeProvider.System);
    }
    private static LiveGrant Grant(LiveSession session, string publisher) => new(session.ExpiresAt, publisher);
    private static LiveSession Published(SessionStore store, string publisher)
    {
        var session = store.Create("http://localhost", publisher);
        store.Replace(session.SessionId, Grant(session, publisher), SyntheticRace.Create(3));
        return session;
    }

    [Fact]
    public void EachPublisherKeyHasItsOwnShareAndDeletingFreesTheSlot()
    {
        var store = Store(("LiveTiming:MaxSessions", "3"), ("LiveTiming:MaxSessionsPerPublisher", "2"));
        var first = store.Create("http://localhost", "a"); store.Create("http://localhost", "a");
        Assert.Throws<LiveCapacityException>(() => store.Create("http://localhost", "a"));
        store.Create("http://localhost", "b"); // Room is left for another organizer.
        store.Delete(first.SessionId, Grant(first, "a"));
        store.Create("http://localhost", "a"); // The deleted session's slot is free again; its marker takes none.
        Assert.Throws<LiveCapacityException>(() => store.Create("http://localhost", "b")); // The global capacity still holds.
        // Restoring a session after a restart counts against the same per-publisher limit.
        // A deletion marker stays until its token expires, so the token never works again; a key holding its maximum of
        // markers (four times its session limit) cannot create more sessions, and deleting never fails.
        var churn = Store(("LiveTiming:MaxSessions", "3"), ("LiveTiming:MaxSessionsPerPublisher", "1"));
        var deleted = new List<LiveSession>();
        for (var i = 0; i < 4; i++) { var s = churn.Create("http://localhost", "a"); churn.Delete(s.SessionId, Grant(s, "a")); deleted.Add(s); }
        Assert.Throws<LiveCapacityException>(() => churn.Create("http://localhost", "a"));
        Assert.All(deleted, s => Assert.Null(churn.Authorize(s.SessionId, s.PublisherToken)));
        churn.Create("http://localhost", "b");
        // Publisher names compare case-insensitively, like configured key names.
        var restarted = Store(("LiveTiming:MaxSessions", "3"), ("LiveTiming:MaxSessionsPerPublisher", "1"));
        restarted.Replace(Guid.NewGuid(), new(DateTimeOffset.UtcNow.AddDays(1), "a"), SyntheticRace.Create(3));
        Assert.Throws<LiveCapacityException>(() => restarted.Replace(Guid.NewGuid(), new(DateTimeOffset.UtcNow.AddDays(1), "A"), SyntheticRace.Create(3)));
        Assert.Throws<LiveCapacityException>(() => restarted.Create("http://localhost", "A"));
    }

    [Fact]
    public void ADeclaredButMissingPlaylistReservesNoServerWideMusic()
    {
        var store = Store(("LiveTiming:RadioMusicMaxMegabytes", "1"));
        var attacker = Published(store, "a"); var victim = Published(store, "b");
        var big = Mp3(900_000, 1); var small = Mp3(500_000, 2);
        store.SetMusic(attacker.SessionId, new([new(RadioMusic.Id(big), big.Length)]));
        // Before the fix the declared 900 kB blocked this playlist with 503 although nothing had been uploaded.
        store.SetMusic(victim.SessionId, new([new(RadioMusic.Id(small), small.Length)]));
        store.AddMusic(victim.SessionId, RadioMusic.Id(small), small);
        // The server-wide limit counts received tracks.
        Assert.Throws<LiveCapacityException>(() => store.AddMusic(attacker.SessionId, RadioMusic.Id(big), big));
    }

    [Fact]
    public void ServerWideClipLimitEvictsFromTheLargestSessionNotAnotherSessionsClips()
    {
        var store = Store(("LiveTiming:RadioMaxMegabytes", "1"));
        var victim = Published(store, "v"); var attackers = new[] { Published(store, "a"), Published(store, "b") };
        foreach (var session in attackers.Append(victim)) { store.RadioStatus(session.SessionId, new(true, false)); }
        var kept = Clip(1000);
        store.AddClip(victim.SessionId, kept); // Received first: the oldest clip on the server.
        for (var i = 0; i < 14; i++) { store.AddClip(attackers[i % 2].SessionId, Clip(10_000)); } // About 1.1 MB in total.
        Assert.NotNull(store.ClipAudio(victim.SessionId, kept.ClipId));
    }

    private static RadioClipUpload Clip(int milliseconds) => new(Guid.NewGuid(), Guid.NewGuid(), 0, 1, "Aino", 3, true,
        120, [], [], "audio/wav", milliseconds, Convert.ToBase64String(SimulationTts.Tone(milliseconds, 440)));
    private static byte[] Mp3(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        "ID3"u8.CopyTo(bytes);
        return bytes;
    }
}

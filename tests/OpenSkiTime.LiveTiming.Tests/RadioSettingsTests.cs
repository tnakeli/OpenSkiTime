using OpenSkiTime.LiveTiming.Client;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

public sealed class RadioSettingsTests
{
    [Fact]
    public void RadioIsOffByDefaultAndTargetsOnlyEnabledRunningChannelsWithASession()
    {
        Assert.False(new RadioConfiguration().LocalEnabled || new RadioConfiguration().CloudEnabled);
        var now = RadioRace.Start;
        var session = new LiveSession(Guid.NewGuid(), "token", now.AddDays(1), "http://x/r/1");
        var running = new PublisherHealth(PublisherState.Running);
        (PublisherKind, PublisherHealth, string?, LiveSession?)[] channels =
        [
            (PublisherKind.Local, running, "http://127.0.0.1:5078", session), (PublisherKind.Cloud, running, "https://live.example.invalid", session),
            (PublisherKind.FisHttps, running, "https://fis.example.invalid", session)
        ];
        Assert.Empty(RadioTargets.Select(new RadioConfiguration(), channels, now));
        Assert.Equal([PublisherKind.Local], RadioTargets.Select(new RadioConfiguration { LocalEnabled = true }, channels, now).Select(x => x.Channel));
        Assert.Equal([PublisherKind.Cloud], RadioTargets.Select(new RadioConfiguration { CloudEnabled = true }, channels, now).Select(x => x.Channel));
        var both = new RadioConfiguration { LocalEnabled = true, CloudEnabled = true };
        Assert.Equal(2, RadioTargets.Select(both, channels, now).Length);
        // A stopped, reconnecting or session-less channel gets no clips; its viewers see no radio control.
        Assert.Empty(RadioTargets.Select(both, [(PublisherKind.Local, new PublisherHealth(PublisherState.Stopped), "http://127.0.0.1:5078", session)], now));
        Assert.Empty(RadioTargets.Select(both, [(PublisherKind.Local, new PublisherHealth(PublisherState.Reconnecting), "http://127.0.0.1:5078", session)], now));
        Assert.Empty(RadioTargets.Select(both, [(PublisherKind.Local, running, "http://127.0.0.1:5078", null)], now));
        Assert.Empty(RadioTargets.Select(both, [(PublisherKind.Local, running, "http://127.0.0.1:5078", session)], now.AddDays(2)));
    }

    [Fact]
    public void SettingsAreStoredPerCompetitionSpendNeverDecreasesAndAnUnreadableFileIsKept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-radio-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RadioSettingsStore(Path.Combine(folder, RadioSettingsStore.FileName));
            var first = Guid.NewGuid(); var second = Guid.NewGuid();
            Assert.Equal(new RadioConfiguration(), store.Read(first).Configuration);
            store.Save(first, new RadioConfiguration { CloudEnabled = true, BudgetUsd = 12.5m, EventContext = "Junioreiden kisa" });
            store.SaveSpend(first, 1.25m); store.SaveSpend(first, 0.5m);
            Assert.Equal(1.25m, store.Read(first).SpentUsd);
            Assert.True(store.Read(first).Configuration.CloudEnabled);
            Assert.False(store.Read(second).Configuration.CloudEnabled);
            Assert.Throws<LiveValidationException>(() => store.Save(second, new RadioConfiguration { BudgetUsd = -1m }));
            Assert.Throws<LiveValidationException>(() => store.Save(second, new RadioConfiguration { VoiceA = "fi-FI\"><x" }));
            File.WriteAllText(store.Path, "{ not json");
            Assert.Equal(new RadioConfiguration(), store.Read(first).Configuration);
            Assert.Throws<IOException>(() => store.SaveSpend(first, 2m));
            Assert.Equal("{ not json", File.ReadAllText(store.Path));
        }
        finally { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }
    }

    [Fact]
    public void ProviderKeysHaveTheirOwnCredentialTargets()
    {
        Assert.Equal("OpenSkiTime.LiveTiming.Radio:azure", RadioKeyCredential.Target(RadioProviders.Azure));
        Assert.Throws<ArgumentException>(() => RadioKeyCredential.Target(RadioProviders.Simulation));
        Assert.Throws<ArgumentException>(() => RadioKeyCredential.Validate("short"));
        Assert.Throws<ArgumentException>(() => RadioKeyCredential.Validate("contains a space 0123456789"));
        RadioKeyCredential.Validate("sk-synthetic-test-key-0123456789");
    }
}

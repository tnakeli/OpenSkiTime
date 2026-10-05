using System.Text.Json;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class ManualTimestampTests
{
    private static readonly long Noon = TimeSpan.FromHours(12).Ticks;

    [Fact]
    public async Task ManualStartAndFinishWithoutDeviceAreAuditedAssignableAndSurviveReopen()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1, 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var bib = list.Plan.Entries[0].Bib;

        var start = await timing.AddManualTimestampAsync(list.Id, 0, "12:00:00.00", "Operator", "Start impulse missing");
        // One decimal is below hand-clock precision.
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.AddManualTimestampAsync(list.Id, 2, "12:00:20.5", "Operator", "Split impulse missing"));
        var split = await timing.AddManualTimestampAsync(list.Id, 2, "12:00:20.50", "Operator", "Split impulse missing");
        var finish = await timing.AddManualTimestampAsync(list.Id, 1, " 12:00:41.2345 ", "Operator", "Finish impulse missing");

        var observation = timing.Snapshot!.Observations.Single(x => x.Observation.Key == finish);
        Assert.Equal("Unassigned", observation.State);
        Assert.True(observation.Observation.ManualEntry);
        Assert.True(observation.Observation.Manual);
        Assert.Equal(4, observation.Observation.Precision);
        Assert.Equal($"manual:{list.Id:N}", observation.Observation.ClockId);
        Assert.Equal(TimingRulesTests.Date.ToDateTime(TimeOnly.MinValue).Ticks + Noon + 412345000, observation.Observation.DeviceTicks);

        foreach (var key in new[] { start, split, finish })
        {
            var keys = timing.Snapshot!.Observations.Where(x => x.Bib == bib).Select(x => x.Observation.Key).ToArray();
            await timing.MoveTimestampAsync(list.Id, bib, key,
                TimingEngine.CurrentDecision(new(DecisionKind.Assignment, key), timing.Snapshot.Audit), keys, "Operator");
        }
        AssertResult(timing.Snapshot!);

        var saved = await workspace.ReadTimingAsync(list.Id);
        Assert.Empty(saved.Packets);
        var entry = saved.Audit.First(x => x.After.Kind == DecisionKind.ManualTime && x.After.ObservationKey == finish);
        Assert.Equal("Operator", entry.Operator);
        Assert.Equal("Finish impulse missing", entry.Reason);
        Assert.Null(entry.Before.Timestamp);
        Assert.Equal(1, entry.After.Timestamp!.Channel);
        Assert.Null(entry.After.Timestamp.ReferenceKey);

        await workspace.CloseAsync(); await workspace.OpenAsync(folder.File);
        await workspace.Timing!.SelectRunAsync(list.Id);
        AssertResult(workspace.Timing.Snapshot!);
        AssertResult(TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), new AlgeDecoderFactory()));

        static void AssertResult(TimingSnapshot snapshot)
        {
            var result = snapshot.Results.Single();
            Assert.Equal(TimingStatus.Finished, result.Status);
            Assert.Equal(4123, result.Hundredths);
            Assert.True(result.StartManual);
            Assert.True(result.FinishManual);
            Assert.Equal("Includes a manually entered time; verify against backup timing.", result.Detail);
            var split = Assert.Single(result.Splits);
            Assert.Equal(2050, split.Hundredths);
            Assert.True(split.Manual);
        }
    }

    [Fact]
    public async Task ManualFinishDuringCaptureUsesTheDeviceClockAndKeepsRawInputUnchanged()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id); await timing.FollowStartOrderAsync(true);
        var source = new SimulatorTimingSource();
        await timing.StartAsync(source, new("Simulator", "Test", TimingRulesTests.Date, Simulation: true), "Operator");
        await Pulse(timing, source, 0, 0);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results.Any(x => x.Status == TimingStatus.OnCourse));
        var first = timing.Snapshot!.Results.Single(x => x.Status == TimingStatus.OnCourse);
        var packets = (await workspace.ReadTimingAsync(list.Id)).Packets;

        var key = await timing.AddManualTimestampAsync(list.Id, 1, "12:00:45.67", "Operator", "Finish photocell missed");
        var manual = timing.Snapshot!.Observations.Single(x => x.Observation.Key == key);
        Assert.Equal("Unassigned", manual.State); // never assigned automatically, even with an armed finish
        Assert.Equal(first.StartKey, timing.Snapshot.Observations[^1].Observation.Key == key
            ? timing.Snapshot.Observations[^2].Observation.Key : null);
        var start = timing.Snapshot.Observations.Single(x => x.Observation.Key == first.StartKey).Observation;
        Assert.Equal(start.ClockId, manual.Observation.ClockId);

        await timing.MoveTimestampAsync(list.Id, first.Bib, key,
            TimingEngine.CurrentDecision(new(DecisionKind.Assignment, key), timing.Snapshot.Audit), [first.StartKey!], "Operator");
        var result = timing.Snapshot!.Results.Single(x => x.Bib == first.Bib);
        Assert.Equal((TimingStatus.Finished, 4567L), (result.Status, result.Hundredths!.Value));
        Assert.False(result.StartManual);
        Assert.True(result.FinishManual);

        // Later device input is ordered after the manual entry, live and after reopening.
        await Pulse(timing, source, 0, 50);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Results.All(x => x.Status != TimingStatus.Ready));
        var liveOrder = timing.Snapshot!.Observations.Select(x => x.Observation.Key).ToArray();
        var saved = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(packets.Select(x => Convert.ToHexString(x.Bytes)), saved.Packets.Take(packets.Count).Select(x => Convert.ToHexString(x.Bytes)));
        Assert.Equal(start.Key, Assert.Single(saved.Audit, x => x.After.Kind == DecisionKind.ManualTime).After.Timestamp!.ReferenceKey);
        await timing.StopAsync(); await workspace.CloseAsync(); await workspace.OpenAsync(folder.File);
        await workspace.Timing!.SelectRunAsync(list.Id);
        Assert.Equal(liveOrder, workspace.Timing.Snapshot!.Observations.Where(x => !x.Observation.Key.EndsWith(":interrupted", StringComparison.Ordinal))
            .Select(x => x.Observation.Key));
        Assert.Equal(4567, workspace.Timing.Snapshot.Results.Single(x => x.Bib == first.Bib).Hundredths);
    }

    [Theory]
    [InlineData(1, "12:00:00", "Missing")]
    [InlineData(1, "12:00:00.1", "Missing")]
    [InlineData(1, "24:00:00.00", "Missing")]
    [InlineData(1, "12:00:00.00", " ")]
    [InlineData(2, "12:00:00.00", "Missing")]
    [InlineData(-1, "12:00:00.00", "Missing")]
    public async Task InvalidManualTimesAreRefusedBeforeAnythingIsSaved(int channel, string time, string reason)
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.AddManualTimestampAsync(list.Id, channel, time, "Operator", reason));
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.AddManualTimestampAsync(Guid.NewGuid(), 1, "12:00:00.00", "Operator", "Missing"));
        Assert.Empty((await workspace.ReadTimingAsync(list.Id)).Audit);
        Assert.Empty(timing.Snapshot!.Observations);
    }

    [Fact]
    public async Task UndoRemovesAnUnassignedManualTimeButNeverAnAssignedOne()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        var bib = list.Plan.Entries[0].Bib;
        var start = await timing.AddManualTimestampAsync(list.Id, 0, "12:00:00.00", "Operator", "Missing start");
        await timing.MoveTimestampAsync(list.Id, bib, start,
            TimingEngine.CurrentDecision(new(DecisionKind.Assignment, start), timing.Snapshot!.Audit), [], "Operator");
        var created = timing.Snapshot!.Audit.Single(x => x.After.Kind == DecisionKind.ManualTime).Id;
        await Assert.ThrowsAsync<DomainValidationException>(() => timing.UndoAsync(created, "Operator", "Wrong time"));

        var spare = await timing.AddManualTimestampAsync(list.Id, 1, "12:01:00.00", "Operator", "Entered by mistake");
        var spareId = timing.Snapshot!.Audit[^1].Id;
        await timing.UndoAsync(spareId, "Operator", "Entered by mistake");
        Assert.DoesNotContain(timing.Snapshot!.Observations, x => x.Observation.Key == spare);
        var saved = await workspace.ReadTimingAsync(list.Id);
        Assert.Equal(spareId, saved.Audit[^1].ReversesId);
        await workspace.CloseAsync(); await workspace.OpenAsync(folder.File);
        await workspace.Timing!.SelectRunAsync(list.Id);
        Assert.DoesNotContain(workspace.Timing.Snapshot!.Observations, x => x.Observation.Key == spare);
        Assert.Equal(TimingStatus.OnCourse, workspace.Timing.Snapshot.Results.Single().Status);
    }

    [Fact]
    public async Task StoreRejectsUnknownManualReferencesAndAssignments()
    {
        using var folder = new TestFolder();
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, folder.File, 1);
        await workspace.CloseAsync();
        await using var session = await new SqliteSeriesFileStore().OpenAsync(folder.File);
        var store = (ITimingStore)session;
        var key = ManualTimestamp.KeyPrefix + Guid.NewGuid().ToString("N");
        var bib = list.Plan.Entries[0].Bib;
        await Assert.ThrowsAsync<DomainValidationException>(() => store.AppendTimingAuditAsync(list.Id, 0,
            new(DecisionKind.Assignment, key), new(DecisionKind.Assignment, key, Bib: bib), "Operator", "Assign", TimingRulesTests.At));
        await Assert.ThrowsAsync<DomainValidationException>(() => store.AppendTimingAuditAsync(list.Id, 0,
            new(DecisionKind.ManualTime, key), new(DecisionKind.ManualTime, key, Timestamp: new(1, Noon, 2, "x", $"{Guid.NewGuid():N}:1")),
            "Operator", "Missing", TimingRulesTests.At));
        await Assert.ThrowsAsync<DomainValidationException>(() => store.AppendTimingAuditAsync(list.Id, 0,
            new(DecisionKind.ManualTime, key), new(DecisionKind.ManualTime, key, Timestamp: new(1, Noon, 1, "x")),
            "Operator", "Missing", TimingRulesTests.At));
        await Assert.ThrowsAsync<DomainValidationException>(() => store.AppendTimingAuditAsync(list.Id, 0,
            new(DecisionKind.ManualTime, "manual:1"), new(DecisionKind.ManualTime, "manual:1", Timestamp: new(1, Noon, 2, "x")),
            "Operator", "Missing", TimingRulesTests.At));
        Assert.Empty((await store.ReadTimingAsync(list.Id)).Audit);

        // Entry and assignment in one batch are one atomic operation.
        await store.AppendTimingAuditBatchAsync(list.Id, 0, [
            new(new(DecisionKind.ManualTime, key), new(DecisionKind.ManualTime, key, Timestamp: new(0, Noon, 2, "x"))),
            new(new(DecisionKind.Assignment, key), new(DecisionKind.Assignment, key, Bib: bib))], "Operator", "Missing start", TimingRulesTests.At);
        Assert.Equal(2, (await store.ReadTimingAsync(list.Id)).Audit.Count);
    }

    [Fact]
    public void OtherDecisionsSerializeExactlyAsBeforeSoApprovedFingerprintsAreUnchanged()
    {
        var json = JsonSerializer.Serialize(new TimingDecision(DecisionKind.Status, CompetitorId: Guid.Empty, Status: TimingStatus.DNF));
        Assert.DoesNotContain("Timestamp", json, StringComparison.Ordinal);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<TimingDecision>(json)));
        Assert.Equal(4, (int)DecisionKind.ManualTime);
    }

    private static async Task Pulse(TimingWorkspace timing, SimulatorTimingSource source, int channel, int seconds)
    {
        var count = timing.Snapshot!.Observations.Count;
        await source.PulseAsync(channel, Noon + seconds * TimeSpan.TicksPerSecond);
        await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Observations.Count > count);
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-manual-time-tests", Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(_root, "synthetic.ost");
        public TestFolder() => Directory.CreateDirectory(_root);
        public void Dispose()
        {
            if (!Path.GetFullPath(_root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-manual-time-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Unexpected test folder."); }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }
}

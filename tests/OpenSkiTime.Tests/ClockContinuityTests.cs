using System.Text;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

// Timing stops when the view is left, so a competitor may start in one session and finish in the next.
public sealed class ClockContinuityTests : IDisposable
{
    private static readonly DateTimeOffset s_at = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "openskitime-clock", Guid.NewGuid().ToString("N"));
    public ClockContinuityTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    // One line of the device and the seconds after the first line at which the computer received it.
    private sealed record Line(double Second, string Text);
    private sealed record Part(Line[] Lines, bool Continues = true, string Endpoint = "Timy USB ", string? Group = null);

    private async Task<(TimingSnapshot Live, TimingSnapshot Replay)> RunAsync(params Part[] parts)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ost");
        TimingSnapshot live;
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(new AlgeDecoderFactory()), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, path, 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        foreach (var part in parts)
        {
            var source = new TimedSource(part.Lines.Select(x => (s_at.AddSeconds(x.Second), x.Text)).ToArray());
            await timing.StartAsync(source, new("Timy 2/3 · USB", part.Endpoint, TimingRulesTests.Date) { ContinuesClock = part.Continues, ClockGroup = part.Group }, "Operator");
            await source.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await TimingStorageTests.UntilAsync(() => timing.Pending == 0);
            await timing.StopAsync();
        }
        live = timing.Snapshot!;
        var replay = TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), new AlgeDecoderFactory());
        Assert.Equal(live.Observations.Select(x => (x.Observation.Key, x.Observation.ClockId, x.State, x.Bib)), replay.Observations.Select(x => (x.Observation.Key, x.Observation.ClockId, x.State, x.Bib)));
        Assert.Equal(live.Results.Select(x => (x.Bib, x.Status, x.Hundredths)), replay.Results.Select(x => (x.Bib, x.Status, x.Hundredths)));
        // Selecting the run again from storage gives the same results.
        await timing.SelectRunAsync(list.Id);
        Assert.Equal(live.Results.Select(x => (x.Bib, x.Status, x.Hundredths)), timing.Snapshot!.Results.Select(x => (x.Bib, x.Status, x.Hundredths)));
        return (live, replay);
    }

    private static readonly Part s_start = new([new(0, "12:00:00.0  \r"), new(1.02, " 0001* C0 12:00:01.0000\r")]);

    [Fact]
    public async Task AFinishAfterReturningToTimingIsTimedWhenTheDeviceClockRanOn()
    {
        var (live, _) = await RunAsync(s_start, new([new(30.05, "12:00:30.0  \r"), new(46.5, " 0001* C1 12:00:46.4567\r")]));
        var result = live.Results.Single(x => x.Bib == 1);
        Assert.Equal((TimingStatus.Finished, 4545L), (result.Status, result.Hundredths));
        Assert.Single(live.Observations.Select(x => x.Observation.ClockId).Distinct());
    }

    [Theory]
    [InlineData("12:00:31.0  \r", " 0001* C1 12:00:47.4567\r")] // set one second ahead while away
    [InlineData("12:00:29.0  \r", " 0001* C1 12:00:45.4567\r")] // set one second back
    public async Task AChangedDeviceClockNeedsReview(string heartbeat, string finish)
    {
        var (live, _) = await RunAsync(s_start, new([new(30.05, heartbeat), new(46.5, finish)]));
        Assert.Equal(TimingStatus.Review, live.Results.Single(x => x.Bib == 1).Status);
    }

    [Fact]
    public async Task SessionsOfEarlierFilesKeepTheirOwnClocks()
    {
        var (live, _) = await RunAsync(s_start with { Continues = false }, new([new(30.05, "12:00:30.0  \r"), new(46.5, " 0001* C1 12:00:46.4567\r")], Continues: false));
        Assert.Equal(TimingStatus.Review, live.Results.Single(x => x.Bib == 1).Status);
        Assert.Equal(2, live.Observations.Select(x => x.Observation.ClockId).Distinct().Count());
    }

    [Fact]
    public async Task AnotherDeviceNeverContinuesTheClock()
    {
        var (live, _) = await RunAsync(s_start, new([new(30.05, "12:00:30.0  \r"), new(46.5, " 0001* C1 12:00:46.4567\r")], Endpoint: "Timy USB 2"));
        Assert.Equal(TimingStatus.Review, live.Results.Single(x => x.Bib == 1).Status);
    }

    [Fact]
    public async Task ALateImpulseBeforeTheFirstHeartbeatDoesNotSplitTheRestOfTheSession()
    {
        // The device delivers an impulse it stored while timing was off, then its heartbeat and the real finish.
        var (live, _) = await RunAsync(s_start, new([new(30.0, " 0009 C1 12:00:20.0000\r"), new(30.1, "12:00:30.0  \r"), new(46.5, " 0001* C1 12:00:46.4567\r")]));
        Assert.Equal((TimingStatus.Finished, 4545L), (live.Results.Single(x => x.Bib == 1).Status, live.Results.Single(x => x.Bib == 1).Hundredths));
        Assert.Equal("Unassigned", live.Observations.Single(x => x.Bib is null).State);
    }

    [Theory]
    [InlineData(43, TimingStatus.Finished)] // the same device: its records run on
    [InlineData(3, TimingStatus.Review)] // another device, or a cleared memory, showing almost the same time
    [InlineData(5043, TimingStatus.Review)]
    public async Task AnotherDeviceWithNearlyTheSameTimeIsNotTakenForTheFirstOne(int record, TimingStatus expected)
    {
        var (live, _) = await RunAsync(new Part([new(0, "12:00:00.0  \r"), new(0.5, " 0042 C2 12:00:00.5000\r"), new(1.02, " 0001* C0 12:00:01.0000\r")]),
            new([new(30.05, "12:00:30.0  \r"), new(40.0, $" {record:0000} C2 12:00:40.0000\r"), new(46.5, " 0001* C1 12:00:46.4567\r")]));
        Assert.Equal(expected, live.Results.Single(x => x.Bib == 1).Status);
    }

    [Fact]
    public async Task TheClockGroupOfAMultiDeviceCaptureContinuesToo()
    {
        var (live, _) = await RunAsync(s_start with { Group = "g1" }, new([new(30.05, "12:00:30.0  \r"), new(46.5, " 0001* C1 12:00:46.4567\r")], Group: "g2"));
        Assert.Equal(TimingStatus.Finished, live.Results.Single(x => x.Bib == 1).Status);
    }

    // Start device and finish device of two visits. The order of the devices is the order in which their input arrives.
    private async Task<TimingSnapshot> RunDevicesAsync(params (string Device, string Protocol, Line[] Lines)[][] visits)
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(new AlgeDecoderFactory()), new AlgeDecoderFactory());
        var list = await TimingStorageTests.SeedAsync(workspace, Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ost"), 2);
        var timing = workspace.Timing!;
        await timing.SelectRunAsync(list.Id);
        for (var visit = 0; visit < visits.Length; visit++)
        {
            var previous = Task.CompletedTask;
            var inputs = new List<TimingSourceInput>();
            foreach (var (device, protocol, lines) in visits[visit])
            {
                var source = new TimedSource(lines.Select(x => (s_at.AddSeconds(x.Second), x.Text)).ToArray(), previous, protocol, device);
                previous = source.Delivered.Task;
                inputs.Add(new(source, new(device, device, TimingRulesTests.Date) { ContinuesClock = true, ClockGroup = "visit" + visit,
                    Routes = device == "start" ? [new(0, 0)] : [new(1, 1)] }));
            }
            await timing.StartAsync(inputs, "Operator");
            await previous.WaitAsync(TimeSpan.FromSeconds(20));
            await TimingStorageTests.UntilAsync(() => timing.Pending == 0);
            await timing.StopAsync();
        }
        var live = timing.Snapshot!;
        var replay = TimingReplay.Restore(await workspace.ReadTimingAsync(list.Id), new AlgeDecoderFactory());
        Assert.Equal(live.Results.Select(x => (x.Bib, x.Status, x.Hundredths)), replay.Results.Select(x => (x.Bib, x.Status, x.Hundredths)));
        return live;
    }

    private static readonly Line[] s_visitOne = [new(0, "12:00:00.0  \r"), new(0.6, " 0007 C0 12:00:00.5000\r"), new(1.02, " 0001* C0 12:00:01.0000\r")];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADeviceWithoutAnEarlierReadingFollowsTheVerifiedClockWhateverArrivesFirst(bool finishFirst)
    {
        // The finish device is silent in the first visit. The start device's next impulse verifies the clock.
        (string, string, Line[]) start = ("start", "alge-ascii/v1", [new(30.0, "12:00:30.0  \r"), new(50.0, " 0002* C0 12:00:50.0000\r")]);
        (string, string, Line[]) finish = ("finish", "alge-ascii/v1", [new(finishFirst ? 46.0 : 51.0, " 0001* C1 12:00:46.4567\r")]);
        var live = await RunDevicesAsync([("start", "alge-ascii/v1", s_visitOne), ("finish", "alge-ascii/v1", [])], finishFirst ? [finish, start] : [start, finish]);
        var result = live.Results.Single(x => x.Bib == 1);
        Assert.Equal((TimingStatus.Finished, 4545L), (result.Status, result.Hundredths));
    }

    [Fact]
    public async Task DevicesThatNobodyVerifiedKeepTheClockOfTheirOwnVisit()
    {
        // The start device was set one second ahead while Timing was closed.
        var live = await RunDevicesAsync([("start", "alge-ascii/v1", s_visitOne), ("finish", "alge-ascii/v1", [])],
            [("start", "alge-ascii/v1", [new(30.0, "12:00:31.0  \r"), new(50.0, " 0002* C0 12:00:51.0000\r")]), ("finish", "alge-ascii/v1", [new(52.0, " 0001* C1 12:00:47.4567\r"), new(95.0, " 0002* C1 12:01:35.0000\r")])]);
        Assert.Equal(TimingStatus.Review, live.Results.Single(x => x.Bib == 1).Status);
        Assert.Equal(TimingStatus.Finished, live.Results.Single(x => x.Bib == 2).Status);
    }

    private static string Trigger(int channel, string time, int bib, int offset) => $$"""{"deviceId":"1","timestamp":{{DateTime.Parse("2026-09-27T" + time, System.Globalization.CultureInfo.InvariantCulture).Ticks - DateTime.UnixEpoch.Ticks}},"timeOffset":{{offset}},"timingChannel":"C{{channel}}","type":"StartNumberTrigger","valid":true,"blocked":false,"startNumber":{"type":"MANUAL","startNumber":{{bib}}},"fallingEdge":true}""";

    [Fact]
    public async Task AlgeResultsDevicesAloneNeverContinueTheirClock()
    {
        var live = await RunDevicesAsync([("start", "alge-results/v1", [new(1, Trigger(0, "12:00:01", 1, 0))]), ("finish", "alge-results/v1", [])],
            [("start", "alge-results/v1", []), ("finish", "alge-results/v1", [new(46, Trigger(1, "12:00:46", 1, 0))])]);
        Assert.Equal(TimingStatus.Review, live.Results.Single(x => x.Bib == 1).Status);
    }

    [Theory]
    [InlineData("12:00:46", 0, TimingStatus.Finished)]
    [InlineData("11:00:46", 60, TimingStatus.Review)] // the offset of the device was changed on the server
    [InlineData("12:00:56", 0, TimingStatus.Review)] // the device clock was set ten seconds ahead
    public async Task AnAlgeResultsDeviceFollowsAVerifiedClockOnlyWhileItsOwnIsUnchanged(string finish, int offset, TimingStatus expected)
    {
        (string, string, Line[]) Start(double second, string bib, string time) => ("start", "alge-ascii/v1", [new(second, $" {bib}* C0 {time}\r")]);
        var live = await RunDevicesAsync([Start(1.02, "0001", "12:00:01.0000"), ("finish", "alge-results/v1", [new(2, Trigger(1, "12:00:02", 9, 0))])],
            [Start(40.02, "0002", "12:00:40.0000"), ("finish", "alge-results/v1", [new(46.3, Trigger(1, finish, 1, offset))])]);
        Assert.Equal(expected, live.Results.Single(x => x.Bib == 1).Status);
    }

    private sealed class TimedSource((DateTimeOffset At, string Text)[] lines, Task? after = null, string protocol = "alge-ascii/v1", string name = "Timy:1") : ITimingSource
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            if (after is not null) { await after; }
            foreach (var (at, text) in lines) { await receive(new(protocol, name, "1", Encoding.ASCII.GetBytes(text), at)); }
            Delivered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Text;
using System.Text.Json;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Persistence;
using OpenSkiTime.Rewrite.Timing;
using OpenSkiTime.TimyUsbHost;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class TimyUsbContractTests
{
    [Fact]
    public void AutomaticSelectionResumesWithNewSdkIdOnlyAfterSelectedDeviceDisconnects()
    {
        var selection = new TimyUsbSelection("");
        Assert.True(selection.Connect("1"));
        Assert.False(selection.Connect("2"));
        Assert.False(selection.Disconnect("2"));
        Assert.True(selection.Accepts("1"));
        Assert.False(selection.Accepts("2"));
        Assert.True(selection.Disconnect("1"));
        Assert.False(selection.Accepts("1"));
        Assert.True(selection.Connect("3"));
        Assert.True(selection.Accepts("3"));
        Assert.False(selection.Accepts("1"));
        Assert.False(selection.Accepts("2"));
    }

    [Fact]
    public void ExplicitSelectionNeverSilentlySwitchesToAnotherSdkId()
    {
        var selection = new TimyUsbSelection("2");
        Assert.False(selection.Connect("1"));
        Assert.True(selection.Connect("2"));
        Assert.True(selection.Disconnect("2"));
        Assert.False(selection.Connect("3"));
        Assert.False(selection.Accepts("3"));
        Assert.False(selection.Accepts("2"));
        Assert.True(selection.Connect("2"));
        Assert.True(selection.Accepts("2"));
    }

    // Synthetic SDK contract, not vendor binaries or a personal timing trace.
    public sealed class Device
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
            Justification = "Regression fixture must match ALGE's public-field SDK contract.")]
        public int Id = 1;
    }
    public sealed class Received
    {
        public Device Device { get; } = new();
        public byte[] Data { get; init; } = [16, 0, 0, 0];
        public string Text { get; init; } = "";
    }

    [Fact]
    public void NativeSdkPublicIdFieldAndBothUnmodifiedInputFieldsAreCaptured()
    {
        var e = new Received { Text = " 0001 C0 12:00:00.1234\r  \0" };
        Assert.Equal("1", TimySdkEvent.DeviceId(e));
        using var json = JsonDocument.Parse(TimySdkEvent.Capture(e));
        Assert.Equal(e.Data, Convert.FromBase64String(json.RootElement.GetProperty("sdkBytes").GetString()!));
        Assert.Equal(e.Text, Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("sdkTextUtf8").GetString()!)));
    }

    [Fact]
    public void UsbChunksDecodeLosslesslyDespiteBrokenSdkByteArrayAndPadding()
    {
        var session = TimingRulesTests.Session(TimingRulesTests.List());
        var decoder = new AlgeDecoderFactory().Create(session, "alge-timy-sdk/v1", "Timy:1", "1");
        Assert.Empty(decoder.Feed(Packet(session, 1, " 0001 C0 12:00:")));
        var impulse = Assert.Single(decoder.Feed(Packet(session, 2, "00.1234 00\r    ")));
        Assert.Equal(ObservationKind.Impulse, impulse.Kind);
        Assert.Equal(4, impulse.Precision);
        Assert.Equal(1, impulse.PacketSequence);
        Assert.Equal("12:00:00.1234000", TimingTime.FormatTimeOfDay(impulse.DeviceTicks));
        Assert.Null(impulse.SuggestedBib);
        var clock = Assert.Single(decoder.Feed(Packet(session, 3, "12:00:01.0  \r   ")));
        Assert.Equal(ObservationKind.Information, clock.Kind);
        Assert.Empty(decoder.Complete()); // USB transport padding is not a truncated timing impulse.
    }

    [Fact]
    public void InvalidSdkInputIsReviewableAndCannotSpliceTwoPartialImpulses()
    {
        var session = TimingRulesTests.Session(TimingRulesTests.List());
        var decoder = new AlgeTimySdkDecoder(session, "Timy:1", "1");
        Assert.Empty(decoder.Feed(Packet(session, 1, " 0001 C0 12:00:00.")));
        Assert.All(decoder.Feed(Packet(session, 2, "invalid \uFFFD")), x => Assert.Equal(ObservationKind.Invalid, x.Kind));
        Assert.Equal(ObservationKind.Invalid, Assert.Single(decoder.Feed(Packet(session, 3, "1234 00\r"))).Kind);
        Assert.Empty(decoder.Complete());
    }

    private static RawTimingPacket Packet(CaptureSession session, long sequence, string text) =>
        new(session.Id, sequence, TimingRulesTests.At, "alge-timy-sdk/v1", "Timy:1", "1",
            TimySdkEvent.Capture(new Received { Text = text }));

    [Fact]
    public async Task KeyboardStartIntermediateAndFinishCaptureCalculateAndReopenWithoutManualApproval()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-keyboard-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "synthetic.ost");
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore(), new AlgeDecoderFactory());
            var list = await TimingStorageTests.SeedAsync(workspace, file, 2, 1);
            var timing = workspace.Timing!;
            await timing.SelectRunAsync(list.Id);
            await timing.FollowStartOrderAsync(true);
            var source = new KeyboardUsbSource();
            await timing.StartAsync(source, new("Timy 2/3 · USB", "Synthetic", TimingRulesTests.Date)
                { IntermediateChannels = [2] }, "Synthetic operator");
            await TimingStorageTests.UntilAsync(() => timing.Snapshot!.Complete);
            Assert.Equal(new long?[] { 502, 798 }, timing.Snapshot!.Results.Select(x => x.Hundredths));
            Assert.Equal(201, timing.Snapshot.Results[0].Splits[0].Hundredths);
            Assert.All(timing.Snapshot.Observations, x => { Assert.True(x.Observation.Manual); Assert.Equal(2, x.Observation.Precision); });
            Assert.All(timing.Snapshot.Audit, x => Assert.Equal(DecisionKind.Assignment, x.After.Kind));
            await timing.StopAsync();
            var stored = await workspace.ReadTimingAsync(list.Id);
            Assert.Equal(source.Frames.Select(Convert.ToHexString), stored.Packets.OrderBy(x => x.Sequence).Select(x => Convert.ToHexString(x.Bytes)));
            await workspace.CloseAsync(); await workspace.OpenAsync(file);
            await workspace.Timing!.SelectRunAsync(list.Id);
            Assert.Equal(new long?[] { 502, 798 }, workspace.Timing.Snapshot!.Results.Select(x => x.Hundredths));
            Assert.True(workspace.Timing.Snapshot.Complete);
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "openskitime-keyboard-tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Directory.Delete(root, true); }
        }
    }

    private sealed class KeyboardUsbSource : ITimingSource
    {
        public byte[][] Frames { get; } = new[]
        {
            "13:15:00.0\r    0101 C0M 13:15:",
            "00.98   00\r    0102 C2M 13:15:02.99   00\r   ",
            " 0103 C1M 13:15:06.00   00\r    0104 C0M 13:15:10.01   00\r",
            "    0105 C1M 13:15:17.99   00\r   "
        }.Select(text => TimySdkEvent.Capture(new Received { Text = text })).ToArray();

        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            status("Synthetic Timy keyboard input");
            foreach (var bytes in Frames) { await receive(new("alge-timy-sdk/v1", "Timy:Synthetic", "1", bytes)); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

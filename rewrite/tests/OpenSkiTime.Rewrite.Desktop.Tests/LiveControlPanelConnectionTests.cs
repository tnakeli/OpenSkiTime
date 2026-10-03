using System.IO.Pipes;
using System.Net.Http.Json;
using System.Text.Json;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.ControlPanel;
using OpenSkiTime.LiveTiming.Tests;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

public sealed class LiveControlPanelConnectionTests
{
    [Fact]
    public async Task PanelStartObtainsFreshSnapshotAndSettingsBeforePublishingAndDisconnectStopsWorkers()
    {
        var name = "OpenSkiTime-panel-test-" + Guid.NewGuid().ToString("N");
        await using var parent = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var session = new LiveControlSession();
        await using var connection = new PanelConnection(name, session);
        var connectionTask = connection.RunAsync();
        await parent.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(parent, leaveOpen: true);
        var writer = new StreamWriter(parent, leaveOpen: true) { AutoFlush = true };
        var competitionId = Guid.NewGuid();
        var time = DateTimeOffset.UnixEpoch;
        var initial = new LiveSnapshot(1, new("Synthetic SL", "Test slope", "SL", new(2026, 10, 3), false, "", "L", "", 0),
            [new(1, "TEST", "Synthetic", "FIN", "Test club", "")], 1,
            [new(1, time, [1], [new(1, LiveStatus.Ready, null, null, null, time)])], time);
        await session.ApplyAsync(new(Guid.NewGuid(), ControlPanelAction.State, competitionId, initial));
        var fresh = initial with { Version = 2, Runs = [new(1, time, [1], [new(1, LiveStatus.Finished, 1234, 1, 0, time)])] };
        var settings = new LiveConnectionSettings($"http://localhost:{ProcessFixture.Port()}", "http://localhost:5079", "https://livedata.fis-ski.com/al/", "live.fis-ski.com", 1550, "", false, "Europe/Helsinki");
        var vm = new PanelViewModel(session, connection); vm.Initialize(settings);
        var prepareReply = Task.Run(async () =>
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var output = JsonSerializer.Deserialize<ControlPanelOutput>(line!, LiveJson.Options)!;
                if (output.SnapshotRequestId is not { } id) { continue; }
                Assert.Equal(settings, output.RequestedSettings);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlPanelInput(id, ControlPanelAction.State, competitionId, fresh), LiveJson.Options));
                return;
            }
        });
        await vm.ControlCommand.ExecuteAsync(vm.Channels[0].Start);
        await prepareReply;
        Assert.Equal("", vm.Error);
        Assert.Equal(2, session.Snapshot!.Version);
        await ProcessFixture.Until(() => session.ReadState().Channels[0].Health.State == PublisherState.Running);
        var local = session.ReadState().Channels[0];
        using var worker = System.Diagnostics.Process.GetProcessById(local.ProcessId!.Value);
        using var http = new HttpClient();
        var url = new Uri(local.Health.PublicUrl!);
        var state = await http.GetFromJsonAsync<LiveSnapshot>(settings.LocalEndpoint + "/api/sessions/" + url.Segments[^1] + "/state", LiveJson.Options);
        Assert.Equal(1234, state!.Runs[0].Results[0].Hundredths);
        await session.ApplyAsync(new(Guid.NewGuid(), ControlPanelAction.State, competitionId, initial));
        Assert.Equal(2, session.Snapshot.Version);
        await writer.DisposeAsync(); parent.Dispose();
        await connectionTask.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }
}

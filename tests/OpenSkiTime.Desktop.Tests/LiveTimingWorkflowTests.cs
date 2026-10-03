using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Tests;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task LiveTimingUiPublishesLocalAndCloudAndCaptureSurvivesWorkerCrash()
    {
        var root=Path.Combine(Path.GetTempPath(),"openskitime-live-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid competitionId=Guid.Empty; string? cloudEndpoint=null;
        try
        {
            await using var cloudServer=new ServerProcess(); await cloudServer.Start(); cloudEndpoint=cloudServer.Endpoint;
            var file=Path.Combine(root,"Synthetic.ost"); var cache=new FisLocalStore(root); await cache.SaveListAsync(SyntheticFisArchive());
            await using var workspace=new SeriesWorkspace(new SqliteSeriesFileStore(),new AlgeDecoderFactory());
            var date=new DateOnly(2026,9,27);
            var series=await workspace.CreateAsync(file,new("Synthetic live race","Test slope","Test club",date,date,"FIN","2026/27"));
            series=await workspace.SaveCompetitionAsync(null,new("Synthetic SL","SL",date,Discipline.Slalom,RaceType.Fis,2,1,"9754"),series.Revision);
            var competition=series.Competitions[0]; competitionId=competition.Id; var revision=series.Revision;
            for(var i=0;i<12;i++)
            { revision=(await workspace.SaveDeskRowAsync(null,new($"TEST{i:00}","Synthetic",2000,$"{123456+i}","FIN","Test Club",Gender.Female),competition.Id,true,null,revision)).Revision; }
            using var vm=new MainViewModel(workspace,new FileDialogsStub { OpenPath=file,NewPath=file,BackupPath=file+".backup" },fisStore:cache,recentSeriesStore:new RecentSeriesStore(root));
            await vm.OpenSeriesCommand.ExecuteAsync(null); await vm.OpenDrawRunCommand.ExecuteAsync(new DrawDestination(competition,1)); await vm.PrepareDrawCommand.ExecuteAsync(null);
            await vm.OpenTimingRunCommand.ExecuteAsync(new DrawDestination(competition,1)); Assert.False(vm.IsError,vm.StatusMessage);
            var window=new MainWindow { DataContext=vm,Width=1280,Height=850 }; window.Show();
            try
            {
                vm.LiveTimeZone="Europe/Helsinki"; vm.LiveLocalEndpoint=$"http://localhost:{ProcessFixture.Port()}"; vm.LiveCloudEndpoint=cloudServer.Endpoint;
                var local=vm.LiveChannels[0]; var cloud=vm.LiveChannels[1];
                await local.StartCommand.ExecuteAsync(null); await cloud.StartCommand.ExecuteAsync(null);
                await LiveUiUntil(()=>local.Status=="Running"&&cloud.Status=="Running");
                Assert.Equal("",vm.LiveTimingError); Assert.NotEmpty(cloud.Expiration);
                using var http=new HttpClient();
                var localId=new Uri(local.PublicUrl).Segments[^1]; var cloudId=new Uri(cloud.PublicUrl).Segments[^1];
                vm.TimingSource="Simulator"; vm.TimingIntermediateChannels="2";
                await vm.ConnectTimingCommand.ExecuteAsync(null); await vm.ToggleTimingChannelCommand.ExecuteAsync("start"); await vm.ToggleTimingChannelCommand.ExecuteAsync("finish"); await vm.ToggleTimingChannelCommand.ExecuteAsync("intermediate:1");
                var bib=vm.TimingRows[0].Bib; vm.SimulationTime="12:00:00.1234567";
                await vm.SimulatePulseCommand.ExecuteAsync("start"); await WaitTimingAsync(vm,()=>vm.OnCourseRows.Count==1);
                await LiveUiUntil(()=>local.Detail.Contains("Last OK",StringComparison.Ordinal));
                await LiveApiUntil(http,vm.LiveCloudEndpoint,cloudId,s=>s.Runs[0].Results.First(r=>r.Bib==bib).Status==LiveStatus.OnCourse);
                using(var worker=System.Diagnostics.Process.GetProcessById(cloud.WorkerProcessId!.Value)) { worker.Kill(true); await worker.WaitForExitAsync(); }
                await LiveUiUntil(()=>cloud.Status=="Error");
                Assert.True(vm.IsTimingConnected); Assert.True(workspace.Timing!.IsActive);
                vm.SimulationTime="12:00:21.9999999"; await vm.SimulatePulseCommand.ExecuteAsync("intermediate:1");
                await WaitTimingAsync(vm,()=>vm.TimingRows.First(r=>r.Bib==bib).HasSplits);
                vm.SimulationTime="12:01:00.9999999"; await vm.SimulatePulseCommand.ExecuteAsync("finish");
                await WaitTimingAsync(vm,()=>vm.TimingRows.First(r=>r.Bib==bib).Status=="Finished");
                Assert.Equal("1:00.87",vm.TimingRows.First(r=>r.Bib==bib).Time);
                await LiveApiUntil(http,vm.LiveLocalEndpoint,localId,s=>s.Runs[0].Results.First(r=>r.Bib==bib).Status==LiveStatus.Finished);
                await cloud.StartCommand.ExecuteAsync(null); await LiveUiUntil(()=>cloud.Status=="Running");
                Assert.Equal(cloudId,new Uri(cloud.PublicUrl).Segments[^1]);
                await LiveApiUntil(http,vm.LiveCloudEndpoint,cloudId,s=>s.Runs[0].Results.First(r=>r.Bib==bib).Hundredths==6087);
                var raw=await workspace.ReadTimingAsync(workspace.Timing.ListId!.Value);
                Assert.Equal(3,raw.Packets.Count); Assert.Contains(raw.Packets,p=>Encoding.ASCII.GetString(p.Bytes).Contains("1234567",StringComparison.Ordinal));
                var panelId=vm.LiveControlPanelProcessId!.Value;
                await vm.OpenLiveTimingCommand.ExecuteAsync(null);
                Assert.Equal(panelId,vm.LiveControlPanelProcessId);
                using var localWorker=System.Diagnostics.Process.GetProcessById(local.WorkerProcessId!.Value);
                using var cloudWorker=System.Diagnostics.Process.GetProcessById(cloud.WorkerProcessId!.Value);
                var localHealth=await http.GetFromJsonAsync<System.Text.Json.JsonElement>(vm.LiveLocalEndpoint+"/health");
                using var localServer=System.Diagnostics.Process.GetProcessById(localHealth.GetProperty("processId").GetInt32());
                using(var panel=System.Diagnostics.Process.GetProcessById(panelId))
                {
                    Assert.Equal("OpenSkiTime.LiveTiming.ControlPanel",panel.ProcessName);
                    Assert.NotEqual(IntPtr.Zero,panel.MainWindowHandle);
                    Assert.Contains("Live timing control panel",panel.MainWindowTitle);
                    panel.Kill(); await panel.WaitForExitAsync();
                }
                await LiveUiUntil(()=>local.Status=="Stopped"&&cloud.Status=="Stopped");
                await localWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await cloudWorker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await localServer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(vm.IsTimingConnected); Assert.True(workspace.Timing.IsActive);
                vm.SimulationTime="12:02:00.1234567"; await vm.SimulatePulseCommand.ExecuteAsync("start");
                await WaitTimingAsync(vm,()=>vm.OnCourseRows.Count==1);
                Assert.Equal(4,(await workspace.ReadTimingAsync(workspace.Timing.ListId!.Value)).Packets.Count);
                await local.StartCommand.ExecuteAsync(null); await cloud.StartCommand.ExecuteAsync(null);
                await LiveUiUntil(()=>local.Status=="Running"&&cloud.Status=="Running");
                Assert.NotEqual(panelId,vm.LiveControlPanelProcessId);
                Assert.Equal(cloudId,new Uri(cloud.PublicUrl).Segments[^1]);
                await LiveApiUntil(http,vm.LiveCloudEndpoint,cloudId,s=>s.Runs[0].Results.Any(r=>r.Status==LiveStatus.OnCourse));
                await cloud.StopCommand.ExecuteAsync(null); await LiveUiUntil(()=>cloud.Status=="Stopped");
                await cloud.RefreshCommand.ExecuteAsync(null); await LiveUiUntil(()=>cloud.Status=="Running");
                await local.DeleteCommand.ExecuteAsync(null); await cloud.DeleteAllDataCommand.ExecuteAsync(null);
                await LiveUiUntil(()=>local.PublicUrl==""&&cloud.PublicUrl=="");
                using var missing=await http.GetAsync(vm.LiveCloudEndpoint+$"/api/sessions/{cloudId}/state"); Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);
                using(var panel=System.Diagnostics.Process.GetProcessById(vm.LiveControlPanelProcessId!.Value))
                {
                    Assert.True(panel.CloseMainWindow());
                    await panel.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                await LiveUiUntil(()=>local.Status=="Stopped"&&cloud.Status=="Stopped");
                Assert.True(vm.IsTimingConnected); await vm.DisconnectTimingCommand.ExecuteAsync(null);
            }
            finally { if(workspace.Timing?.IsActive==true) { await vm.DisconnectTimingCommand.ExecuteAsync(null); } window.Close(); }
        }
        finally
        {
            if(OperatingSystem.IsWindows()&&cloudEndpoint is not null&&competitionId!=Guid.Empty)
            { new WindowsCredentialStore("OpenSkiTime.LiveTiming:"+competitionId.ToString("N")+":"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cloudEndpoint)))[..16]).Remove(); }
            Directory.Delete(root,true);
        }
    }
    private static async Task LiveUiUntil(Func<bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(!condition()) { await Task.Delay(50,timeout.Token); Dispatcher.UIThread.RunJobs(); }
    }
    private static async Task LiveApiUntil(HttpClient http,string endpoint,string sessionId,Func<LiveSnapshot,bool> condition)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(true)
        {
            var snapshot=await http.GetFromJsonAsync<LiveSnapshot>(endpoint+$"/api/sessions/{sessionId}/state",LiveJson.Options,timeout.Token);
            if(snapshot is not null&&condition(snapshot)) { return; }
            await Task.Delay(100,timeout.Token); Dispatcher.UIThread.RunJobs();
        }
    }
}

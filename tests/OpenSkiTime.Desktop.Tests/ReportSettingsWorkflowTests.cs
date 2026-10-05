using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    private static readonly string[] s_reportSettingsTabs = ["FIS", "Timing devices", "Timing report", "Live timing", "About"];
    private static readonly string[] s_reportSettingsCalls = ["equipment", "upload", "upload"];
    [Fact]
    public void CloudDeviceCannotBeReusedAsAnIndependentTimingSystem()
    {
        var date = new DateOnly(2026, 10, 3);
        var left = new CaptureOptions("MT1 · ALGE Results", "synthetic A", date,
            StartDeviceId: "100", FinishDeviceId: "101");
        var right = new CaptureOptions("MT1 · ALGE Results", "synthetic B", date,
            StartDeviceId: "200", FinishDeviceId: "100");
        Assert.True(MainViewModel.SameLocalTimingEndpoint(left, right));
        Assert.True(MainViewModel.SameLocalTimingEndpoint(right, left));
        Assert.False(MainViewModel.SameLocalTimingEndpoint(left, right with { FinishDeviceId = "201" }));
    }

    [Fact]
    public void EditingSerialPreservesHomologationEvidenceWhileChangingIdentityRequiresNewLookup()
    {
        var device = new TimingReportDevice("Synthetic", "Timer", "123", "SYN.1")
            { ValidUntilSeason = 2028, HomologationRetrievedAt = DateTimeOffset.UnixEpoch };
        var editor = new TimingReportDeviceEditor("A", 1);
        editor.Load(device); editor.Serial = "456";
        Assert.Equal(2028, editor.Values.ValidUntilSeason);
        Assert.Equal(DateTimeOffset.UnixEpoch, editor.Values.HomologationRetrievedAt);
        editor.Homologation = "SYN.2";
        Assert.Null(editor.Values.ValidUntilSeason); Assert.Null(editor.Values.HomologationRetrievedAt);
        editor.Load(device); editor.Model = "Other timer";
        Assert.Null(editor.Values.ValidUntilSeason);
        editor.Load(device); editor.Brand = "Other maker";
        Assert.Null(editor.Values.ValidUntilSeason);
    }
    [Theory]
    [InlineData("Timy 2/3 · USB", "Timy USB A", "Timy 2/3 · USB", "Timy USB B", false)]
    [InlineData("Timy 2/3 · USB", "Timy USB A", "Timy 2/3 · USB", "Timy USB A", true)]
    [InlineData("Timy 2/3 · USB", "Timy USB", "Timy 2/3 · USB", "Timy USB B", true)]
    [InlineData("MT1 · USB / serial", "COM3", "MT1 · USB / serial", "com3", true)]
    [InlineData("MT1 · USB / serial", "COM3", "MT1 · USB / serial", "COM4", false)]
    public void DeviceOwnershipDetectionWorksInBothConnectionOrders(string leftSource, string leftEndpoint,
        string rightSource, string rightEndpoint, bool same)
    {
        var date = new DateOnly(2026, 10, 3);
        var left = new CaptureOptions(leftSource, leftEndpoint, date, 0, 1, false, "test");
        var right = new CaptureOptions(rightSource, rightEndpoint, date, 0, 1, false, "test");
        Assert.Equal(same, MainViewModel.SameLocalTimingEndpoint(left, right));
        Assert.Equal(same, MainViewModel.SameLocalTimingEndpoint(right, left));
    }
    [AvaloniaFact]
    public async Task SettingsTabsSaveDefaultsAndOneKeyServesBothXmlArtifactsAndEquipment()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var calls = new List<string>();
            using var handler = new ReportSettingsHandler(request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert.Equal("synthetic-shared-key", Assert.Single(request.Headers.GetValues("X-Api-Key")));
                    calls.Add("equipment");
                    return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] {
                        new FisTimingDevice(1, "Synthetic Timer", "SYN.001T.26", 2030, 1, "SYNTHETIC", 1, "Timer", 10000, null, true) })) };
                }
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("synthetic-shared-key", request.Headers.Authorization.Parameter);
                calls.Add("upload");
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                    uuid = Guid.NewGuid(), testMode = true, outcome = "success",
                    summary = new { total = 1, accepted = 0, processed = 1, rejected = 0, isComplete = true },
                    files = new[] { new { fileName = "FIN9991.xml", status = "processed", comments = "Synthetic" } }
                }), Encoding.UTF8, "application/json") };
            });
            using var http = new HttpClient(handler);
            var credentials = new ReportSettingsCredential();
            var defaults = new TimingReportDefaultsStore(root);
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, credentials), informationHttp: http, submissionHttp: http,
                reportDefaultsStore: defaults, timingDeviceCache: new FisTimingDeviceCache(root), recentSeriesStore: new(root));
            vm.ShowSettingsCommand.Execute(null);
            var window = new Window { Content = new ScrollViewer { Content = new SettingsView { DataContext = vm } }, Width = 1100, Height = 900 };
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            Assert.Equal(s_reportSettingsTabs, tabs.Items.OfType<TabItem>().Select(x => x.Header!.ToString()).ToArray());
            var keyBox = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "FisApiKeyInputBox");
            keyBox.Focus(); window.KeyTextInput("synthetic-shared-key"); Dispatcher.UIThread.RunJobs();
            Assert.Equal("synthetic-shared-key", vm.FisApiKeyInput);
            PressSettingsControl(window, window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Save key")));
            Assert.Equal("synthetic-shared-key", credentials.Read()); Assert.Equal("", vm.FisApiKeyInput);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), x => x.Watermark?.Contains("personal access token", StringComparison.OrdinalIgnoreCase) == true);
            await vm.RefreshTimingDeviceHomologationsCommand.ExecuteAsync(null);
            vm.SelectedTimingDeviceHomologation = Assert.Single(vm.TimingDeviceHomologations);
            vm.SelectedReportDefaultDevice = vm.ReportDefaultDevices[0];
            vm.ApplyTimingDeviceHomologationCommand.Execute(null);
            Assert.Equal("SYN.001T.26", vm.ReportDefaultDevices[0].Homologation);
            tabs.SelectedIndex = 2; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<DataGrid>(), x => x.Name == "ReportEquipmentDefaultsGrid" && x.IsEffectivelyVisible);
            vm.ReportDefaultChief.LastName = "synthetic chief"; vm.ReportDefaultChief.Nation = "fin";
            vm.ReportDefaultTimekeeper.LastName = "synthetic timekeeper";
            PressSettingsControl(window, window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Save report defaults")));
            Assert.Equal("SYNTHETIC CHIEF", defaults.Load().ChiefOfTiming.LastName);
            Assert.Equal("FIN", vm.ReportDefaults.ChiefOfTiming.Nation);
            Assert.Equal("SYN.001T.26", defaults.Load().TimerA.Homologation);
            Assert.Equal(2030, defaults.Load().TimerA.ValidUntilSeason);
            Assert.NotNull(defaults.Load().TimerA.HomologationRetrievedAt);
            var approval = new ApprovedResult(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), null,
                "synthetic", DateTimeOffset.UnixEpoch, "Synthetic", "FIN9991.xml", Encoding.UTF8.GetBytes("<Synthetic />"), 1m, 1m);
            vm.SelectedResultApproval = approval;
            await vm.SendApprovedXmlTestCommand.ExecuteAsync(null);
            Assert.Contains("processed 1", vm.ResultSubmissionStatus);
            await vm.UploadApprovedFisArtifactTestAsync(new(Guid.NewGuid(), "FIN9991.timing.xml", Encoding.UTF8.GetBytes("<Synthetic />")));
            Assert.Equal(s_reportSettingsCalls, calls);
            tabs.SelectedIndex = 1; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Add B Clock") && x.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Connect backup"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<DataGrid>(), x => x.Name == "ReportEquipmentDefaultsGrid" && x.IsEffectivelyVisible);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), x => Equals(x.Content, "Save report defaults") && x.IsEffectivelyVisible);
            window.Close();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DefaultsAndCatalogueReopenOfflineAndInvalidReplacementPreservesDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-settings-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new TimingReportDefaultsStore(root);
            var saved = store.Save(new() { ChiefOfTiming = new("Test", "racer", "fin"),
                TimerA = new("Synthetic", "Timer", "123", "SYN.1") { ValidUntilSeason = 2028, HomologationRetrievedAt = DateTimeOffset.UnixEpoch } });
            Assert.Equal("RACER", new TimingReportDefaultsStore(root).Load().ChiefOfTiming.LastName);
            Assert.Equal(2028, new TimingReportDefaultsStore(root).Load().TimerA.ValidUntilSeason);
            Assert.Equal(DateTimeOffset.UnixEpoch, new TimingReportDefaultsStore(root).Load().TimerA.HomologationRetrievedAt);
            var report = new TimingReportDraft { Defaults = saved };
            store.Save(saved with { ChiefOfTiming = new("Other", "official", "swe") });
            Assert.Equal("RACER", report.Defaults.ChiefOfTiming.LastName);
            Assert.Throws<DomainValidationException>(() => store.Save(saved with { ChiefOfTiming = new(Nation: "invalid") }));
            Assert.Equal("OFFICIAL", store.Load().ChiefOfTiming.LastName);
            var cache = new FisTimingDeviceCache(root);
            var device = new FisTimingDevice(1, "Timer", "SYN.1", 2025, 1, "Synthetic", 1, "Timer", 10000, null, false);
            cache.Save(new(DateTimeOffset.UnixEpoch, [device]));
            Assert.Throws<DomainValidationException>(() => cache.Save(new(DateTimeOffset.UnixEpoch, [device, device])));
            Assert.Equal(device, Assert.Single(new FisTimingDeviceCache(root).Load()!.Devices));
            var timing = new TimingPreferencesStore(root);
            var backup = new TimingConnection(TimingSourceType.Mt1Serial) { Port = "COM9" };
            timing.Save(new TimingRoleConfiguration([new(TimingRole.BackupStart, backup, 0), new(TimingRole.BackupFinish, backup, 1)])
            { BackupWarnings = new(3, 12, 9) });
            Assert.Equal(new BackupClockWarnings(3, 12, 9), new TimingPreferencesStore(root).Load()!.BackupWarnings);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task AboutAndLiveTimingTabsShowVersionLinksAndSavePublisherKeyPerServer()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-about-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root));
            var stores = new Dictionary<string, ReportSettingsCredential>();
            vm.LiveCredentialStore = target => stores.TryGetValue(target, out var store) ? store : stores[target] = new();
            Assert.Equal(ProductInfo.DisplayName + " · FIS rules 2026-27", vm.WindowTitle);
            vm.ShowSettingsCommand.Execute(null);
            var window = new Window { Content = new ScrollViewer { Content = new SettingsView { DataContext = vm } }, Width = 1100, Height = 900 };
            window.Show();
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            tabs.SelectedIndex = 4; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("OpenSkiTime", window.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "AboutProductText").Text);
            Assert.Contains("Version " + ProductInfo.Version, window.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "AboutVersionText").Text, StringComparison.Ordinal);
            var links = window.GetVisualDescendants().OfType<HyperlinkButton>().Select(x => x.NavigateUri!.ToString()).ToArray();
            foreach (var url in new[] { ProductInfo.RepositoryUrl, ProductInfo.LicenseUrl, ProductInfo.ThirdPartyNoticesUrl, ProductInfo.IssuesUrl, ProductInfo.SecurityReportUrl, ProductInfo.PrivacyUrl, ProductInfo.WebsiteUrl })
            { Assert.Contains(url, links); }
            tabs.SelectedIndex = 3; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal("No publisher key saved for this server.", vm.LivePublisherKeyStatus);
            vm.LivePublisherKeyInput = "ost_pk_synthetic-publisher-key-0123456789";
            PressSettingsControl(window, window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Save key")));
            Assert.False(vm.IsError, vm.StatusMessage); Assert.Equal("", vm.LivePublisherKeyInput);
            Assert.Equal("ost_pk_synthetic-publisher-key-0123456789", stores["OpenSkiTime.LiveTiming.PublisherKey:https://live.openskiti.me"].Read());
            // Keys belong to one server origin: another server shows none until its own key is saved.
            vm.LiveCloudEndpoint = "https://self-hosted.example.invalid/";
            Assert.Equal("No publisher key saved for this server.", vm.LivePublisherKeyStatus);
            vm.LiveCloudEndpoint = "https://LIVE.openskiti.me";
            Assert.Equal("Publisher key saved for this server and Windows user.", vm.LivePublisherKeyStatus);
            PressSettingsControl(window, window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Remove key")));
            Assert.Null(stores["OpenSkiTime.LiveTiming.PublisherKey:https://live.openskiti.me"].Read());
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class ReportSettingsCredential : ICredentialStore
    {
        private string? _key;
        public bool Exists() => _key is not null;
        public string? Read() => _key;
        public void Save(string key) => _key = key;
        public void Remove() => _key = null;
    }
    private sealed class ReportSettingsHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request)); }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.ControlPanel;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class LiveTimingLayoutTests
{
    [AvaloniaFact]
    public async Task TimingStripIsCompactAndExposesDetailedHealthOnHover()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var window = new Window { Width = 540, Height = 140 };
        using var vm = new MainViewModel(workspace, new AvaloniaFileDialogs(window));
        vm.LiveChannels[0].Update(new(PublisherKind.Local, new(PublisherState.Running, "http://localhost:5078", LastSuccessfulPublish: DateTimeOffset.UnixEpoch), 100));
        vm.LiveChannels[1].Update(new(PublisherKind.Cloud, new(PublisherState.Error, "http://localhost:5079", Error: "Synthetic connection error"), 101));
        var view = new LiveTimingView { DataContext = vm, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        window.Content = new Border { Classes = { "raceConnection" }, Child = view, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = view.FindControl<Button>("LiveTimingButton")!;
            Assert.Null(button.Flyout); Assert.Same(vm.OpenLiveTimingCommand, button.Command);
            Assert.Contains("secondaryAction", button.Classes);
            Assert.True(view.Bounds.Width < 310, $"Live strip width {view.Bounds.Width}");
            var labels = view.GetVisualDescendants().OfType<TextBlock>().Where(x => x.Text is "Local" or "Cloud" or "FIS").ToArray();
            Assert.Equal(3, labels.Length);
            // Each channel's dot shows its state: running, problem, stopped.
            Color Dot(string label) => Assert.IsAssignableFrom<ISolidColorBrush>(((Panel)labels.Single(x => x.Text == label).Parent!).Children.OfType<Border>().Single().Background).Color;
            Assert.Equal(Color.Parse("#16A34A"), Dot("Local"));
            Assert.Equal(Color.Parse("#DC2626"), Dot("Cloud"));
            Assert.Equal(Color.Parse("#94A3B8"), Dot("FIS"));
            var tips = view.GetVisualDescendants().OfType<Border>().Select(ToolTip.GetTip).OfType<string>().ToArray();
            Assert.Contains(tips, x => x.Contains("Last OK", StringComparison.Ordinal));
            Assert.Contains(tips, x => x.Contains("Synthetic connection error", StringComparison.Ordinal));
            Save(window, "live-timing-strip.png");
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task AWakingCloudServerIsShownAsProgressAndAnErrorTakesPrecedence()
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        using var vm = new MainViewModel(workspace, new AvaloniaFileDialogs(new Window()));
        await using var session = new LiveControlSession();
        var panel = new PanelViewModel(session);
        var waking = new PublisherHealth(PublisherState.Starting, "https://live.example.test") { Notice = "Waking the live timing server." };
        vm.LiveChannels[1].Update(new(PublisherKind.Cloud, waking, 101)); panel.Channels[1].Update(new(PublisherKind.Cloud, waking, 101));
        Assert.Contains("Waking the live timing server.", vm.LiveChannels[1].StatusToolTip, StringComparison.Ordinal);
        Assert.Equal("Waking the live timing server.", panel.Channels[1].Detail);
        var failed = waking with { State = PublisherState.Reconnecting, Error = "Publish/health failed." };
        vm.LiveChannels[1].Update(new(PublisherKind.Cloud, failed, 101)); panel.Channels[1].Update(new(PublisherKind.Cloud, failed, 101));
        Assert.DoesNotContain("Waking", vm.LiveChannels[1].Detail, StringComparison.Ordinal);
        Assert.Equal("Publish/health failed.", panel.Channels[1].Detail);
    }
    [AvaloniaTheory]
    [InlineData(760, 560)]
    [InlineData(940, 790)]
    public async Task ControlPanelRendersReadableIndependentControlsWithoutHorizontalClipping(int width, int height)
    {
        await using var session = new LiveControlSession();
        var vm = new PanelViewModel(session);
        vm.Update(); // Radio defaults before any competition is open.
        var load = new LiveServerStats(DateTimeOffset.UnixEpoch, true, new(42, 3, 61, 25.44, 60), 120, new(530, 4.25, 12.4, 40, 5),
            new(12.3, 2, 180L << 20, 60L << 20, 1L << 30, 0));
        vm.Channels[0].Update(new(PublisherKind.Local, new(PublisherState.Running, "http://localhost:5078", PublicUrl: "http://localhost:5078/r/00000000-0000-0000-0000-000000000001")
            { Server = load, RoundTripMs = 85 }, 100));
        // Viewers, response times and resources, so the operator sees what limits the server when many people follow.
        Assert.True(vm.Channels[0].HasServerLoad); Assert.False(vm.Channels[1].HasServerLoad);
        var lines = vm.Channels[0].ServerLoad.Split(Environment.NewLine);
        Assert.Equal("Viewers 42 now · last 60 min: min 3, avg 25.4, max 61 · 120 on the server", lines[0]);
        Assert.Equal("Response avg 4.3 ms, p95 12 ms, max 40 ms (530 requests, 5 min) · round trip 85 ms", lines[1]);
        Assert.StartsWith("CPU 12.3 % of 2 cores · memory 180 MB (heap 60 MB) of 1.0 GB · queued work 0 · ", lines[2], StringComparison.Ordinal);
        vm.Channels[1].Update(new(PublisherKind.Cloud, new(PublisherState.Error, Error: "Connection unavailable. Check the cloud URL and network, then press Start."), 101));
        vm.Competition = "Synthetic slalom / Run 1";
        var window = new PanelWindow { DataContext = vm, Width = width, Height = height };
        var panelApp = new PanelApp(); panelApp.Initialize();
        var styles = panelApp.Styles.ToArray(); panelApp.Styles.Clear();
        foreach (var style in styles) { window.Styles.Add(style); }
        foreach (var resource in panelApp.Resources) { window.Resources[resource.Key] = resource.Value; }
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains("Live timing control panel", window.Title);
            foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.IsEffectivelyVisible && x.Viewport.Width > 0))
            { Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"{scroll.Extent} exceeds {scroll.Viewport}"); }
            var starts = window.GetVisualDescendants().OfType<Button>().Where(x => Equals(x.Content, "Start")).ToArray();
            Assert.Equal(3, starts.Length);
            foreach (var start in starts) { Assert.Same(vm.ControlCommand, start.Command); Assert.IsType<PanelChannelAction>(start.CommandParameter); Assert.True(start.Bounds.Width > 0); }
            var port = window.GetVisualDescendants().OfType<NumericUpDown>().Single(x => x.Value == 1550);
            Assert.True(port.Bounds.Width > 150);
            // Race radio: off and disabled until a competition is open; its status card sits under the channels.
            Assert.Equal("Off", window.GetVisualDescendants().OfType<TextBlock>().Single(x => x.Name == "RadioStatusText").Text);
            var budget = window.GetVisualDescendants().OfType<NumericUpDown>().Single(x => x.Value == 5m);
            Assert.True(budget.Bounds.Width > 150);
            Assert.False(window.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "SaveRadioButton").IsEffectivelyEnabled);
            Assert.Equal(2, window.GetVisualDescendants().OfType<ComboBox>().Count(x => x.SelectedItem is RadioChoice));
            Save(window, $"live-control-panel-{width}.png");
            foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(x => x.Extent.Height > x.Viewport.Height))
            { scroll.Offset = new Vector(0, scroll.Extent.Height); }
            window.UpdateLayout(); Save(window, $"live-control-panel-{width}-bottom.png");
        }
        finally { window.Close(); }
    }
    private static void Save(Window window, string file)
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_LIVE_VISUAL_DIR");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        Directory.CreateDirectory(output);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, file));
    }
}

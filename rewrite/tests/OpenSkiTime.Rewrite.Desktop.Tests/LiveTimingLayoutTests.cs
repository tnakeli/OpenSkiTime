using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.ControlPanel;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

namespace OpenSkiTime.Rewrite.Tests;

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
            Assert.Equal(Color.Parse("#E6F3F4"), Assert.IsAssignableFrom<ISolidColorBrush>(button.Foreground).Color);
            Assert.True(view.Bounds.Width < 310, $"Live strip width {view.Bounds.Width}");
            var labels = view.GetVisualDescendants().OfType<TextBlock>().Where(x => x.Text is "Local" or "Cloud" or "FIS").ToArray();
            Assert.Equal(3, labels.Length);
            Assert.Equal(Color.Parse("#49BA91"), Assert.IsAssignableFrom<ISolidColorBrush>(labels.Single(x => x.Text == "Local").Foreground).Color);
            Assert.Equal(Color.Parse("#EF7777"), Assert.IsAssignableFrom<ISolidColorBrush>(labels.Single(x => x.Text == "Cloud").Foreground).Color);
            Assert.Equal(Color.Parse("#DCE6E9"), Assert.IsAssignableFrom<ISolidColorBrush>(labels.Single(x => x.Text == "FIS").Foreground).Color);
            var tips = view.GetVisualDescendants().OfType<Border>().Select(ToolTip.GetTip).OfType<string>().ToArray();
            Assert.Contains(tips, x => x.Contains("Last OK", StringComparison.Ordinal));
            Assert.Contains(tips, x => x.Contains("Synthetic connection error", StringComparison.Ordinal));
            Save(window, "live-timing-strip.png");
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(760, 560)]
    [InlineData(940, 790)]
    public async Task ControlPanelRendersReadableIndependentControlsWithoutHorizontalClipping(int width, int height)
    {
        await using var session = new LiveControlSession();
        var vm = new PanelViewModel(session);
        vm.Channels[0].Update(new(PublisherKind.Local, new(PublisherState.Running, "http://localhost:5078", PublicUrl: "http://localhost:5078/r/00000000-0000-0000-0000-000000000001"), 100));
        vm.Channels[1].Update(new(PublisherKind.Cloud, new(PublisherState.Error, Error: "Connection unavailable. Check the cloud URL and network, then press Start."), 101));
        vm.Competition = "Synthetic slalom / Run 1";
        var window = new PanelWindow { DataContext = vm, Width = width, Height = height };
        var panelApp = new PanelApp(); panelApp.Initialize();
        var styles = panelApp.Styles.ToArray(); panelApp.Styles.Clear();
        foreach (var style in styles) { window.Styles.Add(style); }
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
            var port = window.GetVisualDescendants().OfType<NumericUpDown>().Single();
            Assert.Equal(1550, port.Value);
            Assert.True(port.Bounds.Width > 150);
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

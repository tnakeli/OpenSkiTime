using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaTheory]
    [InlineData(980, 680)]
    [InlineData(1280, 800)]
    public async Task EightNavigationStepsAndSettingsRemainReachableAtSupportedWindowSizes(int width, int height)
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-settings-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var file = Path.Combine(root, "synthetic.ost");
            var date = new DateOnly(2026, 10, 3);
            await workspace.CreateAsync(file, new("Synthetic layout series", "Test slope", "Test club", date, date, "FIN", "2026/27"));
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = file, OpenPath = file, BackupPath = file + ".backup" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root),
                reportDefaultsStore: new(root), timingDeviceCache: new(root));
            var window = new MainWindow { DataContext = vm, Width = width, Height = height, WindowState = WindowState.Normal };
            await vm.OpenSeriesCommand.ExecuteAsync(null);
            vm.ShowSettingsCommand.Execute(null);
            vm.AuxiliarySource = vm.AuxiliarySources[0]; vm.AuxiliaryUsbId = "SYNTHETIC-B";
            vm.TimingOperator = "Synthetic timer";
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            try
            {
                var nav = window.GetVisualDescendants().OfType<Button>()
                    .Where(x => x.Classes.Contains("sectionNav") && x.Content is string text && text.Length > 0 && char.IsAsciiDigit(text[0]))
                    .ToArray();
                Assert.Equal(8, nav.Length);
                Assert.Contains(nav, x => Equals(x.Content, "8  PDF Factory  ▾"));
                Assert.Contains(nav, x => Equals(x.Content, "7  Timing report  ▾"));
                foreach (var button in nav) { WithinWindowWidth(window, button); }
                Assert.All(nav, button => Assert.True(button.IsEffectivelyVisible));
                Assert.Single(nav.Select(button => button.TranslatePoint(default, window)!.Value.Y).Distinct());
                var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
                var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
                var scroller = window.FindControl<ScrollViewer>("WorkspaceScroll")!;
                foreach (var tab in tabs.Items.OfType<TabItem>())
                {
                    scroller.Offset = default;
                    window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    PressSettingsControl(window, tab);
                    if (!tab.IsSelected) { CaptureSettingsWindow(window, width, height, "failed-" + tab.Header!.ToString()!.Replace(' ', '-')); }
                    var tabPoint = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
                    var hit = window.InputHitTest(tabPoint);
                    Assert.True(tab.IsSelected, $"Tab '{tab.Header}' was not selected at {tabPoint}; hit={hit}; offset={scroller.Offset}; bounds={tab.Bounds}.");
                    foreach (var control in settings.GetVisualDescendants().OfType<Control>()
                        .Where(x => x.IsEffectivelyVisible && x.Bounds.Width > 0 && x is TextBox or ComboBox or NumericUpDown or Button))
                    { WithinWindowWidth(window, control); }
                    CaptureSettingsWindow(window, width, height, tab.Header!.ToString()!.Replace(' ', '-'));
                    if (Equals(tab.Header, "Timing report"))
                    {
                        var grid = settings.FindControl<DataGrid>("ReportEquipmentDefaultsGrid")!;
                        Assert.True(grid.IsEffectivelyVisible);
                        grid.BringIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                        WithinWindowWidth(window, grid);
                        CaptureSettingsWindow(window, width, height, "equipment");
                    }
                    if (Equals(tab.Header, "Timing devices"))
                    {
                        Assert.DoesNotContain(settings.GetVisualDescendants().OfType<DataGrid>(),
                            x => x.Name == "ReportEquipmentDefaultsGrid" && x.IsEffectivelyVisible);
                        Assert.DoesNotContain(settings.GetVisualDescendants().OfType<Button>(),
                            x => x.IsEffectivelyVisible && Equals(x.Content, "Save report defaults"));
                    }
                    if (Equals(tab.Header, "Timing report"))
                    {
                        var save = settings.GetVisualDescendants().OfType<Button>()
                            .Single(x => x.IsEffectivelyVisible && Equals(x.Content, "Save report defaults"));
                        save.BringIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                        var point = save.TranslatePoint(default, scroller)!.Value;
                        Assert.InRange(point.Y, -1, scroller.Bounds.Height);
                        Assert.True(point.Y + save.Bounds.Height <= scroller.Bounds.Height + 1);
                    }
                }
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void WithinWindowWidth(Window window, Control control)
    {
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.True(point.X >= -1 && point.X + control.Bounds.Width <= window.Bounds.Width + 1,
            $"{control.GetType().Name} {control.Name} extends outside {window.Bounds.Width}px: {point.X}..{point.X + control.Bounds.Width}.");
    }

    private static void CaptureSettingsWindow(Window window, int width, int height, string name)
    {
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_SETTINGS_VISUAL_DIR");
        if (string.IsNullOrWhiteSpace(output)) { return; }
        Directory.CreateDirectory(output);
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, $"settings-{width}-{name}.png"));
    }
}

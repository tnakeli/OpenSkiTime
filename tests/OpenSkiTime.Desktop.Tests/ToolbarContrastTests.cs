using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task DisabledToolbarActionsStayReadableOnTheNavyBar()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-toolbar", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root), timingPreferencesStore: new(root));
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 700 };
            window.Show();
            try
            {
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                // Without a file, Backup / transfer and Close file are disabled items of the File menu.
                var file = window.FindControl<DropDownButton>("FileMenuButton")!;
                var items = Assert.IsType<MenuFlyout>(file.Flyout).Items.OfType<MenuItem>().ToArray();
                Assert.False(items.Single(x => Equals(x.Header, "Close file")).IsEffectivelyEnabled);
                Assert.False(items.Single(x => Equals(x.Header, "Backup / transfer")).IsEffectivelyEnabled);
                Assert.True(items.Single(x => Equals(x.Header, "Open file")).IsEffectivelyEnabled);
                // The title bar menus are light text on the navy bar.
                foreach (var menu in new[] { file, window.FindControl<DropDownButton>("SettingsMenuButton")! })
                {
                    var presenter = menu.GetVisualDescendants().OfType<ContentPresenter>().First(x => x.Name == "PART_ContentPresenter");
                    var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color;
                    var bar = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindControl<Border>("TitleBar")!.Background).Color;
                    Assert.True(Luminance(bar) < 0.1, $"Bar {bar}");
                    Assert.True(Luminance(foreground) - Luminance(bar) > 0.5, $"{menu.Name} foreground {foreground}");
                }
                CaptureDraw(window, Environment.GetEnvironmentVariable("OPENSKITIME_M4_VISUAL_DIR"), "toolbar-no-file.png");
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task HoveredPressedAndOpenTitleBarMenusKeepLightTextOnTheNavyBar()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-toolbar", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root), timingPreferencesStore: new(root));
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 700 };
            window.Show();
            try
            {
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                foreach (var control in new Control[] { window.FindControl<DropDownButton>("FileMenuButton")!, window.FindControl<DropDownButton>("SettingsMenuButton")! })
                {
                    foreach (var state in new[] { ":pointerover", ":pressed", ":flyout-open" })
                    {
                        var classes = (IPseudoClasses)control.Classes;
                        classes.Add(state); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                        var presenter = control.GetVisualDescendants().OfType<ContentPresenter>().First(x => x.Name == "PART_ContentPresenter");
                        var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                        var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color;
                        Assert.True(Luminance(background) < 0.1, $"{control.Name} {state} background {background}");
                        Assert.True(Luminance(foreground) - Luminance(background) > 0.5, $"{control.Name} {state} foreground {foreground}");
                        classes.Remove(state);
                    }
                }
                // Only the window frame uses the dark variant (light caption buttons); the content stays light.
                Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
                Assert.Equal(ThemeVariant.Light, window.FindControl<DropDownButton>("FileMenuButton")!.ActualThemeVariant);
                Assert.Equal(ThemeVariant.Light, window.GetVisualDescendants().OfType<TimingView>().Single().ActualThemeVariant);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte value) { var s = value / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}

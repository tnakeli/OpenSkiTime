using Avalonia.Controls;
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
                var close = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "Close file"));
                Assert.False(close.IsEffectivelyEnabled);
                var presenter = close.GetVisualDescendants().OfType<ContentPresenter>().First(x => x.Name == "PART_ContentPresenter");
                // Readable against the navy bar: a dark surface with light text, not Fluent's light disabled colours.
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color;
                Assert.True(Luminance(background) < 0.1, $"Background {background}");
                Assert.True(Luminance(foreground) - Luminance(background) > 0.2, $"Foreground {foreground}");
                Assert.Equal(1, close.Opacity);
                CaptureDraw(window, Environment.GetEnvironmentVariable("OPENSKITIME_M4_VISUAL_DIR"), "toolbar-no-file.png");
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task HoveredPressedAndOpenToolbarButtonsKeepLightTextOnTheNavyBar()
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
                var open = window.FindControl<SplitButton>("OpenFileButton")!;
                var race = window.FindControl<Button>("ActiveRaceButton")!;
                race.IsVisible = true; race.IsEnabled = true; // As with an open file.
                var newSeries = window.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "New series"));
                foreach (var (control, state) in new (Control, string)[] { (open, ":flyout-open"), (open, ":pointerover"), (open, ":pressed"),
                             (race, ":pointerover"), (race, ":pressed"), (newSeries, ":pressed") })
                {
                    var classes = (IPseudoClasses)control.Classes;
                    classes.Add(state); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                    // The visible surface of a split button is its inner primary part.
                    var target = control is SplitButton ? control.GetVisualDescendants().OfType<Button>().First(x => x.Name == "PART_PrimaryButton") : control;
                    // Hover and press land on the inner part; an open flyout is a state of the split button itself.
                    if (control is SplitButton && state != ":flyout-open") { ((IPseudoClasses)target.Classes).Add(state); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
                    var presenter = target.GetVisualDescendants().OfType<ContentPresenter>().First(x => x.Name == "PART_ContentPresenter");
                    var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color;
                    var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Foreground).Color;
                    Assert.True(Luminance(background) < 0.1, $"{control.Name ?? "New series"} {state} background {background}");
                    Assert.True(Luminance(foreground) - Luminance(background) > 0.5, $"{control.Name ?? "New series"} {state} foreground {foreground}");
                    classes.Remove(state);
                    if (control is SplitButton) { ((IPseudoClasses)target.Classes).Remove(state); }
                }
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

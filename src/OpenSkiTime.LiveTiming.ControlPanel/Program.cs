using Avalonia;

namespace OpenSkiTime.LiveTiming.ControlPanel;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<PanelApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}

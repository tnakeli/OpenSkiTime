using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace OpenSkiTime.LiveTiming.ControlPanel;

public sealed class PanelApp : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var session = new LiveControlSession();
            var args = desktop.Args ?? [];
            var connection = args.Length == 2 && args[0] == "--pipe" ? new PanelConnection(args[1], session) : null;
            var vm = new PanelViewModel(session, connection);
            var window = new PanelWindow { DataContext = vm }; vm.Window = window;
            desktop.MainWindow = window;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => vm.Update());
            timer.Start();
            var closing = false;
            window.Closing += async (_, e) =>
            {
                if (closing) { return; }
                e.Cancel = true; closing = true; timer.Stop();
                if (connection is not null) { await connection.DisposeAsync(); }
                else { await session.DisposeAsync(); }
                window.Close();
            };
            if (connection is not null)
            {
                var initialized = false;
                connection.Received += input => Dispatcher.UIThread.Post(() =>
                {
                    if (!initialized && input.Settings is { } settings) { vm.Initialize(settings); initialized = true; }
                    if (input.Action == ControlPanelAction.Activate)
                    { window.WindowState = Avalonia.Controls.WindowState.Normal; window.Activate(); }
                });
                connection.Disconnected += () => Dispatcher.UIThread.Post(window.Close);
                _ = Task.Run(connection.RunAsync);
            }
            else { vm.Error = "Open a saved timing run in OpenSkiTime and use its Live timing button to connect this panel."; }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

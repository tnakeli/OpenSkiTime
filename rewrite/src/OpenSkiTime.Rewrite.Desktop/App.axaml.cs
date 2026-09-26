using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Persistence;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed class App : Avalonia.Application, IDisposable
{
    private SeriesWorkspace? _workspace;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var window = new MainWindow();
            window.DataContext = new MainViewModel(_workspace, new AvaloniaFileDialogs(window));
            desktop.MainWindow = window;
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (_workspace is not null)
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (_workspace is not null)
        {
            _workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _workspace = null;
        }
    }
}

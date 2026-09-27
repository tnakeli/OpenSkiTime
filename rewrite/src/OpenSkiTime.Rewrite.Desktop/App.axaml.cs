using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Persistence;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed class App : Avalonia.Application, IDisposable
{
    private SeriesWorkspace? _workspace;
    private MainViewModel? _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var window = new MainWindow();
            _viewModel = new MainViewModel(_workspace, new AvaloniaFileDialogs(window),
                new AvaloniaEntryExchange(window));
            window.DataContext = _viewModel;
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
        _viewModel?.Dispose();
        _viewModel = null;
        if (_workspace is not null)
        {
            _workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _workspace = null;
        }
    }
}

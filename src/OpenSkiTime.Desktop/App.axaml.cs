using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using OpenSkiTime.Desktop.Shell;
using OpenSkiTime.Desktop.ViewModels;

namespace OpenSkiTime.Desktop;

public class App : Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shellVm = Program.ServiceProvider.GetRequiredService<ShellViewModel>();
            var window = new ShellWindow { DataContext = shellVm };
            desktop.MainWindow = window;
            window.Show();
            await shellVm.LoadAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

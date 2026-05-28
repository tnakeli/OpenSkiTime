using Avalonia.Controls.ApplicationLifetimes;
using OpenSkiTime.Desktop.Dialogs;

namespace OpenSkiTime.Desktop.Services;

public interface IDialogService
{
    Task ShowMessageAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public Task ShowMessageAsync(string title, string message)
        => ShowMessageWindowAsync(title, message);

    public Task ShowErrorAsync(string title, string message)
        => ShowMessageWindowAsync($"Error — {title}", message);

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        var owner = GetMainWindow();
        if (owner is null)
        {
            return false;
        }

        var dialog = new ConfirmDialog();
        dialog.Configure(title, message);
        var result = await dialog.ShowDialog<bool>(owner);
        return result;
    }

    private static async Task ShowMessageWindowAsync(string title, string message)
    {
        var owner = GetMainWindow();
        if (owner is null)
        {
            Console.WriteLine($"[{title}] {message}");
            return;
        }

        var dialog = new MessageDialog();
        dialog.Configure(title, message);
        await dialog.ShowDialog(owner);
    }

    private static Avalonia.Controls.Window? GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Desktop;

public sealed partial class DebugBundleDialog : Window
{
    private readonly IFileDialogs _fileDialogs;
    public DebugBundleDialog() : this(null) { }
    internal DebugBundleDialog(IFileDialogs? fileDialogs)
    {
        _fileDialogs = fileDialogs ?? new AvaloniaFileDialogs(this);
        AvaloniaXamlLoader.Load(this);
        Closing += (_, e) =>
        {
            if (DataContext is MainViewModel { IsDebugBundleBusy: true }) { e.Cancel = true; }
        };
    }
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.IsDebugBundleBusy) { return; }
        // The native picker belongs to this modal window, not its disabled main-window owner.
        try
        {
            vm.DebugBundleStatus = "Choose a local destination for the ZIP.";
            var path = await _fileDialogs.ChooseDebugBundleAsync();
            if (path is null) { vm.DebugBundleStatus = "Export cancelled. No ZIP was created."; return; }
            await vm.SaveDebugBundleCommand.ExecuteAsync(path);
        }
        catch (OperationCanceledException) { vm.DebugBundleStatus = "Export cancelled. No ZIP was created."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
            or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        { vm.DebugBundleStatus = "Could not open the save dialog. Check folder access and retry. " + ex.Message; }
    }
}

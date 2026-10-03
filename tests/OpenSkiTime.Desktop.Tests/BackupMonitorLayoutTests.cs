using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class BackupMonitorLayoutTests
{
    [AvaloniaTheory]
    [InlineData(980, 680)]
    [InlineData(1280, 800)]
    public async Task BackupPanelIsReadOnlyAndHidingPreservesFocusAndRedWarning(int width, int height)
    {
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var window = new Window { Width = width, Height = height };
        using var vm = new MainViewModel(workspace, new AvaloniaFileDialogs(window));
        vm.ShowBackupTimes = true; vm.CanShowBackup = true;
        vm.BackupWarning = "Bib 12 finish: B signal missing";
        vm.BackupMonitorRows.Add(new(12, "Start", "12:00:00.1234000", "12:00:00.1235000", "+0.0001000", "Observed"));
        vm.BackupMonitorRows.Add(new(12, "Finish", "12:01:00.0000000", "—", "—", "B signal missing"));
        var focus = new TextBox { Text = "Operator input", Width = 160 };
        var view = new TimingView { DataContext = vm };
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        content.Children.Add(focus); content.Children.Add(view); Grid.SetRow(view, 1);
        window.Content = content; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var show = view.FindControl<Button>("ShowBackupButton")!;
            var hide = view.FindControl<Button>("HideBackupButton")!;
            var panel = view.FindControl<Border>("BackupTimesPanel")!;
            var grid = view.FindControl<DataGrid>("BackupTimesGrid")!;
            var warning = view.FindControl<TextBlock>("BackupWarningText")!;
            Assert.Same(vm.ShowBackupCommand, show.Command);
            Assert.Contains("backupWarning", show.Classes);
            Assert.Equal(Color.Parse("#F3C969"), Assert.IsAssignableFrom<ISolidColorBrush>(show.Background).Color);
            Assert.True(grid.IsReadOnly); Assert.False(show.Focusable); Assert.False(hide.Focusable);
            Assert.True(panel.IsVisible); Assert.True(warning.IsVisible);
            Assert.True(panel.Bounds.Width <= width); Assert.True(panel.Bounds.Height < 220);
            var startHeight = view.FindControl<DataGrid>("AtStartGrid")!.Bounds.Height;
            focus.Focus();
            var point = hide.TranslatePoint(new Point(hide.Bounds.Width / 2, hide.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.False(panel.IsVisible); Assert.True(warning.IsVisible);
            window.UpdateLayout();
            Assert.Equal(startHeight, view.FindControl<DataGrid>("AtStartGrid")!.Bounds.Height, 3);
            Assert.True(focus.IsFocused);
            Assert.Equal("Bib 12 finish: B signal missing", warning.Text);
        }
        finally { window.Close(); }
    }
}

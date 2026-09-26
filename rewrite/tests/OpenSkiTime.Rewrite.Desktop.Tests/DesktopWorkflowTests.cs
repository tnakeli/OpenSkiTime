using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Desktop;
using OpenSkiTime.Rewrite.Persistence;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(OpenSkiTime.Rewrite.Tests.HeadlessAppBuilder))]

namespace OpenSkiTime.Rewrite.Tests;

public sealed class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task DesktopCreatesEditsBacksUpAndReopensSeries()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-m1-ui", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "series.ost");
            var backup = Path.Combine(root, "transfer.ost");
            var dialogs = new FileDialogsStub { NewPath = file, OpenPath = backup, BackupPath = backup };
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var vm = new MainViewModel(workspace, dialogs);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            vm.Name = "Levi Weekend";
            vm.Location = "Levi";
            vm.Organizer = "Test Club";
            vm.Season = "2025/26";
            Click(window, "Create series file");
            await vm.CreateSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.True(File.Exists(file));
            Assert.True(vm.IsOpen);

            Click(window, "Add competition");
            vm.CompetitionName = "Slalom";
            vm.CompetitionShortLabel = "3.1 SL";
            Click(window, "Save competition");
            await vm.SaveCompetitionCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Single(vm.Competitions);

            Click(window, "Backup / transfer");
            await vm.BackupCommand.ExecutionTask!;
            Assert.True(File.Exists(backup));
            Click(window, "Close file");
            await vm.CloseSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsOpen);
            Click(window, "Open file");
            await vm.OpenSeriesCommand.ExecutionTask!;
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Single(vm.Competitions);
            Assert.Equal(backup, vm.FileLabel);
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Click(Window window, string label)
    {
        window.UpdateLayout();
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, label) && b.IsVisible);
        Assert.True(button.IsEnabled, $"{label} was disabled");
        button.Command?.Execute(button.CommandParameter);
    }

    private sealed class FileDialogsStub : IFileDialogs
    {
        public required string NewPath { get; init; }
        public required string OpenPath { get; init; }
        public required string BackupPath { get; init; }
        public Task<string?> ChooseNewAsync(string suggestedName) => Task.FromResult<string?>(NewPath);
        public Task<string?> ChooseOpenAsync() => Task.FromResult<string?>(OpenPath);
        public Task<string?> ChooseBackupAsync(string suggestedName) => Task.FromResult<string?>(BackupPath);
        public Task<bool> ConfirmRemoveAsync(string competitionName) => Task.FromResult(true);
    }
}

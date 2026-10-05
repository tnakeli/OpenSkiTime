using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Desktop;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task SettingsRowsShowTheLatestSignalPerChannelOfTheirDeviceWhileConnected()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-signal-monitor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (workspace, vm) = await CreateTimingRunAsync(root);
            await using var ownedWorkspace = workspace;
            using var ownedVm = vm;
            UseSimulatorTiming(vm);
            Assert.All(vm.PrimaryTimingRoles, x => Assert.Equal("", x.SignalText));
            await vm.ConnectTimingCommand.ExecuteAsync(null);
            Assert.True(vm.IsTimingConnected, vm.StatusMessage);
            await Task.Delay(250); vm.RefreshTiming();
            Assert.All(vm.PrimaryTimingRoles, x => Assert.Equal("No signals yet", x.SignalText));

            await PulseStartAsync(vm, workspace, vm.TimingRows[0].Bib, "12:00:01.2345");
            vm.SimulationTime = "12:00:07.5";
            await vm.SimulatePulseCommand.ExecuteAsync("finish");
            await WaitTimingAsync(vm, () => workspace.Timing!.RecentSignals.Count == 2);
            await Task.Delay(250); vm.RefreshTiming();
            // Same simulator device: both rows list C0 and C1; each marks its own channel.
            Assert.Equal("●C0 12:00:01.23 · C1 12:00:07.50", vm.TimingStartRole.SignalText);
            Assert.Equal("C0 12:00:01.23 · ●C1 12:00:07.50", vm.TimingFinishRole.SignalText);

            vm.ShowSettingsCommand.Execute(null);
            var window = new Window { Content = new ScrollViewer { Content = new SettingsView { DataContext = vm } }, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                SelectTimingSettingsTab(window);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), x => x.Name == "RoleSignalText" && x.IsEffectivelyVisible && x.Text == "●C0 12:00:01.23 · C1 12:00:07.50");
                CaptureSettingsWindow(window, 1280, 800, "timing-roles-signals");
            }
            finally { window.Close(); }

            await vm.DisconnectTimingCommand.ExecuteAsync(null);
            await Task.Delay(250); vm.RefreshTiming();
            Assert.All(vm.PrimaryTimingRoles, x => Assert.Equal("", x.SignalText));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

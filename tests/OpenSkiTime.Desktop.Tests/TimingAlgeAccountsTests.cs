using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Devices;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    [AvaloniaFact]
    public async Task TimyStartFinishWithIntermediatesOnTwoClubAlgeAccountsUsesPerAccountDeviceLists()
    {
        var root = Path.Combine(Path.GetTempPath(), "openskitime-alge-accounts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
            var preferences = new TimingPreferencesStore(root);
            using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = "", OpenPath = "", BackupPath = "" },
                fisStore: new FisLocalStore(root, new ReportSettingsCredential()), recentSeriesStore: new(root), timingPreferencesStore: preferences);
            vm.TimingStartRole.Source = TimingSourceTypes.TimyUsbLabel;
            vm.TimingStartRole.UsbId = "1";
            vm.AddTimingIntermediateCommand.Execute(null); vm.AddTimingIntermediateCommand.Execute(null);
            foreach (var (row, user, channel) in new[] { (vm.TimingIntermediateRoles[0], "club-a@example.test", 0), (vm.TimingIntermediateRoles[1], "club-b@example.test", 1) })
            {
                row.UsesLeaderConnection = false;
                row.Source = TimingSourceTypes.AlgeResultsLabel;
                row.AlgeUsername = user;
                row.Channel = channel;
            }
            Assert.Equal(["club-a@example.test", "club-b@example.test"], vm.AlgeAccounts.Select(x => x.Username));
            Assert.All(vm.TimingIntermediateRoles, x => Assert.True(x.ShowAlgeDeviceText));

            // Devices fetched for one account appear only in that account's role rows; choosing one fills the ID.
            vm.AlgeAccounts[0].Devices.Add(new("231203037", "Club A split", "MT1"));
            vm.TimingIntermediateRoles[0].Channel = 0; // any role edit refreshes the shared lists
            vm.TimingIntermediateRoles[1].AlgeDeviceId = "231203016";
            Assert.True(vm.TimingIntermediateRoles[0].ShowAlgeDeviceList);
            Assert.True(vm.TimingIntermediateRoles[1].ShowAlgeDeviceText);
            vm.TimingIntermediateRoles[0].SelectedAlgeDevice = vm.TimingIntermediateRoles[0].AlgeDevices.Single();
            Assert.Equal("231203037", vm.TimingIntermediateRoles[0].AlgeDeviceId);

            await vm.SaveTimingPreferencesCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            var saved = preferences.Load()!;
            Assert.Equal(("club-a@example.test", "231203037"), (saved.Intermediates[0].Connection.AlgeUsername, saved.Intermediates[0].Connection.AlgeDeviceId));
            Assert.Equal(("club-b@example.test", "231203016"), (saved.Intermediates[1].Connection.AlgeUsername, saved.Intermediates[1].Connection.AlgeDeviceId));
            Assert.Equal(3, saved.PrimaryCapture(new DateOnly(2026, 10, 4)).Count);
            Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(root, "timing-roles.json")), StringComparison.Ordinal);

            vm.ShowSettingsCommand.Execute(null);
            var window = new Window { Content = new ScrollViewer { Content = new SettingsView { DataContext = vm } }, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                SelectTimingSettingsTab(window);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                Assert.Equal(2, window.GetVisualDescendants().OfType<Button>().Count(x => Equals(x.Content, "Fetch devices") && x.IsEffectivelyVisible));
                Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), x => x.IsEffectivelyVisible && x.SelectedItem is AlgeResultsDevice { Id: "231203037" });
                CaptureSettingsWindow(window, 1280, 900, "timing-roles-alge-accounts");
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

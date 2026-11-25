using System.Runtime.CompilerServices;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;

namespace OpenSkiTime.Tests;

// Opening Timing or Timing devices opens the configured devices. Tests must not receive from hardware attached to the
// machine, or sign in to ALGE Results, so device sources are replaced by one that stays silent.
internal static class NoDeviceInTests
{
    [ModuleInitializer]
    internal static void Initialize() => TimingSourceFactory.TestSource = Silent;

    internal static ITimingSource? Silent(TimingConnection connection)
        => connection.Source is TimingSourceType.TimyUsb or TimingSourceType.Mt1Serial ? new SilentSource() : null;

    internal sealed class SilentSource : ITimingSource
    {
        public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
        {
            status("Connected · test device");
            await Task.Delay(Timeout.Infinite, ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

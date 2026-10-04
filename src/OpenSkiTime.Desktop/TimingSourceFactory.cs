using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

// One place that turns a configured connection into an existing device integration.
// Used by primary capture, live B Clock and operator-triggered device reads; no source has a parallel implementation.
internal static class TimingSourceFactory
{
    // algePassword returns the password for an ALGE Results username (typed or remembered); it is only called for ALGE Results.
    public static ITimingSource Create(TimingConnection connection, CaptureOptions options, HttpClient http,
        Func<string, string> algePassword, Func<SimulatorTimingSource>? simulator = null)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(algePassword);
        switch (connection.Source)
        {
            case TimingSourceType.TimyUsb: return new TimyUsbSource(connection.UsbId.Trim());
            case TimingSourceType.Mt1Serial:
                if (connection.Port.Trim().Length == 0) { throw new DomainValidationException("Choose the MT1 COM port."); }
                return new SerialTimingSource(connection.Port.Trim(), connection.BaudRate);
            case TimingSourceType.AlgeResults:
                var username = connection.AlgeUsername.Trim();
                if (username.Length == 0) { throw new DomainValidationException("Enter the ALGE Results username for every ALGE Results timing role."); }
                var password = algePassword(username);
                if (password.Length == 0) { throw new DomainValidationException($"Enter the ALGE Results password for {username}."); }
                return new AlgeResultsSource(http, username, password, options);
            case TimingSourceType.Simulator:
                return simulator?.Invoke() ?? throw new DomainValidationException("The simulator is available for primary training capture only.");
            case TimingSourceType.ReplayFile:
                if (!File.Exists(connection.ReplayPath)) { throw new DomainValidationException("Enter the path to an existing raw ALGE ASCII file."); }
                return new ReplayFileTimingSource(connection.ReplayPath);
            default: throw new DomainValidationException("Unsupported timing source.");
        }
    }
}

using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

// One place that turns a configured connection into an existing device integration.
// Used by primary capture, live B Clock and operator-triggered device reads; no source has a parallel implementation.
internal static class TimingSourceFactory
{
    public static ITimingSource Create(TimingConnection connection, CaptureOptions options, HttpClient http,
        string algePassword, bool rememberAlgePassword, Func<SimulatorTimingSource>? simulator = null)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(http);
        switch (connection.Source)
        {
            case TimingSourceType.TimyUsb: return new TimyUsbSource(connection.UsbId.Trim());
            case TimingSourceType.Mt1Serial:
                if (connection.Port.Trim().Length == 0) { throw new DomainValidationException("Choose the MT1 COM port."); }
                return new SerialTimingSource(connection.Port.Trim(), connection.BaudRate);
            case TimingSourceType.AlgeResults:
                var username = connection.AlgeUsername.Trim();
                var credential = new WindowsCredentialStore("OpenSkiTime.ALGE.Results.Password:" + username);
                var password = algePassword;
                if (password.Length == 0 && OperatingSystem.IsWindows()) { password = credential.Read() ?? ""; }
                if (username.Length == 0 || password.Length == 0) { throw new DomainValidationException("Enter the ALGE Results username and password."); }
                if (OperatingSystem.IsWindows()) { if (rememberAlgePassword) { credential.Save(password); } else { credential.Remove(); } }
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

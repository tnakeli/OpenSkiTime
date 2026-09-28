using System.IO.Ports;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Devices;

public sealed class SerialTimingSource(string portName, int baudRate = 38400) : ITimingSource
{
    public static string[] PortNames() => SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receive);
        ArgumentNullException.ThrowIfNull(status);
        var connection = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                { Handshake = Handshake.None, ReadTimeout = 200, WriteTimeout = 1000, DtrEnable = true, RtsEnable = false };
                port.Open();
                status($"Connected · {portName} · {baudRate} 8N1");
                var stream = (++connection).ToString(CultureInfo.InvariantCulture);
                var buffer = new byte[4096];
                while (!ct.IsCancellationRequested)
                {
                    int length;
                    try { length = port.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    if (length > 0) { await receive(new("alge-ascii/v1", portName, stream, buffer.AsSpan(0, length).ToArray())); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status("Disconnected · retrying serial connection");
                await receive(new("transport-status", portName, "disconnect", Encoding.UTF8.GetBytes("Serial connection lost/unavailable. Check cable and port; recover missed impulses from the device.")));
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SimulatorTimingSource : ITimingSource
{
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(128);
    private int _sequence;
    public ValueTask PulseAsync(int channel, long ticks, int? explicitBib = null)
    {
        var number = explicitBib ?? Interlocked.Increment(ref _sequence);
        var time = new TimeOnly(ticks).ToString(TimingTime.TimeOfDayFormat, CultureInfo.InvariantCulture);
        var text = string.Create(CultureInfo.InvariantCulture,
            $" {number:0000}{(explicitBib is not null ? "*" : "")} C{channel} {time}\r");
        return _input.Writer.WriteAsync(Encoding.ASCII.GetBytes(text));
    }
    public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receive); ArgumentNullException.ThrowIfNull(status);
        status("SIMULATION · no physical device");
        using var stop = ct.Register(() => _input.Writer.TryComplete());
        await foreach (var bytes in _input.Reader.ReadAllAsync(CancellationToken.None)) { await receive(new("alge-ascii/v1", "Simulator", "1", bytes)); }
    }
    public ValueTask DisposeAsync() { _input.Writer.TryComplete(); return ValueTask.CompletedTask; }
}

public sealed class ReplayFileTimingSource(string path) : ITimingSource
{
    public async Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receive); ArgumentNullException.ThrowIfNull(status);
        status("REPLAY · test data");
        await using var file = File.OpenRead(path);
        var buffer = new byte[257]; // Deliberately arbitrary; framing must survive packet boundaries.
        int read;
        while ((read = await file.ReadAsync(buffer, ct)) > 0)
        { await receive(new("alge-ascii/v1", "Replay", "1", buffer.AsSpan(0, read).ToArray())); }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

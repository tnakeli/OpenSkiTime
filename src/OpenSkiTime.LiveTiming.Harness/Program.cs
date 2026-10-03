using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Client;
using OpenSkiTime.LiveTiming.Harness;
using OpenSkiTime.LiveTiming.Publishing;

var mode = args.Length > 0 ? args[0] : "help";
if (mode == "help")
{
    Console.WriteLine("Modes: local|cloud <worker.dll> <endpoint> [server.dll]; fis-tcp|fis-https <endpoint> [count]. FIS password: OST_FIS_PASSWORD environment variable, never command line. Codex: OST_FIS_CODEX (default 9754). Press Ctrl+C to stop simulation.");
    return 0;
}
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var initial = SyntheticRace.Create(mode.StartsWith("fis-", StringComparison.Ordinal) && args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 50,
    Environment.GetEnvironmentVariable("OST_FIS_CODEX") ?? "9754");
try
{
    if (mode is "fis-tcp" or "fis-https")
    {
        var password = Environment.GetEnvironmentVariable("OST_FIS_PASSWORD") ?? throw new InvalidOperationException("Set OST_FIS_PASSWORD outside the repository.");
        IFisLiveTimingTransport transport = mode == "fis-tcp" ? new FisTcpTransport(args[1], 1550, Console.WriteLine) : new FisHttpsTransport(args[1], Console.WriteLine);
        await using var publisher = new FisPublisher(transport, password);
        await publisher.PublishAsync(initial, true, stop.Token);
        foreach (var state in SyntheticRace.Simulate(initial)) { await publisher.PublishAsync(state, false, stop.Token); await Task.Delay(100, stop.Token); }
        Console.WriteLine("FIS synthetic race acknowledged successfully.");
    }
    else
    {
        await using var publisher = new PublisherProcess(); publisher.Offer(initial);
        var options = new PublisherOptions(mode == "local" ? PublisherKind.Local : PublisherKind.Cloud, args[2], LocalServerAssembly: args.Length > 3 ? args[3] : null);
        await publisher.StartAsync(args[1], options);
        foreach (var state in SyntheticRace.Simulate(initial))
        {
            publisher.Offer(state); await Task.Delay(500, stop.Token);
            Console.WriteLine($"{publisher.Health.State} {publisher.Health.PublicUrl} {publisher.Health.Error}");
        }
        Console.WriteLine("Simulation complete. Keeping public view available until Ctrl+C.");
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
return 0;

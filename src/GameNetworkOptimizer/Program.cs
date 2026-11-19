using System.Net.NetworkInformation;
using GameNetworkOptimizer.Presentation;
using GameNetworkOptimizer.Services;

if (args.Contains("--help"))
{
    Console.WriteLine("Options: --public-ip 1.1.1.1 --interval-ms 1000 --timeout-ms 2000 --spike-ms 100 --data-dir data");
    return 0;
}
using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; shutdown.Cancel(); };
Console.CancelKeyPress += cancelHandler;
try
{
    var options = MonitorOptions.Parse(args);
    var gateway = NetworkMonitor.DetectGateway(options.PublicAddress);
    if (gateway is null)
        Console.Error.WriteLine("No IPv4 default gateway detected; monitoring the public target only.");
    var targets = new[] { gateway, options.PublicAddress }.OfType<string>().Distinct().ToArray();
    await using var logger = new CsvLogger(options.DataDirectory);
    Console.Error.WriteLine($"CSV log: {Path.GetFullPath(logger.FilePath)}");
    var dashboard = new ConsoleDashboard(targets, options.SpikeMs);
    dashboard.Render();
    try
    {
        await foreach (var measurement in new NetworkMonitor().RunAsync(targets, options, shutdown.Token))
        {
            // Persist completed measurements even when shutdown is requested.
            await logger.WriteAsync(measurement);
            dashboard.Add(measurement);
            dashboard.Render();
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    Console.WriteLine("Monitoring stopped. CSV saved.");
    return 0;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException
    or IOException or UnauthorizedAccessException or NetworkInformationException)
{
    Console.Error.WriteLine($"Monitoring stopped: {ex.Message}. Previously flushed CSV rows remain in the log.");
    return 1;
}
finally { Console.CancelKeyPress -= cancelHandler; }
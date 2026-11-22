using System.Net;
using DualRoute.Client.Windows;

// Integration-only crash fixture; never shipped as a client CLI.
if (Environment.GetEnvironmentVariable("DUALROUTE_TEST_WINTUN") != "1" || args.Length != 1)
    throw new InvalidOperationException("Integration fixture requires DUALROUTE_TEST_WINTUN=1 and an isolated journal directory.");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await using var device = await WintunDevice.CreateCoreAsync(IPAddress.Parse("10.77.0.2"), new WintunNative(),
    new WindowsAdapterConfiguration(), Path.GetFullPath(args[0]), stop.Token);
Console.WriteLine("READY " + device.Name);
await Console.Out.FlushAsync(stop.Token);
try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); }
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

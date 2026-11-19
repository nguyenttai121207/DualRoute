using System.Net;

namespace GameNetworkOptimizer.Services;

/// <summary>Validated command-line monitoring settings.</summary>
public sealed record MonitorOptions(string PublicAddress, int IntervalMs, int TimeoutMs, long SpikeMs, string DataDirectory)
{
    /// <summary>Parses pairs of named options; IP literals only, with a minimum interval of 100 ms.</summary>
    public static MonitorOptions Parse(string[] args)
    {
        var address = "1.1.1.1";
        var interval = 1000;
        var timeout = 2000;
        long spike = 100;
        var directory = "data";
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length) throw new ArgumentException("Every option requires a value.");
            switch (args[i])
            {
                case "--public-ip": address = args[i + 1]; break;
                case "--interval-ms": interval = int.Parse(args[i + 1]); break;
                case "--timeout-ms": timeout = int.Parse(args[i + 1]); break;
                case "--spike-ms": spike = long.Parse(args[i + 1]); break;
                case "--data-dir": directory = args[i + 1]; break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (!IPAddress.TryParse(address, out var ip) || IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            throw new ArgumentException("--public-ip must be a non-loopback IP address.");
        if (interval < 100 || timeout < 1 || timeout > 60000 || spike < 0 || string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Interval must be >= 100 ms, timeout 1–60000 ms, spike >= 0, and data directory nonempty.");
        return new(address, interval, timeout, spike, directory);
    }
}
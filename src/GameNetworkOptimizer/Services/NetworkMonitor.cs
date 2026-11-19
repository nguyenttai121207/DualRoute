using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameNetworkOptimizer.Models;

namespace GameNetworkOptimizer.Services;

/// <summary>Discovers a gateway and produces rate-limited ICMP measurements.</summary>
public sealed class NetworkMonitor
{
    /// <summary>Uses Windows' selected IPv4 interface for the public target to find its gateway.</summary>
    public static string? DetectGateway(string publicAddress)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // UDP Connect selects a route without sending a datagram.
            var destination = IPAddress.Parse(publicAddress);
            socket.Connect(destination.AddressFamily == AddressFamily.InterNetwork ? destination : IPAddress.Parse("1.1.1.1"), 53);
            var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.GetIPProperties())
                .Where(p => p.UnicastAddresses.Any(a => a.Address.Equals(local)))
                .SelectMany(p => p.GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
                ?.ToString();
        }
        catch (SocketException) { return null; }
        catch (NetworkInformationException) { return null; }
    }

    /// <summary>Runs one request per target per cycle, with a full interval between cycles.</summary>
    public async IAsyncEnumerable<NetworkMeasurement> RunAsync(
        IReadOnlyList<string> targets, MonitorOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        long sequence = 0;
        while (true)
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IPStatus status;
                long? latency = null;
                try
                {
                    var reply = await ping.SendPingAsync(IPAddress.Parse(target),
                        TimeSpan.FromMilliseconds(options.TimeoutMs), cancellationToken: cancellationToken);
                    status = reply.Status;
                    if (status == IPStatus.Success) latency = reply.RoundtripTime;
                }
                catch (PingException) { status = IPStatus.Unknown; }
                catch (SocketException) { status = IPStatus.Unknown; }
                yield return new(DateTimeOffset.UtcNow, ++sequence, target, status, latency,
                    latency.HasValue && latency.Value > options.SpikeMs);
            }
            await Task.Delay(options.IntervalMs, cancellationToken);
        }
    }
}
using System.Net;
using DualRoute.Tunnel;

namespace DualRoute.Client.Windows;

/// <summary>Owns neither device nor client; the caller must dispose both in finally.</summary>
public sealed class ClientTunForwarder(TunnelClient client, ITunDevice tun)
{
    private long dropped;
    public long DroppedPackets => Interlocked.Read(ref dropped);
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (tun.Mtu != Protocol.MaxPayloadSize) throw new ArgumentException("Wintun MTU must equal Protocol.MaxPayloadSize.");
        var ip = client.VirtualIp ?? throw new InvalidOperationException("Connect before starting the client forwarder.");
        WintunDevice.ValidateIp(ip);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new[] { FromDeviceAsync(ip, stop.Token), ToDeviceAsync(ip, stop.Token), MonitorAsync(ip, stop.Token) };
        try { await Task.WhenAny(tasks); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (tasks.All(t => t.IsCanceled || t.IsCompletedSuccessfully)) { }
        }
    }
    private async Task MonitorAsync(IPAddress ip, CancellationToken ct)
    {
        while (true)
        {
            if (client.State == ClientState.Failed || !ip.Equals(client.VirtualIp))
                throw new IOException("Tunnel failed or client IP changed; stop and recreate the adapter.");
            await Task.Delay(100, ct);
        }
    }
    private async Task FromDeviceAsync(IPAddress ip, CancellationToken ct)
    {
        while (true)
        {
            var bytes = await tun.ReadAsync(ct);
            if (client.State == ClientState.Failed || !ip.Equals(client.VirtualIp))
                throw new IOException("Tunnel failed or client IP changed; stop and recreate the adapter.");
            if (client.State != ClientState.Connected || !Ipv4Packet.TryParse(bytes, out var source, out _) || !ip.Equals(source))
            { Interlocked.Increment(ref dropped); continue; }
            await client.SendDataAsync(bytes, ct);
        }
    }
    private async Task ToDeviceAsync(IPAddress ip, CancellationToken ct)
    {
        await foreach (var message in client.ReceivedData.ReadAllAsync(ct))
        {
            if (!ip.Equals(client.VirtualIp)) throw new IOException("Client IP changed; recreate the adapter.");
            if (!Ipv4Packet.TryParse(message.Payload, out _, out var destination) || !ip.Equals(destination))
            { Interlocked.Increment(ref dropped); continue; }
            await tun.WriteAsync(message.Payload, ct);
        }
    }
}

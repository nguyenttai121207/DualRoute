namespace DualRoute.Tunnel;

/// <summary>Platform-independent bridge. The server run loop owns transport/device lifetime.</summary>
public sealed class TunForwarder(TunnelServer server, ITunDevice tun, IDatagramTransport transport)
{
    private long dropped;
    public long DroppedPackets => Interlocked.Read(ref dropped);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (tun.Mtu != Protocol.MaxPayloadSize) throw new ArgumentException("TUN MTU must equal Protocol.MaxPayloadSize.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new[] { ToTunAsync(stop.Token), FromTunAsync(stop.Token) };
        try { await Task.WhenAny(tasks); }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested && tasks.All(t => t.IsCanceled || t.IsCompletedSuccessfully)) { }
        }
    }

    private async Task ToTunAsync(CancellationToken ct)
    {
        await foreach (var message in server.ReceivedData.ReadAllAsync(ct))
        {
            if (!Ipv4Packet.TryParse(message.Payload, out var source, out _) || !server.CanForwardToTun(message, source!))
            { Interlocked.Increment(ref dropped); continue; }
            await tun.WriteAsync(message.Payload, ct);
        }
    }

    private async Task FromTunAsync(CancellationToken ct)
    {
        while (true)
        {
            var packet = await tun.ReadAsync(ct);
            if (!Ipv4Packet.TryParse(packet, out _, out var destination))
            { Interlocked.Increment(ref dropped); continue; }
            var datagram = server.PrepareData(destination!, packet);
            if (datagram is null) { Interlocked.Increment(ref dropped); continue; }
            try { await transport.SendAsync(datagram.Bytes, datagram.Remote, ct); }
            catch (System.Net.Sockets.SocketException) { Interlocked.Increment(ref dropped); }
        }
    }
}

using System.Net;
using System.Net.Sockets;

namespace DualRoute.Tunnel;

public sealed record Datagram(byte[] Bytes, IPEndPoint Remote);
public interface IDatagramTransport : IAsyncDisposable
{
    IPEndPoint LocalEndpoint { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> bytes, IPEndPoint remote, CancellationToken cancellationToken);
    ValueTask<Datagram> ReceiveAsync(CancellationToken cancellationToken);
}

public sealed class UdpDatagramTransport : IDatagramTransport
{
    private readonly Socket socket;
    public IPEndPoint LocalEndpoint => (IPEndPoint)socket.LocalEndPoint!;
    public UdpDatagramTransport(IPEndPoint endpoint)
    {
        socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try { socket.Bind(endpoint); }
        catch { socket.Dispose(); throw; }
    }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, IPEndPoint remote, CancellationToken cancellationToken)
    {
        if (bytes.Length > Protocol.MaxDatagramSize) throw new ArgumentException("Datagram exceeds maximum size.");
        await socket.SendToAsync(bytes, SocketFlags.None, remote, cancellationToken);
    }
    public async ValueTask<Datagram> ReceiveAsync(CancellationToken cancellationToken)
    {
        // One extra byte allows oversize detection without unbounded allocation.
        var buffer = new byte[Protocol.MaxDatagramSize + 1];
        EndPoint any = socket.AddressFamily == AddressFamily.InterNetwork ?
            new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
        try
        {
            var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken);
            return new(buffer.AsSpan(0, result.ReceivedBytes).ToArray(), (IPEndPoint)result.RemoteEndPoint);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
        {
            return new(buffer, (IPEndPoint)any); // Oversize marker, rejected before parsing.
        }
    }
    public ValueTask DisposeAsync() { socket.Dispose(); return ValueTask.CompletedTask; }
}
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace DualRoute.Tunnel;

public enum ClientState { Connecting, Connected, Reconnecting, Failed }

/// <summary>One receive owner. Public probe/send calls are serialized; keepalives reconnect automatically.</summary>
public sealed class TunnelClient : IAsyncDisposable
{
    private readonly IDatagramTransport transport;
    private readonly IPEndPoint server;
    private readonly Guid id;
    private readonly byte[] psk;
    private readonly TunnelOptions options;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = true });
    private readonly Task receiver;
    private Task? heartbeat;
    private SecureSession? session;
    private int state = (int)ClientState.Connecting;
    private long probeId;
    private bool disposed;
    public ClientState State => (ClientState)Volatile.Read(ref state);
    private void SetState(ClientState value) => Volatile.Write(ref state, (int)value);
    public IPAddress? VirtualIp { get; private set; }

    public TunnelClient(IDatagramTransport transport, IPEndPoint server, Guid clientId, byte[] psk,
        TunnelOptions? options = null, TimeProvider? timeProvider = null)
    {
        if (clientId == Guid.Empty || psk.Length < 32) throw new ArgumentException("A client UUID and >=32-byte PSK are required.");
        this.options = options ?? new(); this.options.Validate();
        this.transport = transport; this.server = server; id = clientId; this.psk = psk.ToArray();
        time = timeProvider ?? TimeProvider.System;
        receiver = ReceiveAsync();
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (true)
            {
                try
                {
                    var datagram = await transport.ReceiveAsync(stop.Token);
                    if (datagram.Remote.Equals(server) && datagram.Bytes.Length <= Protocol.MaxDatagramSize)
                        incoming.Writer.TryWrite(datagram.Bytes);
                }
                catch (SocketException) { await Task.Delay(TimeSpan.FromMilliseconds(25), time, stop.Token); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { incoming.Writer.TryComplete(ex); SetState(ClientState.Failed); }
        finally { incoming.Writer.TryComplete(); }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        await operation.WaitAsync(linked.Token);
        try
        {
            await ConnectCoreAsync(linked.Token);
            if (heartbeat is null || heartbeat.IsCompleted) heartbeat = HeartbeatAsync();
        }
        catch { SetState(ClientState.Failed); throw; }
        finally { operation.Release(); }
    }
    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        session?.Dispose(); session = null;
        while (incoming.Reader.TryRead(out _)) { }
        using var handshake = new ClientHandshake(id, psk);
        using var deadline = new CancellationTokenSource(options.HandshakeTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await transport.SendAsync(handshake.CreateHello(), server, linked.Token);
        try
        {
            while (session is null)
            {
                var packet = await incoming.Reader.ReadAsync(linked.Token);
                session = handshake.AcceptAck(packet, out var ip);
                if (session is not null) VirtualIp = ip;
            }
            await ExchangeAsync(PacketType.Keepalive, [], linked.Token);
            SetState(ClientState.Connected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Tunnel handshake timed out."); }
    }

    private async Task<byte[]> ExchangeAsync(PacketType type, byte[] payload, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(options.ResponseTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await transport.SendAsync(session!.Encrypt(type, payload), server, linked.Token);
        try
        {
            while (true)
            {
                var packet = await incoming.Reader.ReadAsync(linked.Token);
                if (!session.TryDecrypt(packet, out var header, out var body)) continue;
                if (type == PacketType.Keepalive && header.Type == type && body.Length == 0) return body;
                if (type == PacketType.Probe && header.Type == PacketType.ProbeReply && body.AsSpan().SequenceEqual(payload)) return body;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Tunnel response timed out."); }
    }

    public async Task<TimeSpan> ProbeAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        await operation.WaitAsync(linked.Token);
        try
        {
            if (State != ClientState.Connected) throw new InvalidOperationException("Tunnel is not connected.");
            var payload = new byte[16];
            BinaryPrimitives.WriteInt64BigEndian(payload, ++probeId);
            var start = Stopwatch.GetTimestamp();
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(8), start);
            await ExchangeAsync(PacketType.Probe, payload, linked.Token);
            return Stopwatch.GetElapsedTime(start);
        }
        finally { operation.Release(); }
    }

    public async Task SendDataAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stop.Token);
        await operation.WaitAsync(linked.Token);
        try
        {
            if (State != ClientState.Connected) throw new InvalidOperationException("Tunnel is not connected.");
            await transport.SendAsync(session!.Encrypt(PacketType.Data, payload.Span), server, linked.Token);
        }
        finally { operation.Release(); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(options.KeepaliveInterval, time, stop.Token);
                await operation.WaitAsync(stop.Token);
                try
                {
                    try { await ExchangeAsync(PacketType.Keepalive, [], stop.Token); }
                    catch (Exception ex) when (ex is TimeoutException or SocketException)
                    {
                        SetState(ClientState.Reconnecting);
                        try { await ConnectCoreAsync(stop.Token); }
                        catch (Exception retry) when (retry is TimeoutException or SocketException) { SetState(ClientState.Failed); return; }
                    }
                }
                finally { operation.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { SetState(ClientState.Failed); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        if (heartbeat is not null) await heartbeat;
        await operation.WaitAsync();
        try
        {
            if (session is not null)
            {
                using var deadline = new CancellationTokenSource(options.ResponseTimeout, time);
                try { await transport.SendAsync(session.Encrypt(PacketType.Disconnect, []), server, deadline.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or InvalidOperationException) { }
                session.Dispose();
            }
            await receiver;
            await transport.DisposeAsync();
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(psk);
            SetState(ClientState.Failed);
        }
        finally { operation.Release(); stop.Dispose(); }
    }
}

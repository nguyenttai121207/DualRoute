using System.Net;
using System.Threading.Channels;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DualRoute.Tunnel;

/// <summary>Single-owner server engine; synchronized processing also supports deterministic tests.</summary>
public sealed class TunnelServer : IDisposable
{
    private sealed record Entry(SecureSession Crypto, IPEndPoint Remote, long Created)
    {
        public long LastSeen { get; set; } = Created;
    }
    private readonly object gate = new();
    private readonly Dictionary<Guid, (byte[] Psk, IPAddress Ip)> clients;
    private readonly Dictionary<ulong, Entry> pending = [];
    private readonly Dictionary<Guid, Entry> active = [];
    private readonly TunnelOptions options;
    private readonly TimeProvider time;
    private readonly Channel<ReceivedData> data = Channel.CreateBounded<ReceivedData>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
    public ChannelReader<ReceivedData> ReceivedData => data.Reader;
    private long dropped;
    private bool disposed;
    public long DroppedPackets => Interlocked.Read(ref dropped);
    public int SessionCount { get { lock (gate) return active.Count; } }
    public int PendingCount { get { lock (gate) return pending.Count; } }

    public TunnelServer(ServerConfiguration configuration, TunnelOptions? options = null, TimeProvider? timeProvider = null)
    {
        configuration.Validate();
        this.options = options ?? new();
        this.options.Validate();
        time = timeProvider ?? TimeProvider.System;
        clients = configuration.Clients.ToDictionary(c => c.ClientId,
            c => (Convert.FromBase64String(c.PskBase64), IPAddress.Parse(c.VirtualIp)));
    }

    public void Sweep()
    {
        lock (gate)
        {
            foreach (var key in pending.Where(p => time.GetElapsedTime(p.Value.Created) >= options.HandshakeTimeout).Select(p => p.Key).ToArray())
            { pending[key].Crypto.Dispose(); pending.Remove(key); }
            foreach (var key in active.Where(p => time.GetElapsedTime(p.Value.LastSeen) >= options.IdleTimeout).Select(p => p.Key).ToArray())
            { active[key].Crypto.Dispose(); active.Remove(key); }
        }
    }

    /// <summary>Returns an optional reply to the sender. DATA is authenticated but never forwarded.</summary>
    public byte[]? Process(Datagram datagram)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Sweep();
            var packet = datagram.Bytes;
            byte[]? Drop() { Interlocked.Increment(ref dropped); return null; }
            if (packet.Length < 2 || packet.Length > Protocol.MaxDatagramSize || packet[0] != Protocol.Version) return Drop();
            if (packet[1] == (byte)PacketType.Hello)
            {
                if (packet.Length != Protocol.HelloSize) return Drop();
                var id = Protocol.ReadId(packet.AsSpan(2));
                if (!clients.TryGetValue(id, out var client) || !Protocol.Verify(packet, client.Psk)) return Drop();
                if (pending.Count >= options.MaxPendingHandshakes || (!active.ContainsKey(id) && active.Count >= options.MaxSessions)) return Drop();
                var nonce = packet.AsSpan(18, 16).ToArray();
                var serverNonce = RandomNumberGenerator.GetBytes(16);
                ulong sessionId;
                do { sessionId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8)); }
                while (sessionId == 0 || pending.ContainsKey(sessionId) || active.Values.Any(e => e.Crypto.SessionId == sessionId));
                var keys = Protocol.SessionKeys(client.Psk, nonce, serverNonce);
                try { pending.Add(sessionId, new(new(id, sessionId, keys.S2C, keys.C2S), datagram.Remote, time.GetTimestamp())); }
                finally { CryptographicOperations.ZeroMemory(keys.C2S); CryptographicOperations.ZeroMemory(keys.S2C); }
                return Protocol.Ack(id, nonce, sessionId, serverNonce, client.Ip, client.Psk);
            }
            if (!Protocol.TryReadHeader(packet, out var header)) return Drop();
            Entry? entry = null;
            var activating = pending.TryGetValue(header.SessionId, out var candidate) && candidate.Crypto.ClientId == header.ClientId;
            if (activating) entry = candidate;
            else if (active.TryGetValue(header.ClientId, out var existing) && existing.Crypto.SessionId == header.SessionId) entry = existing;
            if (entry is null || !entry.Remote.Equals(datagram.Remote) ||
                !entry.Crypto.TryDecrypt(packet, out header, out var payload)) return Drop();
            if ((header.Type == PacketType.Probe && payload.Length != 16) ||
                ((header.Type is PacketType.Keepalive or PacketType.Disconnect) && payload.Length != 0) ||
                header.Type == PacketType.ProbeReply) return Drop();
            if (activating)
            {
                if (!active.ContainsKey(header.ClientId) && active.Count >= options.MaxSessions) return Drop();
                if (active.Remove(header.ClientId, out var previous)) previous.Crypto.Dispose();
                active.Add(header.ClientId, entry);
                pending.Remove(header.SessionId);
                // Other incomplete handshakes cannot later replace this activation.
                foreach (var key in pending.Where(p => p.Value.Crypto.ClientId == header.ClientId).Select(p => p.Key).ToArray())
                { pending[key].Crypto.Dispose(); pending.Remove(key); }
            }
            entry.LastSeen = time.GetTimestamp();
            if (header.Type == PacketType.Data && !data.Writer.TryWrite(new(header.ClientId, header.SessionId, payload)))
                Interlocked.Increment(ref dropped);
            if (header.Type == PacketType.Disconnect)
            {
                active.Remove(header.ClientId);
                entry.Crypto.Dispose();
                return null;
            }
            return header.Type switch
            {
                PacketType.Keepalive => entry.Crypto.Encrypt(PacketType.Keepalive, []),
                PacketType.Probe => entry.Crypto.Encrypt(PacketType.ProbeReply, payload),
                _ => null
            };
        }
    }

    internal bool CanForwardToTun(ReceivedData message, IPAddress source)
    {
        lock (gate)
        {
            Sweep();
            return !disposed && active.TryGetValue(message.ClientId, out var entry) &&
                entry.Crypto.SessionId == message.SessionId && clients[message.ClientId].Ip.Equals(source);
        }
    }

    internal Datagram? PrepareData(IPAddress destination, byte[] payload)
    {
        lock (gate)
        {
            if (disposed || payload.Length > Protocol.MaxPayloadSize) return null;
            Sweep();
            foreach (var (id, entry) in active)
                if (clients[id].Ip.Equals(destination))
                    return new(entry.Crypto.Encrypt(PacketType.Data, payload), entry.Remote);
            return null;
        }
    }

    /// <summary>Owns the supplied transport; preserves the Phase 1 raw DATA interface.</summary>
    public Task RunAsync(IDatagramTransport transport, CancellationToken cancellationToken) =>
        RunCoreAsync(transport, null, cancellationToken);

    /// <summary>Owns transport and TUN; this forwarder is the sole ReceivedData consumer.</summary>
    public Task RunAsync(IDatagramTransport transport, ITunDevice tun, CancellationToken cancellationToken) =>
        RunCoreAsync(transport, tun, cancellationToken);

    private async Task RunCoreAsync(IDatagramTransport transport, ITunDevice? tun, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new List<Task>();
        try
        {
            if (tun is not null && tun.Mtu != Protocol.MaxPayloadSize)
                throw new ArgumentException("TUN MTU must equal Protocol.MaxPayloadSize.");
            tasks.Add(ReceiveLoopAsync(transport, stop.Token));
            tasks.Add(MaintainAsync(stop.Token));
            if (tun is not null) tasks.Add(new TunForwarder(this, tun, transport).RunAsync(stop.Token));
            await Task.WhenAny(tasks);
        }
        finally
        {
            stop.Cancel();
            try
            {
                try { await Task.WhenAll(tasks); }
                catch (OperationCanceledException) when (tasks.All(t => t.IsCanceled || t.IsCompletedSuccessfully)) { }
            }
            finally
            {
                Dispose();
                try { if (tun is not null) await tun.DisposeAsync(); }
                finally { await transport.DisposeAsync(); }
            }
        }
    }

    private async Task ReceiveLoopAsync(IDatagramTransport transport, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var datagram = await transport.ReceiveAsync(ct);
                var reply = Process(datagram);
                if (reply is not null) await transport.SendAsync(reply, datagram.Remote, ct);
            }
            catch (SocketException)
            {
                Interlocked.Increment(ref dropped);
                await Task.Delay(TimeSpan.FromMilliseconds(25), time, ct);
            }
        }
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
            Sweep();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in pending.Values.Concat(active.Values)) entry.Crypto.Dispose();
            pending.Clear(); active.Clear(); data.Writer.TryComplete();
            foreach (var client in clients.Values) CryptographicOperations.ZeroMemory(client.Psk);
            clients.Clear();
        }
    }
}
public sealed record ReceivedData(Guid ClientId, ulong SessionId, byte[] Payload);
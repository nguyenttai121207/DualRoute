using System.Net;
using System.Threading.Channels;
using DualRoute.Tunnel;
using Xunit;

namespace DualRoute.Tunnel.Tests;

internal sealed class ControlledTransport(IDatagramTransport inner) : IDatagramTransport
{
    public bool DropKeepalive { get; set; }
    private int droppedKeepalives;
    public int DroppedKeepalives => Volatile.Read(ref droppedKeepalives);
    public TaskCompletionSource KeepaliveSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ProbeSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool HoldProbeReply { get; set; }
    private Datagram? held;
    public IPEndPoint LocalEndpoint => inner.LocalEndpoint;
    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, IPEndPoint remote, CancellationToken ct)
    {
        if (Protocol.TryReadHeader(bytes.Span, out var header))
        {
            if (header.Type == PacketType.Keepalive)
            {
                if (DropKeepalive)
                {
                    Interlocked.Increment(ref droppedKeepalives);
                    KeepaliveSent.TrySetResult();
                    return;
                }
            }
            if (header.Type == PacketType.ProbeReply && HoldProbeReply)
            {
                held = new(bytes.ToArray(), remote);
                ProbeSent.TrySetResult();
                return;
            }
        }
        await inner.SendAsync(bytes, remote, ct);
    }
    public async Task ReleaseProbeAsync(CancellationToken ct)
    {
        var packet = held ?? throw new InvalidOperationException();
        HoldProbeReply = false;
        await inner.SendAsync(packet.Bytes, packet.Remote, ct);
    }
    public ValueTask<Datagram> ReceiveAsync(CancellationToken ct) => inner.ReceiveAsync(ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal sealed class DataFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
    public CancellationToken Token => stop.Token;
    public FakeNetwork Network { get; } = new();
    public ManualTime Clock { get; } = new();
    public FakeTunDevice Tun { get; }
    public FakeTransport RawServerTransport { get; }
    public ControlledTransport Transport { get; }
    public TunnelServer Server { get; }
    public TunnelClient A { get; }
    public TunnelClient B { get; }
    private readonly Task running;
    private bool disposed;
    public DataFixture(FakeTunDevice? tun = null)
    {
        Tun = tun ?? new();
        var a = ProtocolTests.Client(); var b = ProtocolTests.Client(3);
        RawServerTransport = Network.Bind(1235);
        Transport = new(RawServerTransport);
        Server = new(new([a, b]), timeProvider: Clock);
        running = Server.RunAsync(Transport, Tun, stop.Token);
        A = new(Network.Bind(1234), Transport.LocalEndpoint, a.ClientId, Convert.FromBase64String(a.PskBase64), timeProvider: Clock);
        B = new(Network.Bind(1236), Transport.LocalEndpoint, b.ClientId, Convert.FromBase64String(b.PskBase64), timeProvider: Clock);
    }
    public Task ConnectAsync() => Task.WhenAll(A.ConnectAsync(Token), B.ConnectAsync(Token)).WaitAsync(Token);
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await A.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await B.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            stop.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { Server.Dispose(); stop.Dispose(); }
        }
    }
}

public class TunnelDataTests
{
    [Fact]
    public async Task ConcurrentDataSendsHaveIndependentSequencesAndPreservePayloads()
    {
        await using var f = new DataFixture();
        await f.ConnectAsync();
        var packets = Enumerable.Range(0, 32).Select(i =>
        {
            var packet = TunForwarderTests.Packet(size: 21);
            packet[20] = (byte)i;
            return packet;
        }).ToArray();
        await Task.WhenAll(packets.Select(p => f.A.SendDataAsync(p, f.Token))).WaitAsync(f.Token);
        var received = new List<byte>();
        for (var i = 0; i < packets.Length; i++)
            received.Add((await f.Tun.ToKernel.Reader.ReadAsync(f.Token))[20]);
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), received.Order());
        Assert.Equal(0, f.Server.DroppedPackets);
        await f.A.ProbeAsync(f.Token);
    }

    [Fact]
    public async Task DataFlowsWhileProbeWaitsAndFullClientDataQueueDoesNotBlockControl()
    {
        await using var f = new DataFixture();
        await f.ConnectAsync();
        f.Transport.HoldProbeReply = true;
        var probe = f.A.ProbeAsync(f.Token);
        await f.Transport.ProbeSent.Task.WaitAsync(f.Token);
        Assert.False(probe.IsCompleted);
        var request = TunForwarderTests.Packet();
        await f.A.SendDataAsync(request, f.Token).WaitAsync(f.Token);
        Assert.Equal(request, await f.Tun.ToKernel.Reader.ReadAsync(f.Token));

        var reply = TunForwarderTests.Packet(source: "10.77.0.1", destination: "10.77.0.2");
        for (var i = 0; i < 80; i++)
        {
            // A B-bound packet/read is a bridge barrier, avoiding fake-network overflow.
            await f.Tun.FromKernel.Writer.WriteAsync(reply, f.Token);
            await f.Tun.FromKernel.Writer.WriteAsync(TunForwarderTests.Packet(destination: "10.77.0.3"), f.Token);
            await f.B.ReceivedData.ReadAsync(f.Token);
            await TunForwarderTests.Until(() => f.A.ReceivedData.Count + f.A.DroppedDataPackets == i + 1, f.Token);
        }
        await TunForwarderTests.Until(() => f.A.DroppedDataPackets == 16, f.Token);
        await f.Transport.ReleaseProbeAsync(f.Token);
        Assert.True(await probe.WaitAsync(f.Token) >= TimeSpan.Zero);
        Assert.Equal(ClientState.Connected, f.A.State);
        var count = 0;
        while (f.A.ReceivedData.TryRead(out _)) count++;
        Assert.Equal(64, count);
    }

    [Fact]
    public async Task DataSendDoesNotWaitForMissingKeepaliveResponse()
    {
        await using var f = new DataFixture();
        await f.ConnectAsync();
        // Both clients may send a heartbeat; dropping server echoes leaves control exchanges waiting.
        f.Transport.DropKeepalive = true;
        f.Clock.Advance(TimeSpan.FromSeconds(5));
        await f.Transport.KeepaliveSent.Task.WaitAsync(f.Token);
        // Counter barrier verifies heartbeat attempts have run after activation.
        await TunForwarderTests.Until(() => f.Transport.DroppedKeepalives == 2, f.Token);
        await f.A.SendDataAsync(TunForwarderTests.Packet(), f.Token).WaitAsync(f.Token);
        Assert.Equal(TunForwarderTests.Packet(), await f.Tun.ToKernel.Reader.ReadAsync(f.Token));
    }

    [Fact]
    public async Task OversizeClientDataIsDroppedWithoutThrowingOrSending()
    {
        await using var f = new DataFixture();
        await f.ConnectAsync();
        var before = f.Network.Sends;
        await f.A.SendDataAsync(new byte[Protocol.MaxPayloadSize + 1], f.Token);
        Assert.Equal(before, f.Network.Sends);
        Assert.Equal(1, f.A.DroppedDataPackets);
    }

    [Fact]
    public async Task DuplicateAndReorderedServerDataAreDeliveredOnce()
    {
        var config = ProtocolTests.Client();
        var network = new FakeNetwork();
        var remote = network.Bind(1235);
        using var server = new TunnelServer(new([config]));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = server.RunAsync(remote, stop.Token);
        await using var client = new TunnelClient(network.Bind(1234), remote.LocalEndpoint,
            config.ClientId, Convert.FromBase64String(config.PskBase64));
        try
        {
            await client.ConnectAsync(stop.Token);
            await using var tun = new FakeTunDevice();
            var bridge = new TunForwarder(server, tun, remote);
            using var bridgeStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var forwarding = bridge.RunAsync(bridgeStop.Token);
            try
            {
                network.Hold = true; network.Duplicate = true;
                var first = TunForwarderTests.Packet(destination: config.VirtualIp, size: 21);
                var second = TunForwarderTests.Packet(destination: config.VirtualIp, size: 22);
                await tun.FromKernel.Writer.WriteAsync(first, stop.Token);
                await tun.FromKernel.Writer.WriteAsync(second, stop.Token);
                await TunForwarderTests.Until(() => network.Sends == 6, stop.Token); // 4 handshake packets + 2 DATA
                network.Hold = false;
                network.FlushReversed();
                Assert.Equal(second, (await client.ReceivedData.ReadAsync(stop.Token)).Payload);
                Assert.Equal(first, (await client.ReceivedData.ReadAsync(stop.Token)).Payload);
                // Control reply is processed after queued duplicates.
                network.Duplicate = false;
                await client.ProbeAsync(stop.Token);
                Assert.False(client.ReceivedData.TryRead(out _));
            }
            finally { bridgeStop.Cancel(); await forwarding.WaitAsync(stop.Token); }
        }
        finally
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

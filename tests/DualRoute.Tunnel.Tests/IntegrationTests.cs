using System.Net;
using DualRoute.Tunnel;
using Xunit;

namespace DualRoute.Tunnel.Tests;

public class IntegrationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Limit);
        while (!condition()) { deadline.Token.ThrowIfCancellationRequested(); await Task.Delay(1, deadline.Token); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    [Trait("Category", "Integration")]
    public async Task TwoClientsProbeAndDisconnectIndependently(bool realUdp)
    {
        var a = ProtocolTests.Client(); var b = ProtocolTests.Client(3);
        var network = new FakeNetwork();
        IDatagramTransport Bind(int port) => realUdp
            ? new UdpDatagramTransport(new(IPAddress.Loopback, 0)) : network.Bind(port);
        var serverTransport = Bind(100);
        using var server = new TunnelServer(new([a,b]));
        using var stop = new CancellationTokenSource(Limit);
        var running = server.RunAsync(serverTransport, stop.Token);
        var clientA = new TunnelClient(Bind(101),serverTransport.LocalEndpoint,a.ClientId,Convert.FromBase64String(a.PskBase64));
        var clientB = new TunnelClient(Bind(102),serverTransport.LocalEndpoint,b.ClientId,Convert.FromBase64String(b.PskBase64));
        try
        {
            await Task.WhenAll(clientA.ConnectAsync(stop.Token),clientB.ConnectAsync(stop.Token)).WaitAsync(Limit);
            Assert.Equal(2,server.SessionCount);
            Assert.Equal(ClientState.Connected,clientA.State);
            var rtts = await Task.WhenAll(clientA.ProbeAsync(stop.Token),clientB.ProbeAsync(stop.Token)).WaitAsync(Limit);
            Assert.All(rtts,rtt => Assert.True(rtt >= TimeSpan.Zero && rtt < Limit));
            await clientA.SendDataAsync(new byte[] { 3,4 },stop.Token);
            var received = await server.ReceivedData.ReadAsync(stop.Token);
            Assert.Equal(a.ClientId,received.ClientId);
            Assert.Equal(new byte[] { 3,4 },received.Payload);
            await clientA.DisposeAsync().AsTask().WaitAsync(Limit);
            await Until(() => server.SessionCount == 1);
            Assert.True(await clientB.ProbeAsync(stop.Token) < Limit);
        }
        finally
        {
            await clientA.DisposeAsync().AsTask().WaitAsync(Limit);
            await clientB.DisposeAsync().AsTask().WaitAsync(Limit);
            stop.Cancel();
            await running.WaitAsync(Limit);
        }
        Assert.Equal(0,server.SessionCount);
        Assert.Equal(0,server.PendingCount);
    }

    [Fact]
    public async Task FakeTransportDropsDuplicatesAndReordersWithoutReplaySideEffects()
    {
        var config = ProtocolTests.Client();
        using var server = new TunnelServer(new([config]));
        using var session = ProtocolTests.Connect(server,config);
        var network = new FakeNetwork();
        await using var source = network.Bind(1234);
        await using var target = network.Bind(1235);
        using var stop = new CancellationTokenSource(Limit);
        network.Drop = true;
        await source.SendAsync(session.Encrypt(PacketType.Data,[0]),target.LocalEndpoint,stop.Token);
        Assert.False(target.Queue.Reader.TryRead(out _));
        network.Drop = false; network.Hold = true; network.Duplicate = true;
        await source.SendAsync(session.Encrypt(PacketType.Data,[1]),target.LocalEndpoint,stop.Token);
        await source.SendAsync(session.Encrypt(PacketType.Data,[2]),target.LocalEndpoint,stop.Token);
        network.FlushReversed();
        for (var i = 0; i < 4; i++) server.Process(await target.ReceiveAsync(stop.Token));
        var first = await server.ReceivedData.ReadAsync(stop.Token);
        var second = await server.ReceivedData.ReadAsync(stop.Token);
        Assert.Equal(new byte[] {2},first.Payload);
        Assert.Equal(new byte[] {1},second.Payload);
        Assert.False(server.ReceivedData.TryRead(out _));
        Assert.Equal(2,server.DroppedPackets);
    }

    [Fact]
    public async Task HandshakeTimeoutUsesTimeProviderAndShutdownCancelsWaits()
    {
        var config = ProtocolTests.Client(); var clock = new ManualTime();
        var network = new FakeNetwork { Drop = true };
        var transport = network.Bind(1234);
        await using var client = new TunnelClient(transport,new(IPAddress.Loopback,1235),
            config.ClientId,Convert.FromBase64String(config.PskBase64),timeProvider:clock);
        using var stop = new CancellationTokenSource(Limit);
        var connect = client.ConnectAsync(stop.Token);
        await Until(() => clock.TimerCount >= 1);
        clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TimeoutException>(() => connect.WaitAsync(Limit));
        Assert.Equal(ClientState.Failed,client.State);
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task KeepaliveLossReconnectsAndRecoversUsingInjectedTime()
    {
        var config = ProtocolTests.Client(); var clock = new ManualTime();
        var network = new FakeNetwork();
        var serverTransport = network.Bind(1235);
        using var server = new TunnelServer(new([config]),timeProvider:clock);
        using var stop = new CancellationTokenSource(Limit);
        var running = server.RunAsync(serverTransport,stop.Token);
        await using var client = new TunnelClient(network.Bind(1234),serverTransport.LocalEndpoint,
            config.ClientId,Convert.FromBase64String(config.PskBase64),timeProvider:clock);
        try
        {
            await client.ConnectAsync(stop.Token).WaitAsync(Limit);
            await Until(() => clock.TimerCount >= 2);
            network.Drop = true;
            clock.Advance(TimeSpan.FromSeconds(5));
            // Heartbeat replaces its delay timer with the response deadline.
            await Until(() => clock.TimerCount >= 2);
            // Ensure the async heartbeat continuation has actually sent a packet.
            await Until(() => network.Sends >= 4);
            network.Drop = false;
            clock.Advance(TimeSpan.FromSeconds(11));
            await Until(() => network.Sends >= 6 && client.State == ClientState.Connected);
            Assert.Equal(1,server.SessionCount);
            Assert.True(await client.ProbeAsync(stop.Token) < Limit);
        }
        finally
        {
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
            stop.Cancel(); await running.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task ExplicitReconnectAfterFailureRestartsHeartbeatAndPreventsIdleExpiry()
    {
        var config = ProtocolTests.Client();
        var clock = new ManualTime();
        var network = new FakeNetwork();
        var serverTransport = network.Bind(1235);
        var options = new TunnelOptions();
        using var server = new TunnelServer(new([config]), options, clock);
        using var stop = new CancellationTokenSource(Limit);
        var running = server.RunAsync(serverTransport, stop.Token);
        await using var client = new TunnelClient(network.Bind(1234), serverTransport.LocalEndpoint,
            config.ClientId, Convert.FromBase64String(config.PskBase64), options, clock);
        try
        {
            await client.ConnectAsync(stop.Token).WaitAsync(Limit);
            await Until(() => clock.TimerCount == 2);
            network.Drop = true;
            var sends = network.Sends;
            clock.Advance(options.KeepaliveInterval);
            await Until(() => network.Sends > sends && clock.TimerCount == 2);
            sends = network.Sends;
            clock.Advance(options.ResponseTimeout + TimeSpan.FromSeconds(1));
            await Until(() => client.State == ClientState.Reconnecting && network.Sends > sends);
            clock.Advance(options.HandshakeTimeout + TimeSpan.FromSeconds(1));
            await Until(() => client.State == ClientState.Failed && clock.TimerCount == 1);

            network.Drop = false;
            await client.ConnectAsync(stop.Token).WaitAsync(Limit);
            Assert.Equal(ClientState.Connected, client.State);
            var reconnectedAt = clock.GetTimestamp();

            // Advance in keepalive-sized steps so each response can complete before the next deadline.
            while (clock.GetElapsedTime(reconnectedAt) <= options.IdleTimeout)
            {
                sends = network.Sends;
                clock.Advance(options.KeepaliveInterval);
                await Until(() => network.Sends >= sends + 2 && clock.TimerCount == 2);
                server.Sweep();
                Assert.Equal(1, server.SessionCount);
                Assert.Equal(ClientState.Connected, client.State);
            }
        }
        finally
        {
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
            stop.Cancel();
            await running.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task CancellationInterruptsConnectAndServerReceive()
    {
        var config = ProtocolTests.Client(); var network = new FakeNetwork();
        var transport = network.Bind(10);
        await using var client = new TunnelClient(transport,new(IPAddress.Loopback,11),
            config.ClientId,Convert.FromBase64String(config.PskBase64));
        using var stop = new CancellationTokenSource();
        var connect = client.ConnectAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Limit));
        using var server = new TunnelServer(new([config]));
        var serverTransport = network.Bind(12);
        await server.RunAsync(serverTransport,stop.Token).WaitAsync(Limit);
        Assert.True(serverTransport.Disposed);
    }
}

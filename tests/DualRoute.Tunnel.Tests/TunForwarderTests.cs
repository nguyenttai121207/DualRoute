using System.Buffers.Binary;
using System.Net;
using DualRoute.Tunnel;
using Xunit;

namespace DualRoute.Tunnel.Tests;

public class TunForwarderTests
{
    internal static byte[] Packet(string source = "10.77.0.2", string destination = "10.77.0.1", int size = 20)
    {
        var packet = new byte[size];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)size);
        IPAddress.Parse(source).GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(packet, 16);
        return packet;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void InvalidIpv4IsRejected(int kind)
    {
        var packet = Packet();
        switch (kind)
        {
            case 0: packet = packet[..19]; break;
            case 1: packet[0] = 0x65; break;
            case 2: packet[0] = 0x44; break;
            case 3: packet[0] = 0x46; break;
            case 4: packet[3] = 19; break;
            case 5: packet[3] = 21; break;
            case 6: packet = Packet(size: Protocol.MaxPayloadSize + 1); break;
            case 7: packet = Packet(size: 24); packet[0] = 0x46; packet[3] = 20; break;
        }
        Assert.False(Ipv4Packet.TryParse(packet, out _, out _));
    }

    [Fact]
    public void OptionsAndMaximumMtuAreValid()
    {
        var packet = Packet(size: Protocol.MaxPayloadSize);
        packet[0] = 0x4f;
        Assert.True(Ipv4Packet.TryParse(packet, out var source, out var destination));
        Assert.Equal(IPAddress.Parse("10.77.0.2"), source);
        Assert.Equal(IPAddress.Parse("10.77.0.1"), destination);
    }

    [Fact]
    public void DuplicateVirtualIpIsRejectedAtServerStartup()
    {
        var a = ProtocolTests.Client();
        var b = ProtocolTests.Client(3) with { VirtualIp = a.VirtualIp };
        Assert.Throws<ArgumentException>(() => new TunnelServer(new([a, b])));
    }

    [Fact]
    public async Task TwoClientsAreIsolatedAndInvalidOrSpoofedPacketsNeverReachTun()
    {
        await using var fixture = new DataFixture();
        await fixture.ConnectAsync();
        var a = fixture.A;
        var valid = Packet();
        await a.SendDataAsync(Packet(source: "10.77.0.3"), fixture.Token);
        await a.SendDataAsync(new byte[19], fixture.Token);
        var ipv6 = Packet(); ipv6[0] = 0x65;
        await a.SendDataAsync(ipv6, fixture.Token);
        var malformed = Packet(); malformed[3] = 21;
        await a.SendDataAsync(malformed, fixture.Token);
        await a.SendDataAsync(valid, fixture.Token);
        Assert.Equal(valid, await fixture.Tun.ToKernel.Reader.ReadAsync(fixture.Token));
        Assert.False(fixture.Tun.ToKernel.Reader.TryRead(out _));

        var fromB = Packet(source: "10.77.0.3");
        await fixture.B.SendDataAsync(fromB, fixture.Token);
        Assert.Equal(fromB, await fixture.Tun.ToKernel.Reader.ReadAsync(fixture.Token));

        await fixture.Tun.FromKernel.Writer.WriteAsync(Packet(destination: "10.77.0.99"), fixture.Token);
        await fixture.Tun.FromKernel.Writer.WriteAsync(new byte[19], fixture.Token);
        await fixture.Tun.FromKernel.Writer.WriteAsync(Packet(size: Protocol.MaxPayloadSize + 1), fixture.Token);
        var toA = Packet(source: "10.77.0.1", destination: "10.77.0.2");
        var toB = Packet(source: "10.77.0.1", destination: "10.77.0.3");
        await fixture.Tun.FromKernel.Writer.WriteAsync(toA, fixture.Token);
        await fixture.Tun.FromKernel.Writer.WriteAsync(toB, fixture.Token);
        Assert.Equal(toA, (await a.ReceivedData.ReadAsync(fixture.Token)).Payload);
        Assert.Equal(toB, (await fixture.B.ReceivedData.ReadAsync(fixture.Token)).Payload);
        Assert.False(a.ReceivedData.TryRead(out _));
        Assert.False(fixture.B.ReceivedData.TryRead(out _));

        await a.DisposeAsync();
        // UDP ordering plus B's control reply provides a barrier after A's DISCONNECT.
        await fixture.B.ProbeAsync(fixture.Token);
        Assert.Equal(1, fixture.Server.SessionCount);
        await fixture.Tun.FromKernel.Writer.WriteAsync(toA, fixture.Token);
        await fixture.Tun.FromKernel.Writer.WriteAsync(toB, fixture.Token);
        Assert.Equal(toB, (await fixture.B.ReceivedData.ReadAsync(fixture.Token)).Payload);
    }

    [Fact]
    public async Task OldQueuedSessionDataIsDroppedAfterReplacement()
    {
        var config = ProtocolTests.Client();
        using var server = new TunnelServer(new([config]));
        using var old = ProtocolTests.Connect(server, config);
        server.Process(new(old.Encrypt(PacketType.Data, Packet()), ProtocolTests.Remote));
        using var replacement = ProtocolTests.Connect(server, config);
        server.Process(new(replacement.Encrypt(PacketType.Data, Packet(destination: "10.77.0.9")), ProtocolTests.Remote));
        var network = new FakeNetwork();
        await using var transport = network.Bind(50);
        await using var tun = new FakeTunDevice();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bridge = new TunForwarder(server, tun, transport);
        var run = bridge.RunAsync(stop.Token);
        try
        {
            Assert.Equal(Packet(destination: "10.77.0.9"), await tun.ToKernel.Reader.ReadAsync(stop.Token));
            Assert.False(tun.ToKernel.Reader.TryRead(out _));
            Assert.Equal(1, bridge.DroppedPackets);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task IdleSessionDoesNotReceiveTunPackets()
    {
        var clock = new ManualTime();
        var config = ProtocolTests.Client();
        using var server = new TunnelServer(new([config]), timeProvider: clock);
        using var crypto = ProtocolTests.Connect(server, config);
        clock.Advance(TimeSpan.FromSeconds(31));
        var network = new FakeNetwork();
        await using var transport = network.Bind(50);
        await using var tun = new FakeTunDevice();
        await tun.FromKernel.Writer.WriteAsync(Packet(destination: config.VirtualIp));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bridge = new TunForwarder(server, tun, transport);
        var run = bridge.RunAsync(stop.Token);
        try
        {
            await Until(() => bridge.DroppedPackets == 1, stop.Token);
            Assert.Equal(0, server.SessionCount);
            Assert.Equal(0, network.Sends);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CancelWhileTunWriteBlockedDisposesBothDevices()
    {
        var tun = new FakeTunDevice { BlockWrite = true };
        await using var fixture = new DataFixture(tun);
        await fixture.ConnectAsync();
        await fixture.A.SendDataAsync(Packet(), fixture.Token);
        await tun.WriteStarted.Task.WaitAsync(fixture.Token);
        await fixture.DisposeAsync();
        Assert.True(tun.Disposed);
        Assert.True(fixture.RawServerTransport.Disposed);
        Assert.Equal(0, fixture.Server.SessionCount);
    }

    [Fact]
    public async Task TunFailureStopsServerAndDisposesResources()
    {
        var network = new FakeNetwork();
        var transport = network.Bind(90);
        var tun = new FakeTunDevice { FailRead = true };
        using var server = new TunnelServer(new([ProtocolTests.Client()]));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => server.RunAsync(transport, tun, deadline.Token).WaitAsync(deadline.Token));
        Assert.True(tun.Disposed);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task WrongMtuFailsWithoutLeakingResources()
    {
        var network = new FakeNetwork();
        var transport = network.Bind(90);
        var tun = new FakeTunDevice { Mtu = Protocol.MaxPayloadSize + 1 };
        using var server = new TunnelServer(new([ProtocolTests.Client()]));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            server.RunAsync(transport, tun, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(tun.Disposed);
        Assert.True(transport.Disposed);
    }

    internal static async Task Until(Func<bool> condition, CancellationToken ct)
    {
        while (!condition()) { ct.ThrowIfCancellationRequested(); await Task.Delay(1, ct); }
    }
}

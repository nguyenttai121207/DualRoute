using System.Buffers.Binary;
using System.Net.NetworkInformation;
using DualRoute.Tunnel;
using Xunit;

namespace DualRoute.Tunnel.Tests;

public sealed class LinuxTunFactAttribute : FactAttribute
{
    public LinuxTunFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("DUALROUTE_TEST_LINUX_TUN") != "1")
            Skip = "Requires Linux, CAP_NET_ADMIN, iproute2 and DUALROUTE_TEST_LINUX_TUN=1; creates tun0.";
    }
}

public class LinuxTunTests
{
    [Fact]
    public async Task LinuxDeviceRejectsOtherOperatingSystems()
    {
        if (OperatingSystem.IsLinux()) return;
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => LinuxTunDevice.CreateAsync(CancellationToken.None));
    }

    [LinuxTunFact]
    [Trait("Category", "LinuxTun")]
    public async Task KernelIcmpEchoTraversesTunnelAndTunIsRemovedOnShutdown()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var tun = await LinuxTunDevice.CreateAsync(deadline.Token);
        Assert.Equal(Protocol.MaxPayloadSize, tun.Mtu);
        await Assert.ThrowsAsync<IOException>(() => LinuxTunDevice.CreateAsync(deadline.Token));
        var network = new FakeNetwork();
        var transport = network.Bind(1235);
        var config = ProtocolTests.Client();
        using var server = new TunnelServer(new([config]));
        var running = server.RunAsync(transport, tun, deadline.Token);
        await using var client = new TunnelClient(network.Bind(1234), transport.LocalEndpoint,
            config.ClientId, Convert.FromBase64String(config.PskBase64));
        try
        {
            Assert.Contains(NetworkInterface.GetAllNetworkInterfaces(), n => n.Name == tun.Name &&
                n.GetIPProperties().GetIPv4Properties().Mtu == Protocol.MaxPayloadSize);
            await client.ConnectAsync(deadline.Token);
            var packet = TunForwarderTests.Packet(size: 28);
            packet[8] = 64; packet[9] = 1;
            packet[20] = 8; // ICMP echo request, id/sequence=1.
            packet[25] = packet[27] = 1;
            Checksum(packet.AsSpan(20, 8), packet.AsSpan(22, 2));
            Checksum(packet.AsSpan(0, 20), packet.AsSpan(10, 2));
            await client.SendDataAsync(packet, deadline.Token);
            var reply = (await client.ReceivedData.ReadAsync(deadline.Token)).Payload;
            Assert.True(Ipv4Packet.TryParse(reply, out var source, out var destination));
            Assert.Equal("10.77.0.1", source!.ToString());
            Assert.Equal(config.VirtualIp, destination!.ToString());
            Assert.Equal(1, reply[9]);
            Assert.Equal(0, reply[20]); // ICMP echo reply.
            Assert.Equal(packet.AsSpan(24, 4).ToArray(), reply.AsSpan(24, 4).ToArray());
        }
        finally
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            deadline.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.DoesNotContain(NetworkInterface.GetAllNetworkInterfaces(), n => n.Name == tun.Name);
    }

    private static void Checksum(Span<byte> bytes, Span<byte> destination)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(bytes[i..]);
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)~sum);
    }
}

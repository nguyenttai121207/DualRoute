using System.Security.Cryptography;
using DualRoute.Client.Windows;
using DualRoute.Tunnel;
using DualRoute.Tunnel.Tests;
using Xunit;

namespace DualRoute.Client.Windows.Tests;

public sealed class ClientTunForwarderTests
{
    [Fact]
    public async Task ServerAssignedIpAndDataAreForwardedBothWaysWithoutSpoofing()
    {
        var config = new ClientConfiguration(Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "10.77.0.9");
        var network = new FakeNetwork(); var transport = network.Bind(100);
        using var server = new TunnelServer(new([config]));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var serverTun = new FakeTunDevice();
        var running = server.RunAsync(transport, serverTun, stop.Token);
        await using var client = new TunnelClient(network.Bind(101), transport.LocalEndpoint, config.ClientId, Convert.FromBase64String(config.PskBase64));
        await using var clientTun = new FakeTunDevice();
        Task? forwarding = null;
        try
        {
            await client.ConnectAsync(stop.Token);
            Assert.Equal(config.VirtualIp, client.VirtualIp!.ToString());
            var bridge = new ClientTunForwarder(client, clientTun);
            forwarding = bridge.RunAsync(stop.Token);
            await clientTun.FromKernel.Writer.WriteAsync(WintunDeviceTests.Packet(), stop.Token);
            var outbound = WintunDeviceTests.Packet(config.VirtualIp);
            await clientTun.FromKernel.Writer.WriteAsync(outbound, stop.Token);
            Assert.Equal(outbound, await serverTun.ToKernel.Reader.ReadAsync(stop.Token));
            var inbound = WintunDeviceTests.Packet("10.77.0.1", config.VirtualIp);
            await serverTun.FromKernel.Writer.WriteAsync(inbound, stop.Token);
            Assert.Equal(inbound, await clientTun.ToKernel.Reader.ReadAsync(stop.Token));
            Assert.Equal(1, bridge.DroppedPackets);
            await client.ProbeAsync(stop.Token);
        }
        finally
        {
            stop.Cancel();
            if (forwarding is not null) await forwarding.WaitAsync(TimeSpan.FromSeconds(5));
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FailedClientStopsIdleForwarder()
    {
        var config = new ClientConfiguration(Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "10.77.0.2");
        var network = new FakeNetwork(); var transport = network.Bind(100);
        using var server = new TunnelServer(new([config]));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = server.RunAsync(transport, stop.Token);
        await using var client = new TunnelClient(network.Bind(101), transport.LocalEndpoint, config.ClientId, Convert.FromBase64String(config.PskBase64));
        await using var tun = new FakeTunDevice();
        try
        {
            await client.ConnectAsync(stop.Token);
            var forwarding = new ClientTunForwarder(client, tun).RunAsync(stop.Token);
            await client.DisposeAsync();
            await forwarding.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}

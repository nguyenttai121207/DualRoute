using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using DualRoute.Client.Windows;
using DualRoute.Tunnel;
using Xunit;

namespace DualRoute.Client.Windows.Tests;

public sealed class WintunFactAttribute : FactAttribute
{
    public WintunFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("DUALROUTE_TEST_WINTUN") != "1")
            Skip = "Requires Windows AMD64, Administrator and explicit DUALROUTE_TEST_WINTUN=1; creates an adapter.";
        else if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "Requires elevated Administrator; no adapter created.";
    }
}

[CollectionDefinition("Wintun", DisableParallelization = true)] public sealed class WintunCollection;

[Collection("Wintun")]
public sealed class WintunIntegrationTests
{
    [WintunFact, Trait("Category", "Wintun")]
    public async Task RealAdapterHasIpMtuUpAndUdpRoundTripThenDisappears()
    {
        var system = new WindowsAdapterConfiguration();
        // An earlier DualRoute instance may be recovered during CreateCoreAsync; only preserve other adapters.
        var before = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => !Guid.TryParse(n.Id, out var guid) || guid != WintunRecovery.AdapterGuid)
            .ToDictionary(n => n.Id, n => $"{n.Name} ({n.Description}, {n.OperationalStatus})", StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(Path.GetTempPath(), "DualRouteIntegration-" + Guid.NewGuid());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var device = await WintunDevice.CreateCoreAsync(IPAddress.Parse("10.77.0.2"), new WintunNative(), system, directory, deadline.Token);
        var stage = "adapter IP/MTU/UP verification";
        try
        {
            var adapter = Assert.Single(NetworkInterface.GetAllNetworkInterfaces(), n => n.Name == device.Name);
            Assert.Equal(OperationalStatus.Up, adapter.OperationalStatus);
            var properties = adapter.GetIPProperties();
            // .NET's Mtu comes from GetAdaptersAddresses and can remain Wintun's link MTU (65535).
            var ipv4 = properties.GetIPv4Properties()!;
            Assert.Equal((uint)Protocol.MaxPayloadSize, WindowsAdapterConfiguration.GetIpv4Mtu((uint)ipv4.Index));
            Assert.Contains(properties.UnicastAddresses, a => a.Address.Equals(IPAddress.Parse("10.77.0.2")) && a.PrefixLength == 24);
            // Initiate outbound UDP so the injected response is solicited, unlike inbound ICMP on Public profiles.
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse("10.77.0.2"), 0));
            var remote = new IPEndPoint(IPAddress.Parse("10.77.0.1"), 39001);
            udp.Connect(remote);
            var localPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var payload = Guid.NewGuid().ToByteArray();
            stage = "outbound UDP capture (Windows socket -> Wintun); check outbound firewall policy if blocked";
            Assert.Equal(payload.Length, await udp.SendAsync(payload, deadline.Token));
            while (true)
            {
                var request = await device.ReadAsync(deadline.Token);
                if (!Ipv4Packet.TryParse(request, out var source, out var destination) || request[9] != 17 ||
                    !source!.Equals(IPAddress.Parse("10.77.0.2")) || !destination!.Equals(remote.Address)) continue;
                var headerLength = (request[0] & 15) * 4;
                if (request.Length != headerLength + 8 + payload.Length ||
                    BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(headerLength)) != localPort ||
                    BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(headerLength + 2)) != remote.Port) continue;
                Assert.Equal(8 + payload.Length, BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(headerLength + 4)));
                Assert.Equal(payload, request.AsSpan(headerLength + 8).ToArray());
                var reply = request.ToArray();
                request.AsSpan(12, 4).CopyTo(reply.AsSpan(16));
                request.AsSpan(16, 4).CopyTo(reply.AsSpan(12));
                request.AsSpan(headerLength, 2).CopyTo(reply.AsSpan(headerLength + 2));
                request.AsSpan(headerLength + 2, 2).CopyTo(reply.AsSpan(headerLength));
                reply.AsSpan(10, 2).Clear();
                Checksum(reply.AsSpan(0, headerLength), reply.AsSpan(10, 2));
                // RFC 768 permits checksum 0 for IPv4 UDP; captured TX checksums may be offloaded.
                reply.AsSpan(headerLength + 6, 2).Clear();
                stage = "UDP response delivery (Wintun -> Windows socket)";
                await device.WriteAsync(reply, deadline.Token);
                var received = await udp.ReceiveAsync(deadline.Token);
                Assert.Equal(remote, received.RemoteEndPoint);
                Assert.Equal(payload, received.Buffer);
                break;
            }
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        { throw new TimeoutException($"Wintun integration timed out during {stage}; device dropped {device.DroppedPackets} packets.", ex); }
        finally { await device.DisposeAsync(); }
        Assert.DoesNotContain(system.Find(), a => a.Guid == WintunRecovery.AdapterGuid);
        Assert.False(File.Exists(Path.Combine(directory, "wintun-state.json")), "Wintun cleanup left its journal behind.");
        var cleanupWait = Stopwatch.StartNew();
        string[] missing;
        bool dualRouteVisible;
        do
        {
            var current = NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            missing = before.Keys.Except(current, StringComparer.OrdinalIgnoreCase).ToArray();
            dualRouteVisible = current.Any(id => Guid.TryParse(id, out var guid) && guid == WintunRecovery.AdapterGuid);
            if ((!dualRouteVisible && missing.Length == 0) || cleanupWait.Elapsed >= TimeSpan.FromSeconds(5)) break;
            // PnP and GetAdaptersAddresses enumeration can settle after CloseAdapter returns.
            await Task.Delay(50, CancellationToken.None);
        } while (true);
        Assert.False(dualRouteVisible, $"DualRoute adapter {WintunRecovery.AdapterGuid} remains in NetworkInterface after cleanup (5s).");
        Assert.True(missing.Length == 0, $"Pre-existing non-DualRoute adapters missing after cleanup (5s): {string.Join(", ", missing.Select(id => $"{id}: {before[id]}"))}");
        Directory.Delete(directory, true);
    }

    [WintunFact, Trait("Category", "Wintun")]
    public async Task KilledProcessIsRecoveredWithoutTouchingForeignAdapter()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GameNetworkOptimizer.slnx"))) root = root.Parent;
        Assert.NotNull(root);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var harness = Path.Combine(root.FullName, "tests", "DualRoute.Wintun.TestHost", "bin", configuration, "net10.0-windows", "DualRoute.Wintun.TestHost.dll");
        var directory = Path.Combine(Path.GetTempPath(), "DualRouteCrash-" + Guid.NewGuid());
        var system = new WindowsAdapterConfiguration();
        using var foreignApi = new WintunNative();
        var foreignGuid = Guid.NewGuid();
        var foreign = foreignApi.Create("OtherTest-" + Guid.NewGuid().ToString("N"), foreignGuid);
        using var child = new Process { StartInfo = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        child.StartInfo.ArgumentList.Add(harness); child.StartInfo.ArgumentList.Add(directory);
        var started = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            started = child.Start(); Assert.True(started);
            var stderr = child.StandardError.ReadToEndAsync(deadline.Token);
            string? ready;
            do { ready = await child.StandardOutput.ReadLineAsync(deadline.Token); }
            while (ready is not null && !ready.StartsWith("READY ", StringComparison.Ordinal));
            Assert.True(ready is not null, child.HasExited ? await stderr : "Test host did not become ready.");
            var oldName = ready![6..];
            var foreignBefore = system.Find().Where(a => a.Guid != WintunRecovery.AdapterGuid).ToArray();
            child.Kill(true); await child.WaitForExitAsync(deadline.Token);
            using (var recovery = new WintunRecovery(directory, system))
            { await recovery.RecoverAsync(deadline.Token); await recovery.RecoverAsync(deadline.Token); }
            Assert.DoesNotContain(system.Find(), a => a.Name == oldName || a.Guid == WintunRecovery.AdapterGuid);
            Assert.All(foreignBefore, a => Assert.Contains(a, system.Find()));
        }
        finally
        {
            if (started && !child.HasExited) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            try
            {
                foreignApi.Close(foreign);
                // The next test must not snapshot this test fixture while PnP is still removing it.
                using var removal = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    while (system.Find().Any(a => a.Guid == foreignGuid) || NetworkInterface.GetAllNetworkInterfaces()
                        .Any(n => Guid.TryParse(n.Id, out var guid) && guid == foreignGuid))
                        await Task.Delay(50, removal.Token);
                }
                catch (OperationCanceledException ex) when (removal.IsCancellationRequested)
                { throw new TimeoutException($"Recovery test fixture {foreignGuid} remains after CloseAdapter (10s).", ex); }
            }
            finally
            {
                // If assertions fail, still attempt recovery; failed recovery deliberately retains the journal.
                if (Directory.Exists(directory))
                {
                    using (var recovery = new WintunRecovery(directory, system))
                        await recovery.RecoverAsync(CancellationToken.None);
                    Directory.Delete(directory, true);
                }
            }
        }
    }
    private static void Checksum(Span<byte> bytes, Span<byte> destination)
    {
        uint sum = 0; for (var i = 0; i < bytes.Length; i += 2) sum += BinaryPrimitives.ReadUInt16BigEndian(bytes[i..]);
        while (sum > ushort.MaxValue) sum = (sum & 65535) + (sum >> 16);
        BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)~sum);
    }
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using DualRoute.Client.Windows;
using DualRoute.Tunnel;
using Xunit;

namespace DualRoute.Client.Windows.Tests;

internal sealed class FakeAdapterSystem : IAdapterSystem
{
    internal readonly List<AdapterIdentity> Devices = [];
    internal bool FailConfiguration;
    internal int Removes;
    public IReadOnlyList<AdapterIdentity> Find() { lock (Devices) return Devices.ToArray(); }
    public void Remove(AdapterIdentity adapter) { lock (Devices) Devices.Remove(adapter); Removes++; }
    public Task ConfigureAsync(ulong luid, IPAddress ip, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return FailConfiguration ? Task.FromException(new IOException("setup")) : Task.CompletedTask; }
}

internal sealed class FakeNative(FakeAdapterSystem system) : IWintunApi
{
    internal readonly AutoResetEvent Ready = new(false);
    internal readonly ConcurrentQueue<byte[]> Incoming = new();
    internal readonly ConcurrentQueue<byte[]> Sent = new();
    internal readonly TaskCompletionSource EmptyReceive = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<nint, int> buffers = new();
    internal readonly List<string> Lifecycle = [];
    internal int ReceiveError = 259, AllocateError, Releases, Allocations;
    internal bool FailStart;
    public nint Create(string name, Guid guid)
    { Lifecycle.Add("create"); system.Devices.Add(new(guid, name, "SWD\\WINTUN\\FAKE")); return 1; }
    public void Close(nint adapter) { Lifecycle.Add("close"); system.Devices.RemoveAll(d => d.Guid == WintunRecovery.AdapterGuid); }
    public ulong Luid(nint adapter) => 1;
    public nint Start(nint adapter)
    { Lifecycle.Add("start"); return FailStart ? throw new IOException("session") : 2; }
    public void End(nint session)
    { Lifecycle.Add("end"); Assert.Empty(buffers); }
    public WaitHandle ReadEvent(nint session) => new EventWaitHandle(false, EventResetMode.AutoReset)
    { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(Ready.SafeWaitHandle.DangerousGetHandle(), false) };
    public nint Receive(nint session, out uint size, out int error)
    {
        if (!Incoming.TryDequeue(out var bytes)) { size = 0; error = ReceiveError; EmptyReceive.TrySetResult(); return 0; }
        size = (uint)bytes.Length; error = 0;
        var p = Marshal.AllocHGlobal(bytes.Length); buffers[p] = bytes.Length; Marshal.Copy(bytes, 0, p, bytes.Length); return p;
    }
    public void Release(nint session, nint packet) { Assert.True(buffers.TryRemove(packet, out _)); Marshal.FreeHGlobal(packet); Releases++; }
    public nint Allocate(nint session, uint size, out int error)
    {
        error = AllocateError;
        if (error != 0) return 0;
        var p = Marshal.AllocHGlobal((int)size); buffers[p] = (int)size; Allocations++; return p;
    }
    public void Send(nint session, nint packet)
    {
        Assert.True(buffers.TryRemove(packet, out var size)); var b = new byte[size]; Marshal.Copy(packet, b, 0, size);
        Marshal.FreeHGlobal(packet); Sent.Enqueue(b);
    }
    public void Dispose() { Lifecycle.Add("unload"); Ready.Dispose(); }
}

public sealed class WintunDeviceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DualRouteTests-" + Guid.NewGuid());
    internal static byte[] Packet(string source = "10.77.0.2", string destination = "10.77.0.1", int size = 20)
    {
        var b = new byte[size]; b[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)size);
        IPAddress.Parse(source).GetAddressBytes().CopyTo(b, 12); IPAddress.Parse(destination).GetAddressBytes().CopyTo(b, 16); return b;
    }
    private Task<WintunDevice> Create(FakeNative api, FakeAdapterSystem system, CancellationToken ct = default) =>
        WintunDevice.CreateCoreAsync(IPAddress.Parse("10.77.0.2"), api, system, directory, ct, false);

    [Fact]
    public async Task SendReceiveOwnsBuffersAndDisposeIsIdempotent()
    {
        var system = new FakeAdapterSystem(); var api = new FakeNative(system);
        var device = await Create(api, system);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        api.Incoming.Enqueue(Packet()); api.Ready.Set();
        Assert.Equal(Packet(), await device.ReadAsync(deadline.Token));
        await device.WriteAsync(Packet(), deadline.Token);
        Assert.Equal(Packet(), Assert.Single(api.Sent));
        Assert.Equal(1, api.Releases); Assert.Equal(1, api.Allocations);
        var a = device.DisposeAsync().AsTask(); var b = device.DisposeAsync().AsTask(); await Task.WhenAll(a, b);
        Assert.Equal(new[] { "create", "start", "end", "close", "unload" }, api.Lifecycle);
        Assert.Empty(system.Devices); Assert.False(File.Exists(Path.Combine(directory, "wintun-state.json")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.WriteAsync(Packet(), deadline.Token).AsTask());
    }

    [Fact]
    public async Task ReadEventWakesWorkerAfterReceiveRingWasEmpty()
    {
        var system = new FakeAdapterSystem(); var api = new FakeNative(system);
        await using var device = await Create(api, system);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await api.EmptyReceive.Task.WaitAsync(deadline.Token);
        var reading = device.ReadAsync(deadline.Token).AsTask();
        Assert.False(reading.IsCompleted);
        api.Incoming.Enqueue(Packet()); api.Ready.Set();
        Assert.Equal(Packet(), await reading);
        await device.DisposeAsync();
        Assert.Equal(1, api.Releases);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task InitializationFailureRollsBack(bool sessionFailure)
    {
        var system = new FakeAdapterSystem { FailConfiguration = !sessionFailure };
        var api = new FakeNative(system) { FailStart = sessionFailure };
        await Assert.ThrowsAsync<IOException>(() => Create(api, system));
        Assert.Empty(system.Devices); Assert.Contains("close", api.Lifecycle); Assert.Contains("unload", api.Lifecycle);
        Assert.Equal(!sessionFailure, api.Lifecycle.Contains("end"));
    }

    [Fact]
    public async Task CancellationInterruptsEmptyReadAndStopsWorker()
    {
        var system = new FakeAdapterSystem(); var api = new FakeNative(system);
        await using var device = await Create(api, system);
        using var stop = new CancellationTokenSource();
        var read = device.ReadAsync(stop.Token).AsTask(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.WriteAsync(Packet(), stop.Token).AsTask());
    }

    [Fact]
    public async Task MalformedOversizeAndFullSendRingDropWithoutNativeLeaks()
    {
        var system = new FakeAdapterSystem(); var api = new FakeNative(system) { AllocateError = 111 };
        await using var device = await Create(api, system);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await device.WriteAsync(new byte[19], deadline.Token);
        await device.WriteAsync(Packet(size: Protocol.MaxPayloadSize + 1), deadline.Token);
        await device.WriteAsync(Packet(), deadline.Token);
        api.Incoming.Enqueue(new byte[19]); api.Incoming.Enqueue(Packet(size: Protocol.MaxPayloadSize + 1));
        api.Incoming.Enqueue(Packet()); api.Ready.Set();
        Assert.Equal(Packet(), await device.ReadAsync(deadline.Token));
        Assert.Equal(5, device.DroppedPackets); Assert.Equal(3, api.Releases); Assert.Empty(api.Sent);
    }

    [Theory]
    [InlineData(38)] [InlineData(13)]
    public async Task NativeReceiveFailureIsObservableAndCanBeDisposed(int error)
    {
        var system = new FakeAdapterSystem(); var api = new FakeNative(system) { ReceiveError = error };
        await using var device = await Create(api, system);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => device.ReadAsync(deadline.Token).AsTask());
    }

    [Fact]
    public async Task NativeSendFailureFaultsPendingWrite()
    {
        var system = new FakeAdapterSystem(); var api = new FakeNative(system) { AllocateError = 38 };
        await using var device = await Create(api, system);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => device.WriteAsync(Packet(), deadline.Token).AsTask());
    }

    [Theory]
    [InlineData("10.77.0.1")] [InlineData("10.77.0.0")] [InlineData("10.77.0.255")]
    [InlineData("192.168.1.2")] [InlineData("::1")]
    public void InvalidClientIpIsRejected(string ip) => Assert.Throws<ArgumentException>(() => WintunDevice.ValidateIp(IPAddress.Parse(ip)));

    [Fact]
    public void DllMissingWrongArchitectureAndWrongHashAreRejected()
    {
        Directory.CreateDirectory(directory);
        Assert.Throws<FileNotFoundException>(() => WintunNative.ValidateDll(Path.Combine(directory, "missing.dll")));
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "wintun.dll"));
        var pe = BitConverter.ToInt32(bytes, 0x3c); bytes[pe + 4] = 0x4c; bytes[pe + 5] = 0x01;
        var wrong = Path.Combine(directory, "wrong.dll"); File.WriteAllBytes(wrong, bytes);
        Assert.Throws<BadImageFormatException>(() => WintunNative.ValidateDll(wrong));
        bytes[pe + 4] = 0x64; bytes[pe + 5] = 0x86; bytes[^1] ^= 1; File.WriteAllBytes(wrong, bytes);
        Assert.Throws<IOException>(() => WintunNative.ValidateDll(wrong));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RecoveryRemovesOnlyJournaledAdapterAndIsRepeatable(bool created)
    {
        var system = new FakeAdapterSystem();
        using (var r = new WintunRecovery(directory, system, false))
        {
            r.Begin(); system.Devices.Add(new(WintunRecovery.AdapterGuid, r.Name, "FAKE_INSTANCE"));
            if (created) r.Created();
        }
        var foreign = new AdapterIdentity(Guid.NewGuid(), "Other VPN", "OTHER"); system.Devices.Add(foreign);
        using var recovery = new WintunRecovery(directory, system, false);
        await recovery.RecoverAsync(CancellationToken.None); await recovery.RecoverAsync(CancellationToken.None);
        Assert.Equal(foreign, Assert.Single(system.Devices)); Assert.Equal(1, system.Removes);
    }

    [Theory]
    [InlineData("json")] [InlineData("owner")] [InlineData("version")] [InlineData("identity")]
    public async Task InvalidOrForeignStateNeverRemovesDevices(string kind)
    {
        var system = new FakeAdapterSystem();
        using (var r = new WintunRecovery(directory, system, false)) { r.Begin(); system.Devices.Add(new(WintunRecovery.AdapterGuid, r.Name, "INSTANCE")); }
        var path = Path.Combine(directory, "wintun-state.json");
        var state = JsonSerializer.Deserialize<WintunState>(File.ReadAllText(path))!;
        switch (kind)
        {
            case "json": File.WriteAllText(path, "{"); break;
            case "owner": File.WriteAllText(path, JsonSerializer.Serialize(state with { Owner = "Other" })); break;
            case "version": File.WriteAllText(path, JsonSerializer.Serialize(state with { Version = 2 })); break;
            case "identity": system.Devices[0] = system.Devices[0] with { Name = "Other VPN" }; break;
        }
        using var recovery = new WintunRecovery(directory, system, false);
        await Assert.ThrowsAsync<IOException>(() => recovery.RecoverAsync(CancellationToken.None));
        Assert.Equal(0, system.Removes); Assert.Single(system.Devices); Assert.True(File.Exists(path));
    }

    [Fact]
    public void PackagedDllLoadsAndAllRequiredExportsResolveWithoutCreatingAdapter()
    {
        using var api = new WintunNative();
    }

    [Fact]
    public void ActiveLeasePreventsSecondOwner()
    {
        var system = new FakeAdapterSystem();
        using var first = new WintunRecovery(directory, system, false);
        Assert.Throws<IOException>(() => new WintunRecovery(directory, system, false));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

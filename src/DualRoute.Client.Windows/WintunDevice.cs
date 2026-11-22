using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using DualRoute.Tunnel;

namespace DualRoute.Client.Windows;

public sealed class WintunDevice : ITunDevice
{
    private readonly IWintunApi api;
    private readonly WintunRecovery recovery;
    private readonly nint adapter, session;
    private readonly WaitHandle readEvent;
    private readonly ManualResetEvent stop = new(false);
    private readonly AutoResetEvent work = new(false);
    private readonly Channel<byte[]> reads = Channel.CreateBounded<byte[]>(64);
    private sealed record Request(byte[] Packet, CancellationToken Token, TaskCompletionSource Completion);
    private readonly Channel<Request> writes = Channel.CreateBounded<Request>(64);
    private readonly object gate = new();
    private readonly Task worker;
    private Task? disposal;
    private long dropped;
    public int Mtu => Protocol.MaxPayloadSize;
    public string Name { get; }
    public long DroppedPackets => Interlocked.Read(ref dropped);

    private WintunDevice(IWintunApi api, WintunRecovery recovery, nint adapter, nint session, WaitHandle readEvent)
    {
        this.api = api; this.recovery = recovery; this.adapter = adapter; this.session = session;
        this.readEvent = readEvent; Name = recovery.Name;
        worker = Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public static async Task<WintunDevice> CreateAsync(TunnelClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.State != ClientState.Connected || client.VirtualIp is null)
            throw new InvalidOperationException("Connect the tunnel before creating Wintun; its virtual IP must come from the server.");
        var ip = client.VirtualIp;
        ValidateIp(ip);
        cancellationToken.ThrowIfCancellationRequested();
        var device = await CreateCoreAsync(ip, new WintunNative(), new WindowsAdapterConfiguration(),
            WintunRecovery.DefaultDirectory, cancellationToken);
        if (client.State != ClientState.Connected || !ip.Equals(client.VirtualIp))
        {
            await device.DisposeAsync();
            throw new IOException("Tunnel state/IP changed during adapter setup; reconnect and retry.");
        }
        return device;
    }
    internal static void ValidateIp(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4 || b[0] != 10 || b[1] != 77 || b[2] != 0 || b[3] is 0 or 1 or 255)
            throw new ArgumentException("Server-assigned client IP must be 10.77.0.x/24, excluding .0, server .1 and .255.");
    }

    internal static async Task<WintunDevice> CreateCoreAsync(IPAddress ip, IWintunApi api, IAdapterSystem system,
        string directory, CancellationToken ct, bool secure = true)
    {
        WintunRecovery? recovery = null;
        nint adapter = 0, session = 0;
        WaitHandle? readEvent = null;
        try
        {
            ValidateIp(ip); ct.ThrowIfCancellationRequested();
            recovery = new(directory, system, secure);
            await recovery.RecoverAsync(ct);
            recovery.Begin();
            adapter = api.Create(recovery.Name, WintunRecovery.AdapterGuid);
            ct.ThrowIfCancellationRequested();
            recovery.Created();
            session = api.Start(adapter);
            readEvent = api.ReadEvent(session);
            await system.ConfigureAsync(api.Luid(adapter), ip, ct);
            ct.ThrowIfCancellationRequested();
            return new(api, recovery, adapter, session, readEvent);
        }
        catch
        {
            try
            {
                readEvent?.Dispose();
                if (session != 0) api.End(session);
                if (adapter != 0) api.Close(adapter);
                // Invalid/foreign journals remain untouched when no resource was created here.
                if (adapter != 0 && recovery is not null) await recovery.CompleteAsync(CancellationToken.None);
            }
            finally { recovery?.Dispose(); api.Dispose(); }
            throw;
        }
    }

    public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken) => reads.Reader.ReadAsync(cancellationToken);
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) ObjectDisposedException.ThrowIf(disposal is not null, this);
        if (!Ipv4Packet.TryParse(packet.Span, out _, out _)) { Interlocked.Increment(ref dropped); return; }
        var request = new Request(packet.ToArray(), cancellationToken, new(TaskCreationOptions.RunContinuationsAsynchronously));
        await writes.Writer.WriteAsync(request, cancellationToken);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            work.Set();
        }
        await request.Completion.Task.WaitAsync(cancellationToken);
    }
    private void Pump()
    {
        Exception? failure = null;
        Request? current = null;
        try
        {
            WaitHandle[] waits = [stop, work, readEvent];
            while (!stop.WaitOne(0))
            {
                var active = false;
                for (var i = 0; i < 64 && !stop.WaitOne(0); i++)
                {
                    var packet = api.Receive(session, out var size, out var error);
                    if (packet == 0)
                    { if (error == 259) break; throw WintunNative.Error("Wintun receive failed; restart the tunnel", error); }
                    active = true;
                    try
                    {
                        if (size > Protocol.MaxPayloadSize || size < 20) { Interlocked.Increment(ref dropped); continue; }
                        var bytes = new byte[size];
                        Marshal.Copy(packet, bytes, 0, bytes.Length);
                        if (!Ipv4Packet.TryParse(bytes, out _, out _) || !reads.Writer.TryWrite(bytes)) Interlocked.Increment(ref dropped);
                    }
                    finally { api.Release(session, packet); }
                }
                for (var i = 0; i < 64 && !stop.WaitOne(0) && writes.Reader.TryRead(out current); i++)
                {
                    active = true;
                    if (current.Token.IsCancellationRequested) current.Completion.TrySetCanceled(current.Token);
                    else
                    {
                        var packet = api.Allocate(session, (uint)current.Packet.Length, out var error);
                        if (packet == 0)
                        {
                            if (error != 111) throw WintunNative.Error("Wintun send allocation failed; restart the tunnel", error);
                            Interlocked.Increment(ref dropped);
                        }
                        else
                        {
                            // Wintun has no free-send-buffer API: no cancellation/await between allocation and send.
                            Marshal.Copy(current.Packet, 0, packet, current.Packet.Length);
                            api.Send(session, packet);
                        }
                        current.Completion.TrySetResult();
                    }
                    current = null;
                }
                if (!active) WaitHandle.WaitAny(waits);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            failure ??= new ObjectDisposedException(nameof(WintunDevice));
            current?.Completion.TrySetException(failure);
            writes.Writer.TryComplete(failure);
            while (writes.Reader.TryRead(out var pending)) pending.Completion.TrySetException(failure);
            reads.Writer.TryComplete(failure);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        stop.Set(); writes.Writer.TryComplete();
        try { await worker.ConfigureAwait(false); }
        finally
        {
            try
            {
                readEvent.Dispose(); api.End(session); api.Close(adapter);
                await recovery.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally { recovery.Dispose(); api.Dispose(); work.Dispose(); stop.Dispose(); }
        }
    }
}

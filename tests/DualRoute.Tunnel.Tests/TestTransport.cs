using System.Net;
using System.Threading.Channels;
using DualRoute.Tunnel;

namespace DualRoute.Tunnel.Tests;

internal sealed class FakeNetwork
{
    private readonly Dictionary<IPEndPoint, FakeTransport> endpoints = [];
    private readonly List<(IPEndPoint To, Datagram Packet)> held = [];
    private int sends;
    public int Sends => Volatile.Read(ref sends);
    public bool Drop { get; set; }
    public bool Duplicate { get; set; }
    public bool Hold { get; set; }
    public FakeTransport Bind(int port)
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        var transport = new FakeTransport(this, endpoint);
        endpoints.Add(endpoint, transport);
        return transport;
    }
    public void Send(IPEndPoint from, IPEndPoint to, ReadOnlyMemory<byte> bytes)
    {
        lock (held)
        {
            Interlocked.Increment(ref sends);
            if (Drop) return;
            if (bytes.Length > Protocol.MaxDatagramSize) throw new ArgumentException();
            var item = (to, new Datagram(bytes.ToArray(), from));
            if (Hold) { if (held.Count < 64) held.Add(item); }
            else Deliver(item);
        }
    }
    private void Deliver((IPEndPoint To, Datagram Packet) item)
    {
        if (!endpoints.TryGetValue(item.To, out var destination)) return;
        destination.Queue.Writer.TryWrite(item.Packet);
        if (Duplicate) destination.Queue.Writer.TryWrite(item.Packet);
    }
    public void FlushReversed()
    {
        lock (held)
        {
            foreach (var item in held.AsEnumerable().Reverse()) Deliver(item);
            held.Clear();
        }
    }
}

internal sealed class FakeTransport(FakeNetwork network, IPEndPoint endpoint) : IDatagramTransport
{
    internal Channel<Datagram> Queue { get; } = Channel.CreateBounded<Datagram>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropWrite });
    public IPEndPoint LocalEndpoint => endpoint;
    public bool Disposed { get; private set; }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, IPEndPoint remote, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Disposed, this);
        network.Send(endpoint, remote, bytes);
        return ValueTask.CompletedTask;
    }
    public ValueTask<Datagram> ReceiveAsync(CancellationToken cancellationToken) => Queue.Reader.ReadAsync(cancellationToken);
    public ValueTask DisposeAsync() { Disposed = true; Queue.Writer.TryComplete(); return ValueTask.CompletedTask; }
}

internal sealed class ManualTime : TimeProvider
{
    private long ticks;
    public int TimerCount { get { lock (timers) return timers.Count; } }
    private readonly List<ManualTimer> timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (timers)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer); timer.Change(dueTime, period); return timer;
        }
    }
    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref ticks, by.Ticks);
        ManualTimer[] snapshot;
        lock (timers) snapshot = timers.ToArray();
        foreach (var timer in snapshot) timer.Fire();
    }
    private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
    {
        private long due = long.MaxValue, period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan repeat)
        {
            lock (clock.timers)
            {
                if (disposed) return false;
                due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.GetTimestamp() + dueTime.Ticks;
                period = repeat == Timeout.InfiniteTimeSpan ? 0 : repeat.Ticks;
                return true;
            }
        }
        public void Fire()
        {
            lock (clock.timers)
            {
                if (disposed || due > clock.GetTimestamp()) return;
                due = period > 0 ? clock.GetTimestamp() + period : long.MaxValue;
            }
            callback(state);
        }
        public void Dispose() { lock (clock.timers) { disposed = true; clock.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
using System.Threading.Channels;
using DualRoute.Tunnel;

namespace DualRoute.Tunnel.Tests;

internal sealed class FakeTunDevice : ITunDevice
{
    public int Mtu { get; init; } = Protocol.MaxPayloadSize;
    public bool Disposed { get; private set; }
    public bool FailRead { get; init; }
    public bool BlockWrite { get; init; }
    public Channel<byte[]> FromKernel { get; } = Channel.CreateBounded<byte[]>(64);
    public Channel<byte[]> ToKernel { get; } = Channel.CreateBounded<byte[]>(64);
    public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken) =>
        FailRead ? ValueTask.FromException<byte[]>(new IOException("Simulated TUN read failure.")) :
        FromKernel.Reader.ReadAsync(cancellationToken);
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        WriteStarted.TrySetResult();
        if (BlockWrite) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        await ToKernel.Writer.WriteAsync(packet.ToArray(), cancellationToken);
    }
    public ValueTask DisposeAsync()
    {
        Disposed = true;
        FromKernel.Writer.TryComplete();
        ToKernel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

namespace DualRoute.Tunnel;

/// <summary>One reader and one writer; each operation transfers one whole IP packet.</summary>
public interface ITunDevice : IAsyncDisposable
{
    int Mtu { get; }
    ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
}

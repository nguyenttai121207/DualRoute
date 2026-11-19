using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace DualRoute.Tunnel;

/// <summary>A single attempt with a fresh cryptographic nonce; do not reuse for reconnects.</summary>
public sealed class ClientHandshake : IDisposable
{
    private readonly Guid id;
    private readonly byte[] psk, nonce = RandomNumberGenerator.GetBytes(16);
    private bool completed, disposed;
    public ClientHandshake(Guid clientId, byte[] psk)
    {
        if (clientId == Guid.Empty || psk.Length < 32) throw new ArgumentException("A client UUID and >=32-byte PSK are required.");
        id = clientId; this.psk = psk.ToArray();
    }
    public byte[] CreateHello()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Protocol.Hello(id, nonce, psk);
    }
    public SecureSession? AcceptAck(byte[] packet, out IPAddress? virtualIp)
    {
        virtualIp = null;
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed || packet.Length != Protocol.AckSize || packet[0] != Protocol.Version ||
            packet[1] != (byte)PacketType.HelloAck || Protocol.ReadId(packet.AsSpan(2)) != id ||
            !packet.AsSpan(18, 16).SequenceEqual(nonce) || !Protocol.Verify(packet, psk) ||
            BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(62)) != Protocol.MaxDatagramSize) return null;
        var sessionId = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(34));
        if (sessionId == 0) return null;
        virtualIp = new IPAddress(packet.AsSpan(58, 4));
        var keys = Protocol.SessionKeys(psk, nonce, packet.AsSpan(42, 16).ToArray());
        try { completed = true; return new(id, sessionId, keys.C2S, keys.S2C); }
        finally { CryptographicOperations.ZeroMemory(keys.C2S); CryptographicOperations.ZeroMemory(keys.S2C); }
    }
    public void Dispose() { disposed = true; CryptographicOperations.ZeroMemory(psk); }
}
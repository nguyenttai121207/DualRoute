using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DualRoute.Tunnel;

/// <summary>One direction's 4096-entry replay window. Commit only authenticated sequences.</summary>
public sealed class ReplayWindow
{
    private readonly HashSet<ulong> seen = [];
    private ulong highest;
    public bool CanAccept(ulong sequence) =>
        !seen.Contains(sequence) && (sequence >= highest || highest - sequence < 4096);
    public bool Commit(ulong sequence)
    {
        if (!CanAccept(sequence)) return false;
        if (sequence > highest) highest = sequence;
        seen.RemoveWhere(n => highest - n >= 4096);
        seen.Add(sequence);
        return true;
    }
}

/// <summary>Serializes crypto operations, sequence allocation, replay commits and key disposal.</summary>
public sealed class SecureSession : IDisposable
{
    private readonly object gate = new();
    private readonly byte[] sendKey, receiveKey;
    private readonly ReplayWindow replay = new();
    private ulong next;
    private bool disposed;
    public Guid ClientId { get; }
    public ulong SessionId { get; }
    public SecureSession(Guid clientId, ulong sessionId, byte[] sendKey, byte[] receiveKey)
    {
        if (sendKey.Length != 32 || receiveKey.Length != 32) throw new ArgumentException("Keys must contain 32 bytes.");
        ClientId = clientId; SessionId = sessionId;
        this.sendKey = sendKey.ToArray(); this.receiveKey = receiveKey.ToArray();
    }
    public byte[] Encrypt(PacketType type, ReadOnlySpan<byte> payload)
    {
        if (type < PacketType.Data || type > PacketType.Disconnect || payload.Length > Protocol.MaxPayloadSize)
            throw new ArgumentException("Invalid encrypted packet type or payload size.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (next == ulong.MaxValue) { Dispose(); throw new InvalidOperationException("Session sequence exhausted; reconnect."); }
            var packet = new byte[Protocol.HeaderSize + payload.Length + Protocol.TagSize];
            packet[0] = Protocol.Version; packet[1] = (byte)type;
            Protocol.WriteId(packet.AsSpan(2), ClientId);
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(18), SessionId);
            BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(26), next);
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], next++);
            using var cipher = new ChaCha20Poly1305(sendKey);
            cipher.Encrypt(nonce, payload, packet.AsSpan(Protocol.HeaderSize, payload.Length),
                packet.AsSpan(packet.Length - Protocol.TagSize), packet.AsSpan(0, Protocol.HeaderSize));
            return packet;
        }
    }
    public bool TryDecrypt(byte[] packet, out Header header, out byte[] payload)
    {
        payload = [];
        if (!Protocol.TryReadHeader(packet, out header) || header.ClientId != ClientId || header.SessionId != SessionId) return false;
        lock (gate)
        {
            if (disposed || !replay.CanAccept(header.Sequence)) return false;
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], header.Sequence);
            var plaintext = new byte[packet.Length - Protocol.HeaderSize - Protocol.TagSize];
            try
            {
                using var cipher = new ChaCha20Poly1305(receiveKey);
                cipher.Decrypt(nonce, packet.AsSpan(Protocol.HeaderSize, plaintext.Length),
                    packet.AsSpan(packet.Length - Protocol.TagSize), plaintext, packet.AsSpan(0, Protocol.HeaderSize));
            }
            catch (AuthenticationTagMismatchException) { return false; }
            replay.Commit(header.Sequence);
            payload = plaintext;
            return true;
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            CryptographicOperations.ZeroMemory(sendKey);
            CryptographicOperations.ZeroMemory(receiveKey);
        }
    }
}
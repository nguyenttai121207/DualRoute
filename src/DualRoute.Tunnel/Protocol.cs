using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace DualRoute.Tunnel;

public enum PacketType : byte { Hello = 1, HelloAck, Data, Probe, ProbeReply, Keepalive, Disconnect }
public readonly record struct Header(PacketType Type, Guid ClientId, ulong SessionId, ulong Sequence);

public static class Protocol
{
    public const byte Version = 1;
    public const int MaxDatagramSize = 1400, HeaderSize = 34, TagSize = 16;
    public const int HelloSize = 96, AckSize = 96;
    public const int MaxPayloadSize = MaxDatagramSize - HeaderSize - TagSize;

    public static byte[] HandshakeKey(byte[] psk) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, psk, 32, info: Encoding.ASCII.GetBytes("dualroute v1 handshake"));

    public static (byte[] C2S, byte[] S2C) SessionKeys(byte[] psk, byte[] clientNonce, byte[] serverNonce)
    {
        if (psk.Length < 32 || clientNonce.Length != 16 || serverNonce.Length != 16) throw new ArgumentException("Expected >=32-byte PSK and 16-byte nonces.");
        var salt = clientNonce.Concat(serverNonce).ToArray();
        return (HKDF.DeriveKey(HashAlgorithmName.SHA256, psk, 32, salt, Encoding.ASCII.GetBytes("c2s")),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, psk, 32, salt, Encoding.ASCII.GetBytes("s2c")));
    }

    internal static void WriteId(Span<byte> destination, Guid id) => id.TryWriteBytes(destination, true, out _);
    internal static Guid ReadId(ReadOnlySpan<byte> source) => new(source[..16], true);
    private static void Sign(byte[] packet, byte[] psk)
    {
        var key = HandshakeKey(psk);
        try { HMACSHA256.HashData(key, packet.AsSpan(0, packet.Length - 32), packet.AsSpan(packet.Length - 32)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal static bool Verify(byte[] packet, byte[] psk)
    {
        var key = HandshakeKey(psk);
        try
        {
            var expected = HMACSHA256.HashData(key, packet.AsSpan(0, packet.Length - 32));
            return CryptographicOperations.FixedTimeEquals(expected, packet.AsSpan(packet.Length - 32));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Hello(Guid clientId, byte[] nonce, byte[] psk)
    {
        if (nonce.Length != 16) throw new ArgumentException("Client nonce must contain 16 bytes.");
        var packet = new byte[HelloSize];
        packet[0] = Version; packet[1] = (byte)PacketType.Hello;
        WriteId(packet.AsSpan(2), clientId);
        nonce.CopyTo(packet, 18);
        Sign(packet, psk); // Includes the zero padding.
        return packet;
    }

    internal static byte[] Ack(Guid clientId, byte[] clientNonce, ulong sessionId, byte[] serverNonce, IPAddress ip, byte[] psk)
    {
        var packet = new byte[AckSize];
        packet[0] = Version; packet[1] = (byte)PacketType.HelloAck;
        WriteId(packet.AsSpan(2), clientId);
        clientNonce.CopyTo(packet, 18);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(34), sessionId);
        serverNonce.CopyTo(packet, 42);
        ip.GetAddressBytes().CopyTo(packet, 58);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(62), MaxDatagramSize);
        // ACK is 96 bytes: prefix through max packet size plus 32-byte HMAC.
        Sign(packet, psk);
        return packet;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> packet, out Header header)
    {
        header = default;
        if (packet.Length < HeaderSize + TagSize || packet.Length > MaxDatagramSize ||
            packet[0] != Version || packet[1] < (byte)PacketType.Data || packet[1] > (byte)PacketType.Disconnect) return false;
        header = new((PacketType)packet[1], ReadId(packet[2..]),
            BinaryPrimitives.ReadUInt64BigEndian(packet[18..]), BinaryPrimitives.ReadUInt64BigEndian(packet[26..]));
        return true;
    }
}
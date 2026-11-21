using System.Buffers.Binary;
using System.Net;

namespace DualRoute.Tunnel;

/// <summary>Strict IPv4 framing at the IP-device boundary; no transport payload interpretation.</summary>
public static class Ipv4Packet
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IPAddress? source, out IPAddress? destination)
    {
        source = destination = null;
        if (packet.Length < 20 || packet.Length > Protocol.MaxPayloadSize || packet[0] >> 4 != 4) return false;
        var headerLength = (packet[0] & 15) * 4;
        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if (headerLength < 20 || headerLength > packet.Length || totalLength < headerLength || totalLength != packet.Length)
            return false;
        source = new IPAddress(packet.Slice(12, 4));
        destination = new IPAddress(packet.Slice(16, 4));
        return true;
    }
}

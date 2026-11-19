using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DualRoute.Tunnel;

public sealed record ClientConfiguration(
    [property: JsonPropertyName("client_id")] Guid ClientId,
    [property: JsonPropertyName("psk_base64")] string PskBase64,
    [property: JsonPropertyName("virtual_ip")] string VirtualIp);

public sealed record ServerConfiguration([property: JsonPropertyName("clients")] ClientConfiguration[] Clients)
{
    public static ServerConfiguration Load(string path)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new ArgumentException("Configuration exceeds 1 MiB.");
        var config = JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllText(path))
            ?? throw new ArgumentException("Configuration is empty.");
        config.Validate();
        return config;
    }
    public void Validate()
    {
        if (Clients is null || Clients.Length == 0 || Clients.Length > 4096) throw new ArgumentException("Configure 1–4096 clients.");
        var ids = new HashSet<Guid>();
        var ips = new HashSet<IPAddress>();
        foreach (var client in Clients)
        {
            if (client is null || client.ClientId == Guid.Empty || !ids.Add(client.ClientId))
                throw new ArgumentException("Client IDs must be unique, nonempty UUIDs.");
            byte[] key;
            try { key = Convert.FromBase64String(client.PskBase64 ?? ""); }
            catch (FormatException) { throw new ArgumentException("Client PSK must be Base64."); }
            try { if (key.Length < 32) throw new ArgumentException("Client PSK must contain at least 32 bytes."); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
            if (!IPAddress.TryParse(client.VirtualIp, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new ArgumentException("Virtual IP must be IPv4.");
            var bytes = ip.GetAddressBytes();
            if (bytes[0] == 0 || bytes[0] == 127 || bytes[0] >= 224 || bytes[3] is 0 or 255 || !ips.Add(ip))
                throw new ArgumentException("Virtual IPs must be unique unicast IPv4 hosts (excluding .0 and .255).");
        }
    }
}

public sealed record TunnelOptions
{
    public int MaxSessions { get; init; } = 128;
    public int MaxPendingHandshakes { get; init; } = 128;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan KeepaliveInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public void Validate()
    {
        if (MaxSessions is < 1 or > 4096 || MaxPendingHandshakes is < 1 or > 4096 ||
            new[] { HandshakeTimeout, KeepaliveInterval, ResponseTimeout, IdleTimeout }.Any(t => t <= TimeSpan.Zero || t > TimeSpan.FromDays(1)))
            throw new ArgumentException("Invalid tunnel capacity or timeout.");
    }
}
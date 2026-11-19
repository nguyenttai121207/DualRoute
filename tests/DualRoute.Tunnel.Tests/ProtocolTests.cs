using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using DualRoute.Tunnel;
using Xunit;

namespace DualRoute.Tunnel.Tests;

public class ProtocolTests
{
    internal static readonly IPEndPoint Remote = new(IPAddress.Loopback, 1234);
    internal static ClientConfiguration Client(int host = 2) => new(Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), $"10.77.0.{host}");
    internal static ClientHandshake Handshake(ClientConfiguration c) => new(c.ClientId, Convert.FromBase64String(c.PskBase64));
    internal static SecureSession Connect(TunnelServer server, ClientConfiguration client)
    {
        using var handshake = Handshake(client);
        var ack = server.Process(new(handshake.CreateHello(), Remote))!;
        var session = handshake.AcceptAck(ack, out _)!;
        Assert.NotNull(session);
        var reply = server.Process(new(session.Encrypt(PacketType.Keepalive, []), Remote))!;
        Assert.True(session.TryDecrypt(reply, out _, out _));
        return session;
    }

    [Fact]
    public void HeaderIsBigEndianAndRoundTrips()
    {
        var id = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");
        var key = RandomNumberGenerator.GetBytes(32);
        using var sender = new SecureSession(id, 0x0102030405060708, key, key);
        using var receiver = new SecureSession(id, 0x0102030405060708, key, key);
        var packet = sender.Encrypt(PacketType.Data, [1, 2, 3]);
        Assert.Equal(Enumerable.Range(1, 16).Select(n => (byte)n), packet.AsSpan(2, 16).ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, packet.AsSpan(18, 8).ToArray());
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(26)));
        Assert.True(receiver.TryDecrypt(packet, out var header, out var body));
        Assert.Equal(id, header.ClientId); Assert.Equal(new byte[] { 1, 2, 3 }, body);
        var second = sender.Encrypt(PacketType.Data, []);
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64BigEndian(second.AsSpan(26)));
    }

    [Fact]
    public void CipherMatchesExplicitNonceAndHeaderAssociatedData()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var session = new SecureSession(Guid.NewGuid(), 1, key, key);
        var packet = session.Encrypt(PacketType.Data, [8, 9]);
        var plaintext = new byte[2];
        using var cipher = new ChaCha20Poly1305(key);
        cipher.Decrypt(new byte[12], packet.AsSpan(34, 2), packet.AsSpan(36, 16), plaintext, packet.AsSpan(0, 34));
        Assert.Equal(new byte[] { 8, 9 }, plaintext);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(33)] [InlineData(49)] [InlineData(1401)] [InlineData(65000)]
    public void InvalidLengthsRejected(int length) => Assert.False(Protocol.TryReadHeader(new byte[length], out _));

    [Fact]
    public void MaximumPayloadAndTampering()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var id = Guid.NewGuid();
        using var sender = new SecureSession(id, 1, key, key);
        using var receiver = new SecureSession(id, 1, key, key);
        var packet = sender.Encrypt(PacketType.Data, new byte[Protocol.MaxPayloadSize]);
        Assert.Equal(1400, packet.Length);
        Assert.Throws<ArgumentException>(() => sender.Encrypt(PacketType.Data, new byte[Protocol.MaxPayloadSize + 1]));
        foreach (var offset in new[] { 0, 1, 2, 18, 26, 34, packet.Length - 1 })
        {
            var changed = packet.ToArray(); changed[offset] ^= 0x40;
            Assert.False(receiver.TryDecrypt(changed, out _, out _));
        }
        Assert.True(receiver.TryDecrypt(packet, out _, out _));
        Assert.False(receiver.TryDecrypt(packet, out _, out _));
    }

    [Fact]
    public void ReplayWindowHandlesReorderingAndBoundary()
    {
        var window = new ReplayWindow();
        Assert.True(window.Commit(4096));
        Assert.True(window.Commit(1)); // Oldest still in window.
        Assert.False(window.Commit(0));
        Assert.False(window.Commit(1));
        Assert.True(window.Commit(4095));
        Assert.True(window.Commit(8192));
        Assert.False(window.Commit(4096));
    }

    [Fact]
    public void SequenceAllocationIsConcurrentAndDoesNotWrap()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var session = new SecureSession(Guid.NewGuid(), 1, key, key);
        var packets = new byte[100][];
        Parallel.For(0, packets.Length, i => packets[i] = session.Encrypt(PacketType.Data, []));
        Assert.Equal(Enumerable.Range(0, 100).Select(n => (ulong)n),
            packets.Select(p => BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(26))).Order());
        typeof(SecureSession).GetField("next", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(session, ulong.MaxValue);
        Assert.Throws<InvalidOperationException>(() => session.Encrypt(PacketType.Data, []));
    }

    [Fact]
    public void HandshakeAuthenticationAndDirectionalKeys()
    {
        var client = Client();
        using var server = new TunnelServer(new([client]));
        using var handshake = Handshake(client);
        var hello = handshake.CreateHello();
        Assert.True(hello.Length >= Protocol.AckSize);
        var bad = hello.ToArray(); bad[35] ^= 1;
        Assert.Null(server.Process(new(bad, Remote)));
        using var unknown = new ClientHandshake(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32));
        Assert.Null(server.Process(new(unknown.CreateHello(), Remote)));
        var ack = server.Process(new(hello, Remote))!;
        Assert.Equal(0, server.SessionCount);
        var badAck = ack.ToArray(); badAck[58] ^= 1;
        Assert.Null(handshake.AcceptAck(badAck, out _));
        using var session = handshake.AcceptAck(ack, out var ip);
        Assert.NotNull(session); Assert.Equal(client.VirtualIp, ip!.ToString());
        Assert.Null(handshake.AcceptAck(ack, out _));
        var psk = Convert.FromBase64String(client.PskBase64);
        var a = Protocol.SessionKeys(psk, hello.AsSpan(18, 16).ToArray(), ack.AsSpan(42, 16).ToArray());
        var b = Protocol.SessionKeys(psk, hello.AsSpan(18, 16).ToArray(), ack.AsSpan(42, 16).ToArray());
        Assert.Equal(a.C2S, b.C2S); Assert.NotEqual(a.C2S, a.S2C);
        Assert.Equal(HKDF.DeriveKey(HashAlgorithmName.SHA256, psk, 32,
            hello.AsSpan(18,16).ToArray().Concat(ack.AsSpan(42,16).ToArray()).ToArray(), System.Text.Encoding.ASCII.GetBytes("c2s")), a.C2S);
        var activation = session!.Encrypt(PacketType.Keepalive, []);
        var corrupt = activation.ToArray(); corrupt[^1] ^= 1;
        Assert.Null(server.Process(new(corrupt, Remote)));
        Assert.Equal(0, server.SessionCount);
        Assert.NotNull(server.Process(new(activation, Remote)));
        Assert.Equal(1, server.SessionCount);
    }

    [Fact]
    public void ReplacementRequiresAuthenticationAndDisconnectIsIsolated()
    {
        var a = Client(); var b = Client(3);
        using var server = new TunnelServer(new([a, b]));
        using var sa = Connect(server, a);
        using var sb = Connect(server, b);
        using var attempt = Handshake(a);
        var ack = server.Process(new(attempt.CreateHello(), Remote))!;
        using var replacement = attempt.AcceptAck(ack, out _)!;
        Assert.NotNull(server.Process(new(sa.Encrypt(PacketType.Keepalive, []), Remote)));
        var forged = replacement.Encrypt(PacketType.Keepalive, []); forged[^1] ^= 1;
        Assert.Null(server.Process(new(forged, Remote)));
        Assert.NotNull(server.Process(new(sa.Encrypt(PacketType.Keepalive, []), Remote)));
        Assert.NotNull(server.Process(new(replacement.Encrypt(PacketType.Keepalive, []), Remote)));
        Assert.Null(server.Process(new(sa.Encrypt(PacketType.Keepalive, []), Remote)));
        Assert.Equal(2, server.SessionCount);
        server.Process(new(replacement.Encrypt(PacketType.Disconnect, []), Remote));
        Assert.Equal(1, server.SessionCount);
        Assert.NotNull(server.Process(new(sb.Encrypt(PacketType.Keepalive, []), Remote)));
    }

    [Fact]
    public void PendingAndIdleAreBoundedAndExpireWithInjectedTime()
    {
        var client = Client(); var clock = new ManualTime();
        using var server = new TunnelServer(new([client]), new() { MaxPendingHandshakes = 1 }, clock);
        using var first = Handshake(client); using var second = Handshake(client);
        Assert.NotNull(server.Process(new(first.CreateHello(), Remote)));
        Assert.Null(server.Process(new(second.CreateHello(), Remote)));
        clock.Advance(TimeSpan.FromSeconds(6)); server.Sweep();
        Assert.Equal(0, server.PendingCount);
        using var session = Connect(server, client);
        clock.Advance(TimeSpan.FromSeconds(29)); server.Sweep();
        Assert.Equal(1, server.SessionCount);
        Assert.NotNull(server.Process(new(session.Encrypt(PacketType.Keepalive, []), Remote)));
        clock.Advance(TimeSpan.FromSeconds(30)); server.Sweep();
        Assert.Equal(0, server.SessionCount);
    }

    [Fact]
    public void SessionCapacityAndWrongEndpointAreRejected()
    {
        var a = Client(); var b = Client(3);
        using var server = new TunnelServer(new([a,b]), new() { MaxSessions = 1 });
        using var session = Connect(server,a);
        using var attempt = Handshake(b);
        Assert.Null(server.Process(new(attempt.CreateHello(),Remote)));
        Assert.Null(server.Process(new(session.Encrypt(PacketType.Keepalive,[]),new(IPAddress.Loopback,999))));
        Assert.Equal(1,server.SessionCount);
    }

    [Fact]
    public void InvalidConfigurationAndDisposedKeys()
    {
        var a = Client();
        Assert.Throws<ArgumentException>(() => new ServerConfiguration([a,a]).Validate());
        Assert.Throws<ArgumentException>(() => new ServerConfiguration([a with { PskBase64 = "fake" }]).Validate());
        Assert.Throws<ArgumentException>(() => new ServerConfiguration([a with { VirtualIp = "127.0.0.1" }]).Validate());
        Assert.Throws<ArgumentException>(() => new ServerConfiguration([a,Client(3) with { VirtualIp = a.VirtualIp }]).Validate());
        var session = new SecureSession(a.ClientId,1,RandomNumberGenerator.GetBytes(32),RandomNumberGenerator.GetBytes(32));
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Encrypt(PacketType.Data,[]));
    }

    [Fact]
    public void InvalidPacketsCannotAdvanceReplayOrAffectAnotherClient()
    {
        var a = Client(); var b = Client(3);
        using var server = new TunnelServer(new([a,b]));
        using var sa = Connect(server,a); using var sb = Connect(server,b);
        var packet = sa.Encrypt(PacketType.Keepalive,[]);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(26),ulong.MaxValue);
        Assert.Null(server.Process(new(packet,Remote)));
        Assert.NotNull(server.Process(new(sa.Encrypt(PacketType.Keepalive,[]),Remote)));
        var wrongClient = sa.Encrypt(PacketType.Disconnect,[]);
        b.ClientId.TryWriteBytes(wrongClient.AsSpan(2),true,out _);
        Assert.Null(server.Process(new(wrongClient,Remote)));
        Assert.NotNull(server.Process(new(sb.Encrypt(PacketType.Keepalive,[]),Remote)));
        Assert.Equal(2,server.SessionCount);
    }

    [Fact]
    public void ProbePayloadIsEchoedExactlyAndDataQueueIsBounded()
    {
        var a = Client();
        using var server = new TunnelServer(new([a]));
        using var session = Connect(server,a);
        var payload = RandomNumberGenerator.GetBytes(16);
        var reply = server.Process(new(session.Encrypt(PacketType.Probe,payload),Remote))!;
        Assert.True(session.TryDecrypt(reply,out var header,out var body));
        Assert.Equal(PacketType.ProbeReply,header.Type); Assert.Equal(payload,body);
        Assert.Null(server.Process(new(session.Encrypt(PacketType.Probe,[1]),Remote)));
        for (var i = 0; i < 100; i++) server.Process(new(session.Encrypt(PacketType.Data,[1]),Remote));
        var count = 0;
        while (server.ReceivedData.TryRead(out _)) count++;
        Assert.Equal(64,count); Assert.Equal(37,server.DroppedPackets);
    }

    [Fact]
    public void WrongVersionAndEncryptedPacketTypesAreRejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var session = new SecureSession(Guid.NewGuid(),1,key,key);
        var packet = session.Encrypt(PacketType.Data,[]);
        packet[0] = 2;
        Assert.False(Protocol.TryReadHeader(packet,out _));
        packet[0] = 1; packet[1] = 255;
        Assert.False(Protocol.TryReadHeader(packet,out _));
        Assert.Throws<ArgumentException>(() => session.Encrypt(PacketType.Hello,[]));
    }
}
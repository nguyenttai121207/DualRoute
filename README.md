# GameNetworkOptimizer — diagnostic MVP

A .NET 10 console tool for Windows 11. Monitors the detected IPv4 gateway and a configurable public IP using ICMP. The diagnostic module does not modify routes or forward traffic. The independent Phase 1 tunnel library is described below.

## Build, test, run

Install the .NET 10 SDK. From this directory:

    dotnet build GameNetworkOptimizer.slnx
    dotnet test GameNetworkOptimizer.slnx
    dotnet run --project src/GameNetworkOptimizer -- --public-ip 1.1.1.1 --interval-ms 1000 --timeout-ms 2000 --spike-ms 100 --data-dir data

All options are optional. Use --help for syntax. Stop with Ctrl+C. Test package restoration requires NuGet access; the application has no external packages.

## Metrics

Each target has independent session statistics:
- Current RTT is the most recent request's RTT, or n/a on failure.
- Minimum, maximum and arithmetic average use successful requests only.
- P95 uses nearest rank: sort successful RTTs, select ceil(0.95 × count), with one-based indexing.
- Average jitter is the mean absolute difference between successive successful RTTs. Failures are skipped, so two successes separated by failures still form a pair. Fewer than two successes gives n/a.
- ICMP loss is unsuccessful completed requests / all completed requests × 100. Timeouts and network errors count as failures. Zero requests gives 0%.
- A spike is a successful RTT strictly greater than the threshold. Spike timestamps and highest RTT are retained independently of loss.
- Empty latency statistics give n/a. RTTs and jitter are milliseconds; timestamps are UTC.

The dashboard shows every target's status, all metrics, spike count and highest spike, and elapsed monitoring duration.

## Monitoring and storage

Requests are sequential, one per target per cycle; a full configured interval follows each cycle. This limits request frequency, but slow requests lengthen the effective sample interval. Minimum interval is 100 ms and timeout is 1–60000 ms. Cancellation interrupts the active ping or delay; an interrupted request is not counted as loss.

Gateway discovery uses the local IPv4 interface Windows selects for the public target. For an IPv6 public target, discovery uses the IPv4 route to 1.1.1.1. No UDP datagram is sent during discovery. VPN/virtual/multiple adapters can affect selection; if no gateway is found a warning appears and public monitoring continues. Gateway changes require restarting.

CSV logs use UTF-8 and unique timestamp-based names in the automatically created data directory. Rows are flushed after each request. Existing logs are never overwritten. A write failure stops monitoring and reports an error, preserving earlier flushed rows; the failed row may be incomplete. Hardware failure or abrupt power loss cannot be guaranteed safe by a stream flush.

Successful RTT samples and spike records remain in memory for exact session P95 and timestamps. Memory grows with session length; restart for very long sessions.

## Interpretation

ICMP to a gateway/public resolver is only a diagnostic indicator. Routers may deprioritize or block ICMP, and 1.1.1.1 does not follow the same path as a Valorant server. ICMP loss is not Valorant UDP loss, and low ICMP ping does not guarantee a good gaming connection.

Tests use deterministic data and local temporary files, with no Internet pings. Live network behavior depends on the host's adapter, firewall and ICMP availability.
## DualRoute Phase 1 tunnel

src/DualRoute.Tunnel is an independent, cross-platform net10.0 class library with no NuGet dependencies. It does not reference the Network Monitor, alter routes, create virtual adapters, or forward Internet/game traffic. TunnelServer, TunnelClient, and IDatagramTransport are the integration entry points. Cancel and await the server run task before disposing it. The server owns/disposes its transport; the client owns/disposes its transport and background tasks.

Build and run all local tests:

    dotnet build GameNetworkOptimizer.slnx -c Release
    dotnet test GameNetworkOptimizer.slnx -c Release
    dotnet test tests/DualRoute.Tunnel.Tests -c Release --filter Category=Integration

Integration tests bind real UDP sockets to port 0 on loopback, connect two clients concurrently, exchange authenticated probes and DATA, disconnect A, and verify B still works. No VPS, game, external Internet, privileged port, or real configuration is needed. Test PSKs are generated at runtime. Other tests use bounded fake transports supporting drop, duplication and reversed delivery, plus a manual TimeProvider for handshake/keepalive/idle deadlines. Every test wait has an overall deadline. No Thread.Sleep is used.

### Wire format v1

All integer fields are unsigned big-endian unless noted. UUIDs occupy 16 bytes in canonical/network order, not .NET's legacy mixed-endian byte array order. Types: HELLO=1, HELLO_ACK=2, DATA=3, PROBE=4, PROBE_REPLY=5, KEEPALIVE=6, DISCONNECT=7. Version is one byte (1).

- HELLO (96 bytes): version[1], type[1], client_id[16], client_nonce[16], padding[30], HMAC[32]. Client emits zero padding; padding is authenticated.
- HELLO_ACK (96 bytes): version[1], type[1], client_id[16], client_nonce[16], session_id[8], server_nonce[16], virtual_ipv4[4], max_datagram_size[2], HMAC[32].
- HMAC authenticates every preceding byte. HMAC key = HKDF-SHA256(PSK, empty salt, ASCII info "dualroute v1 handshake", length 32).
- Session keys = HKDF-SHA256(PSK, client_nonce || server_nonce, ASCII info "c2s" or "s2c", length 32).
- Encrypted header (34 bytes): version[1], type[1], client_id[16], session_id[8], seq[8]. This entire header is ChaCha20-Poly1305 associated data. Header is followed by ciphertext and a 16-byte tag.
- Maximum UDP payload is 1400 bytes, leaving 1350 bytes for encrypted application payload.
- Nonce = four zero bytes followed by the big-endian 8-byte sequence. Separate directional keys; each sender begins at zero. Crypto operations and sequence allocation share a lock. Exhaustion closes the crypto session before sequence wrap.
- PROBE payload: probe_id[8], client-local Stopwatch timestamp[8] (signed Int64 encoded big-endian). PROBE_REPLY echoes all 16 bytes. RTT uses only the client's Stopwatch elapsed time.
- KEEPALIVE and DISCONNECT have empty encrypted payloads.

The client sends encrypted KEEPALIVE after accepting ACK and declares Connected only after its authenticated echo. The server preserves the old active session until a valid newly keyed packet authenticates. Activation disposes the old session and other pending attempts for that client. Replay checks use a 4096-sequence sliding window; only authenticated packets commit replay state. Endpoints are pinned per session; NAT rebinding requires reconnect.

### Configuration and limits

Only config.example.json is committed. Its PSKs are deliberately invalid placeholders; copy it to ignored config.json and provide independent cryptographically random Base64 PSKs decoding to at least 32 bytes. Never commit real PSKs. Load and validate through ServerConfiguration.Load(path); errors do not include supplied secrets. Virtual addresses must be unique unicast IPv4 assignments (this MVP also rejects .0/.255 hosts); no interface is configured.

TunnelOptions defaults: 128 active sessions, 128 pending handshakes, 5-second pending expiry, 5-second keepalive, 10-second response timeout, 30-second idle expiry. Timeouts and capacities are validated and configurable. Cleanup runs every second and before packet processing, using TimeProvider timestamps. Unknown IDs, invalid HMACs, failed AEAD, duplicates and malformed packets are silently dropped and counted. There are no repetitive security logs, and no secrets or payloads are logged.

The receive loop processes one bounded datagram at a time. Pending/session maps have explicit caps; replay memory is capped at 4096 entries per receiver; client inbound and server application DATA channels hold at most 64 items. A full DATA channel drops and counts the authenticated application message. Consume TunnelServer.ReceivedData to receive application DATA; nothing is forwarded. Maximum configuration file size is 1 MiB and at most 4096 client records.

A lost handshake fails after its deadline. A missed keepalive enters Reconnecting and makes one fresh handshake attempt; failure enters Failed. A caller can explicitly call ConnectAsync again. No unbounded retry loop or task creation occurs. PROBE and DATA calls serialize with lifecycle operations. DATA is one-way client-to-server in this phase; server-to-client encrypted control replies are handled by the client.

### Verification boundaries

Automated tests exercise Windows loopback and deterministic clocks; Linux deployment, external VPS paths, NAT behavior and long-running load have not been verified. This PSK protocol provides no forward secrecy. Possession of a client's PSK permits impersonating that client; protect configuration files. Authenticated clients can consume bounded pending capacity; this phase does not implement per-client rate limiting. No claim is made about Valorant RTT/loss or production security certification.
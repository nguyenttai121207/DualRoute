# GameNetworkOptimizer — diagnostic MVP

A .NET 10 console tool for Windows 11. Monitors the detected IPv4 gateway and a configurable public IP using ICMP. It does not modify routes or provide VPN, relay, optimization, or GUI functionality.

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
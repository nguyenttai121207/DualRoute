using System.Net.NetworkInformation;

namespace GameNetworkOptimizer.Models;

/// <summary>One completed diagnostic ICMP request; failed requests have no RTT.</summary>
public sealed record NetworkMeasurement(
    DateTimeOffset TimestampUtc, long SequenceNumber, string TargetAddress,
    IPStatus Status, long? LatencyMs, bool IsSpike);
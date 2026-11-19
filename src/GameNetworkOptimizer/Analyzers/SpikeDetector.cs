namespace GameNetworkOptimizer.Analyzers;

/// <summary>Detects successful RTTs strictly greater than the threshold.</summary>
public sealed class SpikeDetector
{
    private readonly List<(DateTimeOffset TimestampUtc, long LatencyMs)> spikes = [];
    /// <summary>Creates a detector with a threshold in milliseconds.</summary>
    public SpikeDetector(long thresholdMs = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdMs);
        ThresholdMs = thresholdMs;
    }
    /// <summary>Threshold in milliseconds.</summary>
    public long ThresholdMs { get; }
    /// <summary>Recorded spike times and RTTs.</summary>
    public IReadOnlyList<(DateTimeOffset TimestampUtc, long LatencyMs)> Spikes => spikes.AsReadOnly();
    /// <summary>Highest spike RTT, or null.</summary>
    public long? Highest => spikes.Count == 0 ? null : spikes.Max(s => s.LatencyMs);
    /// <summary>Records a spike if the RTT exceeds the threshold.</summary>
    public bool Add(DateTimeOffset timestampUtc, long latencyMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(latencyMs);
        if (latencyMs <= ThresholdMs) return false;
        spikes.Add((timestampUtc, latencyMs));
        return true;
    }
}
namespace GameNetworkOptimizer.Analyzers;

/// <summary>Averages absolute differences between consecutive successful RTTs, skipping failures.</summary>
public sealed class JitterAnalyzer
{
    private long? previous;
    private double total;
    private long count;
    /// <summary>Adds a successful RTT in milliseconds.</summary>
    public void Add(long latencyMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(latencyMs);
        if (previous.HasValue) { total += Math.Abs(latencyMs - previous.Value); count++; }
        previous = latencyMs;
    }
    /// <summary>Average difference in milliseconds; null until two successes.</summary>
    public double? Average => count == 0 ? null : total / count;
}
namespace GameNetworkOptimizer.Analyzers;

/// <summary>Successful RTT statistics in milliseconds, using nearest-rank P95.</summary>
public sealed class LatencyAnalyzer
{
    private readonly List<long> samples = [];
    /// <summary>Adds a successful RTT in milliseconds.</summary>
    public void Add(long latencyMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(latencyMs);
        samples.Add(latencyMs);
    }
    /// <summary>Returns min, max, average and nearest-rank P95; nulls when empty.</summary>
    public (long? Min, long? Max, double? Average, long? P95) Calculate()
    {
        if (samples.Count == 0) return (null, null, null, null);
        var sorted = samples.Order().ToArray();
        return (sorted[0], sorted[^1], samples.Average(), sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]);
    }
}
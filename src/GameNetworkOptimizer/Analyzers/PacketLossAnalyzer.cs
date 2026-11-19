namespace GameNetworkOptimizer.Analyzers;

/// <summary>Counts unsuccessful ICMP requests independently of latency spikes.</summary>
public sealed class PacketLossAnalyzer
{
    /// <summary>Total completed requests.</summary>
    public long Total { get; private set; }
    /// <summary>Completed requests that did not succeed, including timeouts.</summary>
    public long Failed { get; private set; }
    /// <summary>Records one completed request.</summary>
    public void Add(bool success) { Total++; if (!success) Failed++; }
    /// <summary>Failed / total times 100; zero before any request.</summary>
    public double Percentage => Total == 0 ? 0 : Failed * 100.0 / Total;
}
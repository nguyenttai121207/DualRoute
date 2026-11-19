using System.Diagnostics;
using System.Net.NetworkInformation;
using GameNetworkOptimizer.Analyzers;
using GameNetworkOptimizer.Models;

namespace GameNetworkOptimizer.Presentation;

/// <summary>Maintains independent target statistics and renders the console dashboard.</summary>
public sealed class ConsoleDashboard
{
    private sealed class TargetState(long threshold)
    {
        public NetworkMeasurement? Current;
        public readonly LatencyAnalyzer Latency = new();
        public readonly JitterAnalyzer Jitter = new();
        public readonly PacketLossAnalyzer Loss = new();
        public readonly SpikeDetector Spikes = new(threshold);
    }
    private readonly Dictionary<string, TargetState> states;
    private readonly Stopwatch duration = Stopwatch.StartNew();
    /// <summary>Creates an independent statistics bucket for each target.</summary>
    public ConsoleDashboard(IEnumerable<string> targets, long spikeMs) =>
        states = targets.ToDictionary(t => t, _ => new TargetState(spikeMs));
    /// <summary>Records one completed request.</summary>
    public void Add(NetworkMeasurement measurement)
    {
        var state = states[measurement.TargetAddress];
        state.Current = measurement;
        var success = measurement.Status == IPStatus.Success && measurement.LatencyMs.HasValue;
        state.Loss.Add(success);
        if (success)
        {
            var latency = measurement.LatencyMs!.Value;
            state.Latency.Add(latency);
            state.Jitter.Add(latency);
            state.Spikes.Add(measurement.TimestampUtc, latency);
        }
    }
    /// <summary>Renders all target metrics; redirected output receives ordinary snapshots.</summary>
    public void Render()
    {
        static string Ms(double? value) => value.HasValue ? $"{value.Value:F1} ms" : "n/a";
        if (!Console.IsOutputRedirected)
        {
            try { Console.Clear(); }
            catch (IOException) { }
        }
        Console.WriteLine($"GameNetworkOptimizer | Duration {duration.Elapsed:hh\\:mm\\:ss} | Ctrl+C to stop");
        Console.WriteLine("ICMP diagnostics only; these are not Valorant UDP latency/loss measurements.");
        foreach (var (target, state) in states)
        {
            var stats = state.Latency.Calculate();
            Console.WriteLine($"\nTarget: {target} | Status: {state.Current?.Status.ToString() ?? "Waiting"}");
            Console.WriteLine($"Current: {Ms(state.Current?.LatencyMs)} | Avg: {Ms(stats.Average)} | Min: {Ms(stats.Min)} | Max: {Ms(stats.Max)} | P95: {Ms(stats.P95)}");
            Console.WriteLine($"Jitter: {Ms(state.Jitter.Average)} | Loss: {state.Loss.Percentage:F2}% ({state.Loss.Failed}/{state.Loss.Total}) | Spikes: {state.Spikes.Spikes.Count} | Highest spike: {Ms(state.Spikes.Highest)}");
        }
    }
}
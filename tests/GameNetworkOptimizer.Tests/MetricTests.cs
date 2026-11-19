using System.Net.NetworkInformation;
using GameNetworkOptimizer.Analyzers;
using GameNetworkOptimizer.Models;
using GameNetworkOptimizer.Services;
using Xunit;

namespace GameNetworkOptimizer.Tests;

public class MetricTests
{
    [Fact]
    public void LatencyUsesSuccessfulSamplesAndNearestRank()
    {
        var analyzer = new LatencyAnalyzer();
        foreach (var n in Enumerable.Range(1, 100).Reverse()) analyzer.Add(n);
        var result = analyzer.Calculate();
        Assert.Equal(50.5, result.Average);
        Assert.Equal(1L, result.Min);
        Assert.Equal(100L, result.Max);
        Assert.Equal(95L, result.P95);
    }

    [Fact]
    public void EmptyMetricsHaveDefinedValues()
    {
        var latency = new LatencyAnalyzer().Calculate();
        Assert.Null(latency.Min);
        Assert.Null(latency.Max);
        Assert.Null(latency.Average);
        Assert.Null(latency.P95);
        Assert.Null(new JitterAnalyzer().Average);
        Assert.Equal(0, new PacketLossAnalyzer().Percentage);
        Assert.Null(new SpikeDetector().Highest);
    }

    [Fact]
    public void OneSuccessHasNoJitter()
    {
        var jitter = new JitterAnalyzer();
        jitter.Add(20);
        Assert.Null(jitter.Average);
        var latency = new LatencyAnalyzer();
        latency.Add(20);
        Assert.Equal(20L, latency.Calculate().P95);
    }

    [Fact]
    public void JitterAveragesAbsoluteConsecutiveDifferences()
    {
        var jitter = new JitterAnalyzer();
        foreach (var n in new long[] { 20, 25, 22, 35 }) jitter.Add(n);
        Assert.Equal(7, jitter.Average);
    }

    [Fact]
    public void TimeoutsAndOtherFailuresCountAsLoss()
    {
        var loss = new PacketLossAnalyzer();
        var latency = new LatencyAnalyzer();
        var jitter = new JitterAnalyzer();
        var samples = new (IPStatus Status, long? Rtt)[]
        {
            (IPStatus.Success, 20), (IPStatus.TimedOut, null),
            (IPStatus.DestinationHostUnreachable, null), (IPStatus.Success, 30)
        };
        foreach (var sample in samples)
        {
            loss.Add(sample.Status == IPStatus.Success);
            if (sample.Rtt is long rtt) { latency.Add(rtt); jitter.Add(rtt); }
        }
        Assert.Equal(50, loss.Percentage);
        Assert.Equal(2, loss.Failed);
        Assert.Equal(25, latency.Calculate().Average);
        Assert.Equal(10, jitter.Average);
    }

    [Fact]
    public void AllFailuresHaveOneHundredPercentLoss()
    {
        var loss = new PacketLossAnalyzer();
        loss.Add(false);
        Assert.Equal(100, loss.Percentage);
    }

    [Fact]
    public void SpikesAreStrictlyAboveThresholdAndRetainTimes()
    {
        var detector = new SpikeDetector();
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Assert.False(detector.Add(time, 100));
        Assert.True(detector.Add(time, 101));
        Assert.True(detector.Add(time.AddSeconds(1), 140));
        Assert.Equal(2, detector.Spikes.Count);
        Assert.Equal(time, detector.Spikes[0].TimestampUtc);
        Assert.Equal(140L, detector.Highest);
        Assert.False(new SpikeDetector(200).Add(time, 140));
    }

    [Theory]
    [InlineData("--interval-ms", "0")]
    [InlineData("--timeout-ms", "-1")]
    [InlineData("--spike-ms", "-1")]
    [InlineData("--public-ip", "invalid")]
    public void InvalidOptionsAreRejected(string key, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => MonitorOptions.Parse([key, value]));

    [Fact]
    public async Task CsvPreservesRowsAndUsesUniqueFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            await using (var logger = new CsvLogger(directory))
            {
                path = logger.FilePath;
                await logger.WriteAsync(new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), 1, "1.1.1.1", IPStatus.Success, 123, true));
                await logger.WriteAsync(new(DateTimeOffset.Parse("2026-01-01T00:00:01Z"), 2, "1.1.1.1", IPStatus.TimedOut, null, false));
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var flushed = (await reader.ReadToEndAsync()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(3, flushed.Length);
            }
            await using (var second = new CsvLogger(directory)) Assert.NotEqual(path, second.FilePath);
            var rows = await File.ReadAllLinesAsync(path);
            Assert.Equal("TimestampUtc,SequenceNumber,TargetAddress,Status,LatencyMs,IsSpike", rows[0]);
            Assert.EndsWith(",1,\"1.1.1.1\",Success,123,true", rows[1]);
            Assert.EndsWith(",2,\"1.1.1.1\",TimedOut,,false", rows[2]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
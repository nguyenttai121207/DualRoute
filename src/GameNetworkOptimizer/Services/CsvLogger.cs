using System.Globalization;
using System.Text;
using GameNetworkOptimizer.Models;

namespace GameNetworkOptimizer.Services;

/// <summary>Writes unique UTF-8 CSV logs, flushing each row to preserve completed writes.</summary>
public sealed class CsvLogger : IAsyncDisposable
{
    private readonly StreamWriter writer;
    /// <summary>Creates the directory and a new log without overwriting any existing file.</summary>
    public CsvLogger(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"measurements-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.csv");
        writer = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        writer.WriteLine("TimestampUtc,SequenceNumber,TargetAddress,Status,LatencyMs,IsSpike");
        writer.Flush();
    }
    /// <summary>Path of the current CSV log.</summary>
    public string FilePath { get; }
    /// <summary>Appends and flushes a row; writing errors propagate so monitoring can stop.</summary>
    public async Task WriteAsync(NetworkMeasurement measurement)
    {
        static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var m = measurement;
        await writer.WriteLineAsync(string.Join(",",
            m.TimestampUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            m.SequenceNumber.ToString(CultureInfo.InvariantCulture), Escape(m.TargetAddress),
            m.Status, m.LatencyMs?.ToString(CultureInfo.InvariantCulture) ?? "", m.IsSpike ? "true" : "false"));
        await writer.FlushAsync();
    }
    /// <summary>Flushes and closes the log.</summary>
    public ValueTask DisposeAsync() => writer.DisposeAsync();
}
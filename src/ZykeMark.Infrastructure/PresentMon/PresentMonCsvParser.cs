using System.Globalization;
using ZykeMark.Core.Models;

namespace ZykeMark.Infrastructure.PresentMon;

public sealed class PresentMonCsvParser
{
    private readonly Dictionary<string, int> _columnIndexes = new(StringComparer.OrdinalIgnoreCase);

    public void ParseHeader(string headerLine)
    {
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            throw new ArgumentException("Header line is required.", nameof(headerLine));
        }

        var headers = headerLine.Split(',', StringSplitOptions.TrimEntries);
        _columnIndexes.Clear();

        for (var i = 0; i < headers.Length; i++)
        {
            if (!_columnIndexes.ContainsKey(headers[i]))
            {
                _columnIndexes.Add(headers[i], i);
            }
        }

        EnsureRequiredColumns();
    }

    public bool TryParse(string line, out FrameSample sample)
    {
        sample = default!;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (_columnIndexes.Count == 0)
        {
            throw new InvalidOperationException("CSV header must be parsed before parsing rows.");
        }

        var fields = line.Split(',', StringSplitOptions.None);

        // Try CPUStartQPCTime first (when --qpc_time_ms flag is used), then TimeInSeconds (default output)
        if (!TryReadTimestampMs(fields, out var timestampMs))
        {
            return false;
        }

        if (!TryReadDouble(fields, new[] { "MsBetweenPresents", "FrameTime" }, out var frameTimeMs))
        {
            return false;
        }

        var cpuFrameTimeMs = TryReadNullableDouble(fields, new[] { "MsCPUBusy", "CPUBusy" });
        var gpuFrameTimeMs = TryReadNullableDouble(fields, new[] { "MsGPUTime", "GPUTime" });

        sample = new FrameSample(timestampMs, frameTimeMs, cpuFrameTimeMs, gpuFrameTimeMs);
        return true;
    }

    // All timestamp column names that are already in milliseconds
    private static readonly string[] TimestampColumnsMs = { "CPUStartTime", "CPUStartQPC", "CPUStartQPCTime", "CPUStartDateTime" };

    // Timestamp column name that needs conversion from seconds to milliseconds
    private const string TimeInSecondsColumn = "TimeInSeconds";

    private bool TryReadTimestampMs(string[] fields, out double timestampMs)
    {
        // CPUStartTime, CPUStartQPC, CPUStartQPCTime, CPUStartDateTime are already in milliseconds
        if (TryReadDouble(fields, TimestampColumnsMs, out timestampMs))
        {
            return true;
        }

        // TimeInSeconds needs conversion to milliseconds (legacy PresentMon output)
        if (TryReadDouble(fields, new[] { TimeInSecondsColumn }, out var timeInSeconds))
        {
            timestampMs = timeInSeconds * 1000.0;
            return true;
        }

        timestampMs = 0;
        return false;
    }

    private bool TryReadDouble(string[] fields, IEnumerable<string> columnNames, out double value)
    {
        value = 0;
        var index = GetColumnIndex(columnNames);
        if (index is null || index.Value >= fields.Length)
        {
            return false;
        }

        var raw = fields[index.Value];
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private double? TryReadNullableDouble(string[] fields, IEnumerable<string> columnNames)
    {
        var index = GetColumnIndex(columnNames);
        if (index is null || index.Value >= fields.Length)
        {
            return null;
        }

        var raw = fields[index.Value];
        if (string.Equals(raw, "NA", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private int? GetColumnIndex(IEnumerable<string> columnNames)
    {
        foreach (var columnName in columnNames)
        {
            if (_columnIndexes.TryGetValue(columnName, out var index))
            {
                return index;
            }
        }

        return null;
    }

    private void EnsureRequiredColumns()
    {
        var hasTimestamp = TimestampColumnsMs.Any(col => _columnIndexes.ContainsKey(col)) || _columnIndexes.ContainsKey(TimeInSecondsColumn);
        var hasFrameTime = _columnIndexes.ContainsKey("MsBetweenPresents") || _columnIndexes.ContainsKey("FrameTime");

        if (!hasTimestamp || !hasFrameTime)
        {
            var acceptedTimestampColumns = string.Join(", ", TimestampColumnsMs.Append(TimeInSecondsColumn));
            var found = string.Join(", ", _columnIndexes.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"PresentMon CSV missing required columns. Need ({acceptedTimestampColumns}) and (MsBetweenPresents or FrameTime). Header columns: [{found}].");
        }
    }
}

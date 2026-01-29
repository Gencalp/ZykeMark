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

    private bool TryReadTimestampMs(string[] fields, out double timestampMs)
    {
        // CPUStartQPCTime is already in milliseconds (when --qpc_time_ms flag is used)
        if (TryReadDouble(fields, new[] { "CPUStartQPCTime" }, out timestampMs))
        {
            return true;
        }

        // TimeInSeconds needs conversion to milliseconds (default PresentMon output)
        if (TryReadDouble(fields, new[] { "TimeInSeconds" }, out var timeInSeconds))
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
        var hasTimestamp = _columnIndexes.ContainsKey("CPUStartQPCTime") || _columnIndexes.ContainsKey("TimeInSeconds");
        var hasFrameTime = _columnIndexes.ContainsKey("MsBetweenPresents") || _columnIndexes.ContainsKey("FrameTime");

        if (!hasTimestamp || !hasFrameTime)
        {
            var found = string.Join(", ", _columnIndexes.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"PresentMon CSV missing required columns. Need (CPUStartQPCTime or TimeInSeconds) and (MsBetweenPresents or FrameTime). Found: {found}.");
        }
    }
}

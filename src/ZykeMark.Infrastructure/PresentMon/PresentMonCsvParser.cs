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

        if (!TryReadDouble(fields, new[] { "CPUStartQPCTime" }, out var timestampMs))
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
}

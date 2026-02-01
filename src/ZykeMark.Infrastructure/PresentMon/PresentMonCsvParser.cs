using System.Globalization;
using ZykeMark.Core.Models;

namespace ZykeMark.Infrastructure.PresentMon;

/// <summary>
/// Reason for why a row could not be parsed.
/// </summary>
public enum ParseFailureReason
{
    None,
    EmptyLine,
    TimestampParseFailed,
    FrameTimeParseFailed
}

public sealed class PresentMonCsvParser
{
    private readonly Dictionary<string, int> _columnIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the column names parsed from the header line.
    /// </summary>
    public IReadOnlyCollection<string> HeaderColumns => _columnIndexes.Keys;

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
        return TryParse(line, out sample, out _);
    }

    public bool TryParse(string line, out FrameSample sample, out ParseFailureReason failureReason)
    {
        sample = default!;
        failureReason = ParseFailureReason.None;

        if (string.IsNullOrWhiteSpace(line))
        {
            failureReason = ParseFailureReason.EmptyLine;
            return false;
        }

        if (_columnIndexes.Count == 0)
        {
            throw new InvalidOperationException("CSV header must be parsed before parsing rows.");
        }

        var fields = line.Split(',', StringSplitOptions.None);

        // Try timestamp columns: CPUStartTime (default), CPUStartQPC, CPUStartQPCTime, CPUStartDateTime, or TimeInSeconds
        if (!TryReadTimestampMs(fields, out var timestampMs))
        {
            failureReason = ParseFailureReason.TimestampParseFailed;
            return false;
        }

        if (!TryReadDouble(fields, new[] { "MsBetweenPresents", "FrameTime" }, out var frameTimeMs))
        {
            failureReason = ParseFailureReason.FrameTimeParseFailed;
            return false;
        }

        var cpuFrameTimeMs = TryReadNullableDouble(fields, new[] { "MsCPUBusy", "CPUBusy", "msCPUActive", "CPUActive" });
        var gpuFrameTimeMs = TryReadNullableDouble(fields, new[] { "MsGPUTime", "GPUTime", "GPUBusy", "msGPUActive", "GPUActive" });

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

    /// <summary>
    /// Discovers the CSV header line from a set of lines, skipping preamble lines like
    /// "Started recording.", "Stopped recording.", or other non-CSV console output.
    /// A valid header line must contain "Application" and "ProcessID" tokens separated by commas.
    /// </summary>
    /// <param name="lines">Lines to search (typically first N lines of a file).</param>
    /// <param name="headerLineIndex">The zero-based index of the discovered header line.</param>
    /// <returns>The discovered header line, or null if no valid header was found.</returns>
    public static string? DiscoverHeaderLine(IEnumerable<string> lines, out int headerLineIndex)
    {
        headerLineIndex = -1;
        var index = 0;

        foreach (var line in lines)
        {
            if (IsValidCsvHeaderLine(line))
            {
                headerLineIndex = index;
                return line;
            }

            index++;
        }

        return null;
    }

    /// <summary>
    /// Checks if a line appears to be a valid PresentMon CSV header line.
    /// A valid header must contain commas and the required "Application" and "ProcessID" columns.
    /// </summary>
    public static bool IsValidCsvHeaderLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        // Must contain commas (CSV format)
        if (!line.Contains(','))
        {
            return false;
        }

        // Must contain both "Application" and "ProcessID" tokens (case-insensitive)
        var containsApplication = line.Contains("Application", StringComparison.OrdinalIgnoreCase);
        var containsProcessId = line.Contains("ProcessID", StringComparison.OrdinalIgnoreCase);

        return containsApplication && containsProcessId;
    }
}

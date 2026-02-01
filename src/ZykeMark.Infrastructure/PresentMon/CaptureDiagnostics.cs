using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZykeMark.Infrastructure.PresentMon;

/// <summary>
/// Comprehensive diagnostics information captured during PresentMon execution.
/// Written to capture_diagnostics.json in the session folder for debugging.
/// </summary>
public sealed class CaptureDiagnostics
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Absolute path to the PresentMon executable.
    /// </summary>
    public string? PresentMonExePath { get; set; }

    /// <summary>
    /// Working directory for the PresentMon process.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Full argument string passed to PresentMon.
    /// </summary>
    public string? ArgumentString { get; set; }

    /// <summary>
    /// UTC timestamp when capture started.
    /// </summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>
    /// UTC timestamp when capture ended.
    /// </summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>
    /// Exit code from the PresentMon process.
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// Standard output captured from PresentMon.
    /// </summary>
    public string? StdOut { get; set; }

    /// <summary>
    /// Standard error captured from PresentMon.
    /// </summary>
    public string? StdErr { get; set; }

    /// <summary>
    /// Path to the CSV output file (expected or discovered).
    /// </summary>
    public string? OutputCsvPath { get; set; }

    /// <summary>
    /// Whether the output CSV file exists.
    /// </summary>
    public bool? OutputCsvExists { get; set; }

    /// <summary>
    /// Size of the output CSV file in bytes.
    /// </summary>
    public long? FileSizeBytes { get; set; }

    /// <summary>
    /// Last write time of the CSV file in UTC.
    /// </summary>
    public DateTime? LastWriteTimeUtc { get; set; }

    /// <summary>
    /// First 30 lines of the CSV file (or fewer if file is smaller).
    /// </summary>
    public IReadOnlyList<string>? CsvPreviewLines { get; set; }

    /// <summary>
    /// Column names parsed from the CSV header.
    /// </summary>
    public IReadOnlyList<string>? ParsedHeaderColumns { get; set; }

    /// <summary>
    /// Number of data rows parsed from the CSV file.
    /// </summary>
    public int? ParsedRowCount { get; set; }

    /// <summary>
    /// Number of data rows that failed to parse.
    /// </summary>
    public int? SkippedRowCount { get; set; }

    /// <summary>
    /// Parser errors encountered during CSV parsing.
    /// </summary>
    public IReadOnlyList<string>? ParserErrors { get; set; }

    /// <summary>
    /// Whether the capture was successful overall.
    /// </summary>
    public bool? Success { get; set; }

    /// <summary>
    /// Detailed failure reason if the capture failed.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Exception details if an exception occurred.
    /// </summary>
    public string? ExceptionDetails { get; set; }

    /// <summary>
    /// Writes the diagnostics to a JSON file.
    /// </summary>
    public void WriteToFile(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Writes the diagnostics to capture_diagnostics.json in the specified folder.
    /// </summary>
    public void WriteToSessionFolder(string sessionFolder)
    {
        var path = Path.Combine(sessionFolder, "capture_diagnostics.json");
        WriteToFile(path);
    }

    /// <summary>
    /// Generates a detailed failure message for when FrameCount==0.
    /// </summary>
    public string GenerateDetailedErrorMessage()
    {
        var messages = new List<string>();

        // Check if PresentMon started
        if (ExitCode is null)
        {
            messages.Add("PresentMon failed to start");
            if (!string.IsNullOrWhiteSpace(ExceptionDetails))
            {
                messages.Add($"Exception: {ExceptionDetails}");
            }
            return string.Join(". ", messages);
        }

        // Check exit code
        if (ExitCode != 0)
        {
            messages.Add($"PresentMon exited with code {ExitCode}");
            if (!string.IsNullOrWhiteSpace(StdErr))
            {
                var stderrPreview = StdErr.Length > 500 ? StdErr[..500] + "..." : StdErr;
                messages.Add($"stderr: {stderrPreview}");
            }
        }

        // Check CSV file
        if (OutputCsvExists != true)
        {
            messages.Add($"CSV output file not found at: {OutputCsvPath ?? "(unknown)"}");
        }
        else if (FileSizeBytes == 0)
        {
            messages.Add("CSV output file is empty (0 bytes)");
        }
        else if (ParsedHeaderColumns is null || ParsedHeaderColumns.Count == 0)
        {
            messages.Add("CSV file contains no valid header");
            if (CsvPreviewLines is { Count: > 0 })
            {
                messages.Add($"First line: {CsvPreviewLines[0]}");
            }
        }
        else if (ParsedRowCount == 0)
        {
            messages.Add("CSV file contains header but no data rows");
            if (CsvPreviewLines is { Count: > 1 })
            {
                var headerLine = CsvPreviewLines.FirstOrDefault(l => l.Contains("Application", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(headerLine))
                {
                    messages.Add($"Header: {headerLine}");
                }
            }
        }

        // Add parser errors if any
        if (ParserErrors is { Count: > 0 })
        {
            messages.Add($"Parser errors: {string.Join("; ", ParserErrors.Take(3))}");
        }

        // Add skip ratio if concerning
        if (ParsedRowCount == 0 && SkippedRowCount > 0)
        {
            messages.Add($"All {SkippedRowCount} data rows were skipped (parsing failed)");
        }

        if (messages.Count == 0)
        {
            messages.Add("Unknown failure - check capture_diagnostics.json for details");
        }

        // Add reference to diagnostics file
        messages.Add("See capture_diagnostics.json in session folder for full details");

        return string.Join(". ", messages);
    }
}

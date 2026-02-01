using System.IO;
using System.Text.Json;
using ZykeMark.Core.Models;

namespace ZykeMark.App.Desktop.Services;

/// <summary>
/// Service for discovering and reading session data from the sessions root folder.
/// </summary>
public sealed class SessionDiscoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Gets the sessions root folder path.
    /// </summary>
    public static string GetSessionsRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZykeMark",
            "sessions");
    }

    /// <summary>
    /// Discovers all sessions in the sessions root folder.
    /// </summary>
    public IReadOnlyList<SessionListItem> DiscoverSessions()
    {
        var sessionsRoot = GetSessionsRoot();

        if (!Directory.Exists(sessionsRoot))
        {
            return Array.Empty<SessionListItem>();
        }

        var sessions = new List<SessionListItem>();
        var sessionDirs = Directory.GetDirectories(sessionsRoot);

        foreach (var sessionDir in sessionDirs)
        {
            try
            {
                var item = LoadSessionListItem(sessionDir);
                if (item is not null)
                {
                    sessions.Add(item);
                }
            }
            catch
            {
                // Skip sessions that can't be loaded
            }
        }

        // Sort by date descending (most recent first)
        return sessions
            .OrderByDescending(s => s.StartedAtUtc)
            .ToList();
    }

    /// <summary>
    /// Loads summary data for a specific session.
    /// </summary>
    public SessionSummaryData? LoadSessionSummary(string sessionFolder)
    {
        var summaryPath = Path.Combine(sessionFolder, "summary.json");

        if (!File.Exists(summaryPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(summaryPath);
            return JsonSerializer.Deserialize<SessionSummaryData>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private SessionListItem? LoadSessionListItem(string sessionDir)
    {
        var sessionId = Path.GetFileName(sessionDir);
        var summaryPath = Path.Combine(sessionDir, "summary.json");
        var metadataPath = Path.Combine(sessionDir, "metadata.json");

        // Try to load from summary.json first (more complete)
        if (File.Exists(summaryPath))
        {
            try
            {
                var json = File.ReadAllText(summaryPath);
                var summary = JsonSerializer.Deserialize<SessionSummaryData>(json, JsonOptions);

                if (summary?.Metadata is not null)
                {
                    return new SessionListItem(
                        sessionId,
                        sessionDir,
                        summary.Metadata.GameName ?? "Unknown",
                        summary.Metadata.BuildVersion,
                        summary.Metadata.StartedAtUtc,
                        summary.Metadata.DurationMs,
                        summary.Aggregates?.AvgFps,
                        summary.Aggregates?.P99FrameTimeMs,
                        summary.DataQuality?.EtwEventsLostRiskLevel,
                        File.Exists(Path.Combine(sessionDir, "report.pdf")),
                        summary.Metadata.CaptureTarget?.ToDisplayString());
                }
            }
            catch
            {
                // Fall through to metadata
            }
        }

        // Fall back to metadata.json
        if (File.Exists(metadataPath))
        {
            try
            {
                var json = File.ReadAllText(metadataPath);
                var metadata = JsonSerializer.Deserialize<SessionMetadata>(json, JsonOptions);

                if (metadata is not null)
                {
                    return new SessionListItem(
                        sessionId,
                        sessionDir,
                        metadata.GameName ?? "Unknown",
                        metadata.BuildVersion,
                        metadata.StartedAtUtc,
                        metadata.DurationMs,
                        null,  // AvgFps
                        null,  // P99FrameTimeMs
                        null,  // EtwRiskLevel
                        File.Exists(Path.Combine(sessionDir, "report.pdf")),
                        metadata.CaptureTarget?.ToDisplayString());
                }
            }
            catch
            {
                // Skip this session
            }
        }

        return null;
    }
}

/// <summary>
/// Represents a session in the sessions list.
/// </summary>
public sealed record SessionListItem(
    string SessionId,
    string SessionFolder,
    string GameName,
    string? BuildVersion,
    DateTime StartedAtUtc,
    long? DurationMs,
    double? AvgFps,
    double? P99FrameTimeMs,
    string? EtwRiskLevel,
    bool HasReport,
    string? CaptureTargetDisplay = null)
{
    /// <summary>
    /// Display date in user's locale format (e.g., dd/MM/yyyy HH:mm for tr-TR).
    /// </summary>
    public string StartDateDisplay
    {
        get
        {
            var localTime = StartedAtUtc.ToLocalTime();
            // Use short date + short time patterns from current culture
            // This respects user locale: dd/MM/yyyy HH:mm for tr-TR, MM/dd/yyyy hh:mm for en-US, etc.
            return localTime.ToString("g"); // General date/time (short)
        }
    }

    public string DurationDisplay
    {
        get
        {
            if (!DurationMs.HasValue)
            {
                return "--:--";
            }

            var duration = TimeSpan.FromMilliseconds(DurationMs.Value);
            // Include hours when duration is 60+ minutes
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"mm\:ss");
        }
    }

    public string AvgFpsDisplay => AvgFps.HasValue
        ? AvgFps.Value.ToString("F1")
        : "N/A";

    public string P99Display => P99FrameTimeMs.HasValue
        ? $"{P99FrameTimeMs.Value:F1} ms"
        : "N/A";
}

/// <summary>
/// Data model for deserializing summary.json.
/// </summary>
public sealed class SessionSummaryData
{
    public SessionMetadataData? Metadata { get; set; }
    public SessionAggregatesData? Aggregates { get; set; }
    public DataQualityData? DataQuality { get; set; }
    
    /// <summary>
    /// Session status: "Completed" for successful sessions, "Failed" for sessions with 0 samples.
    /// </summary>
    public string? Status { get; set; }
    
    /// <summary>
    /// Error message when Status is "Failed".
    /// </summary>
    public string? ErrorMessage { get; set; }
}

public sealed class SessionMetadataData
{
    public string? SessionId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public string? GameName { get; set; }
    public string? BuildVersion { get; set; }
    public CaptureTargetData? CaptureTarget { get; set; }
}

/// <summary>
/// Data model for deserializing CaptureTarget from summary.json.
/// </summary>
public sealed class CaptureTargetData
{
    public string? ProcessName { get; set; }
    public int? ProcessId { get; set; }
    public string? SelectionMode { get; set; }
    public string? WindowTitle { get; set; }

    /// <summary>
    /// Converts this data object to a CaptureTarget model instance.
    /// </summary>
    public ZykeMark.Core.Models.CaptureTarget ToCaptureTarget()
    {
        return new ZykeMark.Core.Models.CaptureTarget(
            ProcessName,
            ProcessId,
            SelectionMode ?? ZykeMark.Core.Models.CaptureTarget.ModeAuto,
            WindowTitle);
    }

    /// <summary>
    /// Returns a human-readable display string for the capture target.
    /// Delegates to the core CaptureTarget model to avoid duplication.
    /// </summary>
    public string ToDisplayString() => ToCaptureTarget().ToDisplayString();
}

public sealed class SessionAggregatesData
{
    public int FrameCount { get; set; }
    public double DurationMs { get; set; }
    public double AvgFps { get; set; }
    public double AvgFrameTimeMs { get; set; }
    public double P99FrameTimeMs { get; set; }
    public double OnePercentLowFps { get; set; }
    public double PointOnePercentLowFps { get; set; }
    public double? AvgCpuFrameTimeMs { get; set; }
    public double? AvgGpuFrameTimeMs { get; set; }
    
    // Telemetry aggregates (from real telemetry sampling)
    public double? AvgGpuUtilizationPercent { get; set; }
    public double? AvgVramDedicatedMB { get; set; }
    public double? AvgVramSharedMB { get; set; }
    public double? AvgCpuProcessPercent { get; set; }
    public double? AvgTopThreadCpuPercent { get; set; }
    public double? AvgRamWorkingSetMB { get; set; }
    public double? AvgRamPrivateBytesMB { get; set; }
    public double? AvgDiskReadMBps { get; set; }
}

public sealed class DataQualityData
{
    public int EtwEventsLostCount { get; set; }
    public string? EtwEventsLostRiskLevel { get; set; }
    public IReadOnlyList<string>? CaptureWarnings { get; set; }
}

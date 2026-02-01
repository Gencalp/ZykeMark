using System.Text.Json;
using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;

namespace ZykeMark.Core.Services;

public sealed class SessionManager : ISessionManager
{
    private readonly ILocalStore _localStore;
    private readonly IAggregator _aggregator;
    private readonly Func<DateTime> _utcNow;
    private SessionMetadata? _currentSession;

    public SessionManager(ILocalStore localStore, IAggregator aggregator, Func<DateTime>? utcNowProvider = null)
    {
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    public SessionMetadata StartSession(string? gameName, string? buildVersion, RunConfig? runConfig = null, CaptureTarget? captureTarget = null)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var startedAtUtc = _utcNow();

        var metadata = new SessionMetadata(
            sessionId,
            startedAtUtc,
            EndedAtUtc: null,
            DurationMs: null,
            gameName,
            buildVersion,
            runConfig,
            captureTarget);

        _localStore.CreateSession(metadata);
        _currentSession = metadata;

        return metadata;
    }

    public string StopSession(string? sessionId = null, DataQuality? dataQuality = null)
    {
        var resolvedSessionId = sessionId ?? _currentSession?.SessionId;

        if (string.IsNullOrWhiteSpace(resolvedSessionId))
        {
            throw new InvalidOperationException("No active session is available to stop.");
        }

        var metadata = _localStore.ReadMetadata(resolvedSessionId);
        var endedAtUtc = _utcNow();
        var durationMs = (long)(endedAtUtc - metadata.StartedAtUtc).TotalMilliseconds;

        var updatedMetadata = metadata with
        {
            EndedAtUtc = endedAtUtc,
            DurationMs = durationMs
        };

        _localStore.WriteMetadata(updatedMetadata);

        var chunks = _localStore.ReadChunks(resolvedSessionId);
        var samples = chunks.SelectMany(chunk => chunk.Samples).ToArray();
        
        // Determine session status based on samples collected
        // If 0 frame samples were collected, mark the session as Failed
        string sessionStatus;
        string? errorMessage = null;
        
        SessionAggregates aggregates;
        if (samples.Length == 0)
        {
            sessionStatus = "Failed";
            errorMessage = BuildDetailedErrorMessage(resolvedSessionId);
            aggregates = new SessionAggregates(
                FrameCount: 0,
                DurationMs: 0,
                AvgFps: 0,
                AvgFrameTimeMs: 0,
                P99FrameTimeMs: 0,
                OnePercentLowFps: 0,
                PointOnePercentLowFps: 0,
                AvgCpuFrameTimeMs: null,
                AvgGpuFrameTimeMs: null);
        }
        else
        {
            sessionStatus = "Completed";
            aggregates = _aggregator.Aggregate(samples);
        }

        // Use provided data quality or create empty/unknown if not provided
        var effectiveDataQuality = dataQuality ?? DataQuality.Empty;

        var summaryPayload = new
        {
            metadata = updatedMetadata,
            aggregates,
            status = sessionStatus,
            errorMessage = errorMessage,
            dataQuality = new
            {
                EtwEventsLostCount = effectiveDataQuality.EtwEventsLostCount,
                EtwEventsLostRiskLevel = effectiveDataQuality.EtwEventsLostRiskLevel.ToString(),
                CaptureWarnings = effectiveDataQuality.CaptureWarnings
            }
        };

        var summaryPath = _localStore.WriteSummary(resolvedSessionId, summaryPayload);
        _currentSession = updatedMetadata;

        return summaryPath;
    }

    public SessionMetadata? GetCurrentSession() => _currentSession;

    /// <summary>
    /// Builds a detailed error message when FrameCount==0 by reading capture_diagnostics.json if available.
    /// </summary>
    private string BuildDetailedErrorMessage(string sessionId)
    {
        var defaultMessage = "No frame samples were captured. Check that the target process is running and PresentMon has access permissions.";
        
        try
        {
            var sessionFolder = _localStore.GetSessionFolder(sessionId);
            var diagnosticsPath = Path.Combine(sessionFolder, "capture_diagnostics.json");
            
            if (!File.Exists(diagnosticsPath))
            {
                return $"{defaultMessage} (No diagnostics file found - capture may not have been attempted)";
            }
            
            var json = File.ReadAllText(diagnosticsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            var messages = new List<string>();
            
            // Check if PresentMon started
            if (!root.TryGetProperty("ExitCode", out var exitCodeProp) || exitCodeProp.ValueKind == JsonValueKind.Null)
            {
                if (root.TryGetProperty("FailureReason", out var failureReasonProp))
                {
                    messages.Add($"PresentMon failed to start: {failureReasonProp.GetString()}");
                }
                else
                {
                    messages.Add("PresentMon failed to start");
                }
            }
            else
            {
                var exitCode = exitCodeProp.GetInt32();
                if (exitCode != 0)
                {
                    messages.Add($"PresentMon exited with code {exitCode}");
                }
            }
            
            // Check CSV file status
            if (root.TryGetProperty("OutputCsvExists", out var csvExistsProp) && csvExistsProp.ValueKind != JsonValueKind.Null)
            {
                if (!csvExistsProp.GetBoolean())
                {
                    messages.Add("CSV output file was not created");
                }
                else if (root.TryGetProperty("FileSizeBytes", out var sizeProp) && sizeProp.GetInt64() == 0)
                {
                    messages.Add("CSV output file is empty (0 bytes)");
                }
            }
            
            // Check parsed row count
            if (root.TryGetProperty("ParsedRowCount", out var rowCountProp) && rowCountProp.ValueKind != JsonValueKind.Null)
            {
                var rowCount = rowCountProp.GetInt32();
                if (rowCount == 0)
                {
                    messages.Add("CSV contains header but no data rows");
                }
            }
            
            // Include failure reason if present
            if (root.TryGetProperty("FailureReason", out var reasonProp) && reasonProp.ValueKind != JsonValueKind.Null)
            {
                var reason = reasonProp.GetString();
                if (!string.IsNullOrWhiteSpace(reason) && !messages.Any(m => m.Contains(reason, StringComparison.OrdinalIgnoreCase)))
                {
                    messages.Add(reason);
                }
            }
            
            if (messages.Count == 0)
            {
                return $"{defaultMessage} See capture_diagnostics.json for details.";
            }
            
            messages.Add("See capture_diagnostics.json in session folder for full details");
            return string.Join(". ", messages);
        }
        catch
        {
            // If we can't read the diagnostics file, return the default message
            return defaultMessage;
        }
    }
}

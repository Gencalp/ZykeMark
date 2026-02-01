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
            errorMessage = "No frame samples were captured. Check that the target process is running and PresentMon has access permissions.";
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
}

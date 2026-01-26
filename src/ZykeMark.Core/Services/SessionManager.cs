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

    public SessionMetadata StartSession(string? gameName, string? buildVersion, RunConfig? runConfig = null)
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
            runConfig);

        _localStore.CreateSession(metadata);
        _currentSession = metadata;

        return metadata;
    }

    public string StopSession(string? sessionId = null)
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
        var aggregates = _aggregator.Aggregate(samples);

        var summaryPayload = new
        {
            metadata = updatedMetadata,
            aggregates
        };

        var summaryPath = _localStore.WriteSummary(resolvedSessionId, summaryPayload);
        _currentSession = updatedMetadata;

        return summaryPath;
    }

    public SessionMetadata? GetCurrentSession() => _currentSession;
}

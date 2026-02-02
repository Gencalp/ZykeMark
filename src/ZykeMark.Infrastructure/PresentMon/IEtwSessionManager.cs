namespace ZykeMark.Infrastructure.PresentMon;

/// <summary>
/// Interface for managing ETW (Event Tracing for Windows) sessions.
/// Used to detect and clean up stale trace sessions that may interfere with PresentMon.
/// </summary>
public interface IEtwSessionManager
{
    /// <summary>
    /// Lists all active ETW trace sessions matching the specified prefix.
    /// </summary>
    /// <param name="prefix">Session name prefix to filter by (e.g., "ZykeMark_")</param>
    /// <returns>List of matching session names</returns>
    Task<EtwSessionListResult> ListSessionsAsync(string prefix);

    /// <summary>
    /// Stops an active ETW trace session by name.
    /// </summary>
    /// <param name="sessionName">Name of the session to stop</param>
    /// <returns>Result indicating success or failure</returns>
    Task<EtwSessionStopResult> StopSessionAsync(string sessionName);

    /// <summary>
    /// Cleans up all stale ETW trace sessions matching the specified prefix.
    /// This should be called before starting a new PresentMon capture.
    /// </summary>
    /// <param name="prefix">Session name prefix to clean up (e.g., "ZykeMark_")</param>
    /// <returns>Result containing cleanup details</returns>
    Task<EtwCleanupResult> CleanupStaleSessionsAsync(string prefix);
}

/// <summary>
/// Result of listing ETW sessions.
/// </summary>
public sealed record EtwSessionListResult(
    bool Success,
    IReadOnlyList<string> SessionNames,
    string? RawOutput,
    string? ErrorMessage);

/// <summary>
/// Result of stopping an ETW session.
/// </summary>
public sealed record EtwSessionStopResult(
    bool Success,
    string SessionName,
    string? RawOutput,
    string? ErrorMessage);

/// <summary>
/// Result of cleaning up stale ETW sessions.
/// </summary>
public sealed record EtwCleanupResult(
    bool Success,
    IReadOnlyList<string> StoppedSessions,
    IReadOnlyList<string> FailedSessions,
    string? ErrorMessage)
{
    /// <summary>
    /// Total number of sessions that were attempted to be cleaned up.
    /// </summary>
    public int TotalAttempted => StoppedSessions.Count + FailedSessions.Count;

    /// <summary>
    /// Whether any sessions were cleaned up.
    /// </summary>
    public bool HadStaleSessions => TotalAttempted > 0;
}

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace ZykeMark.Infrastructure.PresentMon;

/// <summary>
/// Windows implementation of IEtwSessionManager that uses logman.exe to manage ETW trace sessions.
/// logman.exe is a built-in Windows tool for managing performance counters and event trace sessions.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsEtwSessionManager : IEtwSessionManager
{
    private const string LogmanPath = @"C:\Windows\System32\logman.exe";
    private const int DefaultTimeoutMs = 10000; // 10 seconds

    private readonly Action<string>? _logger;

    public WindowsEtwSessionManager(Action<string>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EtwSessionListResult> ListSessionsAsync(string prefix)
    {
        try
        {
            var (exitCode, stdout, stderr) = await RunLogmanAsync("query -ets").ConfigureAwait(false);

            if (exitCode != 0)
            {
                Log($"[ETW] logman query failed with exit code {exitCode}. stderr: {stderr}");
                return new EtwSessionListResult(
                    Success: false,
                    SessionNames: Array.Empty<string>(),
                    RawOutput: stdout,
                    ErrorMessage: $"logman query -ets failed with exit code {exitCode}: {stderr}");
            }

            var sessions = ParseSessionNames(stdout, prefix);
            Log($"[ETW] Found {sessions.Count} session(s) matching prefix '{prefix}'");

            return new EtwSessionListResult(
                Success: true,
                SessionNames: sessions,
                RawOutput: stdout,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            Log($"[ETW] Exception listing sessions: {ex.Message}");
            return new EtwSessionListResult(
                Success: false,
                SessionNames: Array.Empty<string>(),
                RawOutput: null,
                ErrorMessage: ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<EtwSessionStopResult> StopSessionAsync(string sessionName)
    {
        try
        {
            Log($"[ETW] Stopping session: {sessionName}");
            var (exitCode, stdout, stderr) = await RunLogmanAsync($"stop \"{sessionName}\" -ets").ConfigureAwait(false);

            if (exitCode != 0)
            {
                Log($"[ETW] Failed to stop session '{sessionName}'. Exit code: {exitCode}, stderr: {stderr}");
                return new EtwSessionStopResult(
                    Success: false,
                    SessionName: sessionName,
                    RawOutput: stdout + stderr,
                    ErrorMessage: $"logman stop failed with exit code {exitCode}: {stderr}");
            }

            Log($"[ETW] Successfully stopped session: {sessionName}");
            return new EtwSessionStopResult(
                Success: true,
                SessionName: sessionName,
                RawOutput: stdout,
                ErrorMessage: null);
        }
        catch (Exception ex)
        {
            Log($"[ETW] Exception stopping session '{sessionName}': {ex.Message}");
            return new EtwSessionStopResult(
                Success: false,
                SessionName: sessionName,
                RawOutput: null,
                ErrorMessage: ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<EtwCleanupResult> CleanupStaleSessionsAsync(string prefix)
    {
        Log($"[ETW] Starting cleanup of stale sessions with prefix '{prefix}'");

        var listResult = await ListSessionsAsync(prefix).ConfigureAwait(false);
        if (!listResult.Success)
        {
            Log($"[ETW] Cleanup failed: could not list sessions. Error: {listResult.ErrorMessage}");
            return new EtwCleanupResult(
                Success: false,
                StoppedSessions: Array.Empty<string>(),
                FailedSessions: Array.Empty<string>(),
                ErrorMessage: $"Failed to list sessions: {listResult.ErrorMessage}");
        }

        if (listResult.SessionNames.Count == 0)
        {
            Log($"[ETW] No stale sessions found with prefix '{prefix}'");
            return new EtwCleanupResult(
                Success: true,
                StoppedSessions: Array.Empty<string>(),
                FailedSessions: Array.Empty<string>(),
                ErrorMessage: null);
        }

        var stoppedSessions = new List<string>();
        var failedSessions = new List<string>();

        foreach (var sessionName in listResult.SessionNames)
        {
            var stopResult = await StopSessionAsync(sessionName).ConfigureAwait(false);
            if (stopResult.Success)
            {
                stoppedSessions.Add(sessionName);
            }
            else
            {
                failedSessions.Add(sessionName);
            }
        }

        var success = failedSessions.Count == 0;
        Log($"[ETW] Cleanup complete. Stopped: {stoppedSessions.Count}, Failed: {failedSessions.Count}");

        return new EtwCleanupResult(
            Success: success,
            StoppedSessions: stoppedSessions,
            FailedSessions: failedSessions,
            ErrorMessage: success ? null : $"Failed to stop {failedSessions.Count} session(s)");
    }

    /// <summary>
    /// Runs logman.exe with the specified arguments and returns the result.
    /// </summary>
    private async Task<(int ExitCode, string StdOut, string StdErr)> RunLogmanAsync(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = LogmanPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Log($"[ETW] Running: {LogmanPath} {arguments}");

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(DefaultTimeoutMs);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore kill failures
            }
            return (-1, stdout.ToString(), "Process timed out after " + DefaultTimeoutMs + "ms");
        }

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Parses session names from logman query output, filtering by prefix.
    /// </summary>
    private List<string> ParseSessionNames(string logmanOutput, string prefix)
    {
        var sessions = new List<string>();

        // logman query -ets output format:
        // Data Collector Set                      Type                          Status
        // -------------------------------------------------------------------------------
        // EventLog-Application                    Trace                         Running
        // ZykeMark_abc123                         Trace                         Running
        // ...

        var lines = logmanOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        // Skip header lines (first two lines are column headers and separator)
        foreach (var line in lines.Skip(2))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            // Extract the session name (first column, space-separated)
            // The name ends where consecutive spaces or tab starts (column separator)
            var sessionName = ExtractSessionName(trimmedLine);
            
            if (!string.IsNullOrWhiteSpace(sessionName) && 
                sessionName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                sessions.Add(sessionName);
                Log($"[ETW] Found matching session: {sessionName}");
            }
        }

        return sessions;
    }

    /// <summary>
    /// Extracts the session name from a logman output line.
    /// </summary>
    private static string ExtractSessionName(string line)
    {
        // logman uses columns separated by multiple spaces
        // The session name is the first column
        // Pattern: session names don't contain tabs or multiple consecutive spaces
        
        // Find where the first column ends (multiple spaces or tab indicate column boundary)
        var match = Regex.Match(line, @"^(\S+(?:\s(?!\s)\S*)*)");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}

namespace ZykeMark.Core.Models;

/// <summary>
/// Represents the target process for frame capture.
/// Persisted in session metadata and summary.json for traceability.
/// </summary>
public sealed record CaptureTarget(
    string? ProcessName,
    int? ProcessId,
    string SelectionMode,
    string? WindowTitle = null)
{
    /// <summary>
    /// Selection mode indicating how the target was chosen:
    /// "pid" - User explicitly selected by process ID
    /// "name" - User specified by process name
    /// "auto" - Automatically detected or default
    /// </summary>
    public const string ModePid = "pid";
    public const string ModeName = "name";
    public const string ModeAuto = "auto";

    /// <summary>
    /// Known multi-process applications that require capturing by process name instead of PID.
    /// These applications use separate processes for GPU rendering (e.g., browsers, Electron apps).
    /// When targeting these by PID, the main process may not produce frames - the GPU process does.
    /// </summary>
    private static readonly HashSet<string> MultiProcessApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        // Browsers
        "msedge",
        "msedge.exe",
        "chrome",
        "chrome.exe",
        "firefox",
        "firefox.exe",
        "brave",
        "brave.exe",
        "opera",
        "opera.exe",
        "vivaldi",
        "vivaldi.exe",
        
        // Electron-based applications
        "slack",
        "slack.exe",
        "discord",
        "discord.exe",
        "code",
        "code.exe",
        "teams",
        "teams.exe",
        "spotify",
        "spotify.exe"
    };

    /// <summary>
    /// Returns true if the target process is a known multi-process application
    /// that requires capturing by process name instead of process ID.
    /// </summary>
    public bool IsMultiProcessApplication => 
        !string.IsNullOrWhiteSpace(ProcessName) && 
        MultiProcessApplications.Contains(ProcessName);

    /// <summary>
    /// Returns a human-readable display string for the capture target.
    /// </summary>
    public string ToDisplayString()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(ProcessName))
        {
            parts.Add(ProcessName);
        }

        if (ProcessId.HasValue)
        {
            parts.Add($"PID {ProcessId}");
        }

        if (!string.IsNullOrWhiteSpace(WindowTitle))
        {
            parts.Add($"\"{WindowTitle}\"");
        }

        if (parts.Count == 0)
        {
            return "Unknown";
        }

        var result = string.Join(" / ", parts);

        // Append selection mode in parentheses
        var modeDisplay = SelectionMode switch
        {
            ModePid => "by PID",
            ModeName => "by name",
            ModeAuto => "auto",
            _ => SelectionMode
        };

        return $"{result} ({modeDisplay})";
    }
}

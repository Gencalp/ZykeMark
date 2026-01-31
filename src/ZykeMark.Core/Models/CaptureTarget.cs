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

namespace ZykeMark.Infrastructure.PresentMon;

/// <summary>
/// Represents the result of a PresentMon run.
/// </summary>
/// <param name="CsvPath">The absolute path to the CSV output file (may be discovered via multi_csv fallback).</param>
/// <param name="ExitCode">The exit code of the PresentMon process.</param>
/// <param name="StdOut">The standard output captured from PresentMon.</param>
/// <param name="StdErr">The standard error captured from PresentMon.</param>
/// <param name="EtwEventsLostCount">The number of ETW events lost during capture, if detected. Null if not detected.</param>
/// <param name="RawWarnings">List of raw warning strings detected in stdout/stderr.</param>
/// <param name="Diagnostics">Capture diagnostics for debugging when issues occur.</param>
public sealed record PresentMonRunResult(
    string? CsvPath,
    int ExitCode,
    string StdOut,
    string StdErr,
    int? EtwEventsLostCount = null,
    IReadOnlyList<string>? RawWarnings = null,
    CaptureDiagnostics? Diagnostics = null);

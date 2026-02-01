namespace ZykeMark.Infrastructure.PresentMon;

/// <summary>
/// Represents the result of attempting to resolve the PresentMon executable path.
/// </summary>
public sealed record PresentMonPathResolverResult(
    /// <summary>
    /// The resolved absolute path to PresentMon.exe, or null if not found.
    /// </summary>
    string? ResolvedAbsolutePath,

    /// <summary>
    /// The reason resolution failed, or null if successful.
    /// </summary>
    string? FailureReason,

    /// <summary>
    /// Describes the source where PresentMon was found (e.g., "Settings", "App Directory", "PATH", etc.).
    /// </summary>
    string? Source = null)
{
    /// <summary>
    /// Gets whether PresentMon was successfully found.
    /// </summary>
    public bool IsFound => !string.IsNullOrWhiteSpace(ResolvedAbsolutePath);
}

/// <summary>
/// Service for resolving the PresentMon executable path from various sources.
/// </summary>
public interface IPresentMonPathResolver
{
    /// <summary>
    /// Resolves the PresentMon executable path using the following priority:
    /// 1. User settings path (if non-empty and exists)
    /// 2. App directory (alongside ZykeMark.App.Desktop.exe)
    /// 3. %LocalAppData%\ZykeMark\tools\presentmon\ folder
    /// 4. PATH lookup using where.exe
    /// 5. User Downloads folder (limited scan)
    /// 
    /// If found in Downloads, the file is copied to the tools folder for stability.
    /// </summary>
    /// <param name="userSettingsPath">The path from user settings, or null/empty if not configured.</param>
    /// <returns>A result containing the resolved path or failure reason.</returns>
    PresentMonPathResolverResult Resolve(string? userSettingsPath);
}

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ZykeMark.Infrastructure.PresentMon;

/// <summary>
/// Implementation of IPresentMonPathResolver that searches for PresentMon in multiple locations.
/// </summary>
public sealed class PresentMonPathResolver : IPresentMonPathResolver
{
    private const string PresentMonExeName = "PresentMon.exe";
    private const string VersionedExePattern = "PresentMon-*-x64.exe";
    private const int DownloadsScanMaxFiles = 200;
    private const int DownloadsScanMaxDepth = 2;

    private readonly Action<string>? _logger;
    private readonly string? _appDirectory;

    public PresentMonPathResolver(Action<string>? logger = null, string? appDirectory = null)
    {
        _logger = logger;
        _appDirectory = appDirectory ?? AppContext.BaseDirectory;
    }

    public PresentMonPathResolverResult Resolve(string? userSettingsPath)
    {
        // Priority 1: User settings path (if non-empty and exists)
        if (!string.IsNullOrWhiteSpace(userSettingsPath))
        {
            Log($"[PathResolver] Checking user settings path: {userSettingsPath}");
            if (File.Exists(userSettingsPath))
            {
                Log($"[PathResolver] Found at user settings path");
                return new PresentMonPathResolverResult(
                    Path.GetFullPath(userSettingsPath),
                    null,
                    "User Settings");
            }

            Log($"[PathResolver] User settings path does not exist");
            // Don't fail yet - try other locations
        }

        // Priority 2: App directory (alongside ZykeMark.App.Desktop.exe)
        var appDirResult = SearchInDirectory(_appDirectory, "App Directory");
        if (appDirResult.IsFound)
        {
            return appDirResult;
        }

        // Priority 3: %LocalAppData%\ZykeMark\tools\presentmon\ folder
        var toolsFolder = GetToolsFolder();
        var toolsResult = SearchInDirectory(toolsFolder, "Tools Folder");
        if (toolsResult.IsFound)
        {
            return toolsResult;
        }

        // Priority 4: PATH lookup using where.exe
        var pathResult = SearchInPath();
        if (pathResult.IsFound)
        {
            return pathResult;
        }

        // Priority 5: User Downloads folder (limited scan)
        var downloadsResult = SearchInDownloads();
        if (downloadsResult.IsFound)
        {
            // Copy to tools folder for stability
            var stablePath = CopyToToolsFolder(downloadsResult.ResolvedAbsolutePath!);
            if (stablePath != null)
            {
                Log($"[PathResolver] Copied from Downloads to: {stablePath}");
                return new PresentMonPathResolverResult(stablePath, null, "Downloads (copied to Tools)");
            }

            // If copy failed, still return the Downloads path
            return downloadsResult;
        }

        // Not found anywhere
        var failureReason = BuildFailureReason(userSettingsPath);
        Log($"[PathResolver] Not found. Reason: {failureReason}");
        return new PresentMonPathResolverResult(null, failureReason, null);
    }

    private PresentMonPathResolverResult SearchInDirectory(string? directory, string source)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Log($"[PathResolver] {source} directory does not exist: {directory ?? "(null)"}");
            return new PresentMonPathResolverResult(null, null, null);
        }

        Log($"[PathResolver] Searching in {source}: {directory}");

        // First, check for exact PresentMon.exe
        var exactPath = Path.Combine(directory, PresentMonExeName);
        if (File.Exists(exactPath))
        {
            Log($"[PathResolver] Found {PresentMonExeName} in {source}");
            return new PresentMonPathResolverResult(exactPath, null, source);
        }

        // Then check for versioned executables (PresentMon-*-x64.exe)
        var versionedPath = FindHighestVersionedExe(directory);
        if (versionedPath != null)
        {
            Log($"[PathResolver] Found versioned exe in {source}: {Path.GetFileName(versionedPath)}");
            return new PresentMonPathResolverResult(versionedPath, null, source);
        }

        return new PresentMonPathResolverResult(null, null, null);
    }

    private string? FindHighestVersionedExe(string directory)
    {
        try
        {
            var pattern = new Regex(@"^PresentMon-([0-9]+\.[0-9]+\.[0-9]+)-x64\.exe$", RegexOptions.IgnoreCase);
            var matches = new List<(string Path, Version Version)>();

            foreach (var file in Directory.GetFiles(directory, "PresentMon-*-x64.exe"))
            {
                var fileName = Path.GetFileName(file);
                var match = pattern.Match(fileName);
                if (match.Success && Version.TryParse(match.Groups[1].Value, out var version))
                {
                    matches.Add((file, version));
                }
            }

            if (matches.Count == 0)
            {
                // Try simpler pattern without version parsing
                var simpleMatches = Directory.GetFiles(directory, "PresentMon-*-x64.exe");
                if (simpleMatches.Length > 0)
                {
                    // Return most recently modified
                    return simpleMatches.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
                }
                return null;
            }

            return matches.OrderByDescending(m => m.Version).First().Path;
        }
        catch (Exception ex)
        {
            Log($"[PathResolver] Error searching for versioned executables: {ex.Message}");
            return null;
        }
    }

    private PresentMonPathResolverResult SearchInPath()
    {
        Log("[PathResolver] Searching in PATH using where.exe");

        try
        {
            // Try PresentMon.exe first
            var result = RunWhereExe(PresentMonExeName);
            if (!string.IsNullOrWhiteSpace(result) && File.Exists(result))
            {
                Log($"[PathResolver] Found via PATH: {result}");
                return new PresentMonPathResolverResult(result, null, "PATH");
            }

            // Try versioned pattern (where.exe doesn't support wildcards well, so we skip this)
        }
        catch (Exception ex)
        {
            Log($"[PathResolver] Error searching PATH: {ex.Message}");
        }

        return new PresentMonPathResolverResult(null, null, null);
    }

    private string? RunWhereExe(string exeName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = exeName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(5000); // 5 second timeout

            return output?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private PresentMonPathResolverResult SearchInDownloads()
    {
        var downloadsFolder = GetDownloadsFolder();
        if (string.IsNullOrWhiteSpace(downloadsFolder) || !Directory.Exists(downloadsFolder))
        {
            Log($"[PathResolver] Downloads folder not accessible: {downloadsFolder ?? "(null)"}");
            return new PresentMonPathResolverResult(null, null, null);
        }

        Log($"[PathResolver] Searching in Downloads (limited scan): {downloadsFolder}");

        try
        {
            var filesChecked = 0;
            var candidates = new List<(string Path, Version? Version, DateTime LastWrite)>();

            // Enumerate files with depth limit
            foreach (var file in EnumerateFilesWithDepthLimit(downloadsFolder, DownloadsScanMaxDepth))
            {
                filesChecked++;
                if (filesChecked > DownloadsScanMaxFiles)
                {
                    Log($"[PathResolver] Downloads scan reached file limit ({DownloadsScanMaxFiles})");
                    break;
                }

                var fileName = Path.GetFileName(file);

                // Check for versioned pattern first (PresentMon-*-x64.exe)
                if (fileName.StartsWith("PresentMon-", StringComparison.OrdinalIgnoreCase) &&
                    fileName.EndsWith("-x64.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var version = ExtractVersion(fileName);
                    candidates.Add((file, version, File.GetLastWriteTimeUtc(file)));
                }
                // Check for exact name
                else if (fileName.Equals(PresentMonExeName, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add((file, null, File.GetLastWriteTimeUtc(file)));
                }
            }

            if (candidates.Count == 0)
            {
                Log("[PathResolver] No PresentMon executables found in Downloads");
                return new PresentMonPathResolverResult(null, null, null);
            }

            // Prefer versioned, then by highest version, then by most recent
            var best = candidates
                .OrderByDescending(c => c.Version != null)
                .ThenByDescending(c => c.Version)
                .ThenByDescending(c => c.LastWrite)
                .First();

            Log($"[PathResolver] Found in Downloads: {best.Path}");
            return new PresentMonPathResolverResult(best.Path, null, "Downloads");
        }
        catch (Exception ex)
        {
            Log($"[PathResolver] Error searching Downloads: {ex.Message}");
            return new PresentMonPathResolverResult(null, null, null);
        }
    }

    private static IEnumerable<string> EnumerateFilesWithDepthLimit(string rootDir, int maxDepth)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((rootDir, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.exe");
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            if (depth < maxDepth)
            {
                IEnumerable<string> subdirs;
                try
                {
                    subdirs = Directory.EnumerateDirectories(dir);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var subdir in subdirs)
                {
                    stack.Push((subdir, depth + 1));
                }
            }
        }
    }

    private static Version? ExtractVersion(string fileName)
    {
        var pattern = new Regex(@"^PresentMon-([0-9]+\.[0-9]+\.[0-9]+)-x64\.exe$", RegexOptions.IgnoreCase);
        var match = pattern.Match(fileName);
        if (match.Success && Version.TryParse(match.Groups[1].Value, out var version))
        {
            return version;
        }
        return null;
    }

    private string? CopyToToolsFolder(string sourcePath)
    {
        try
        {
            var toolsFolder = GetToolsFolder();
            Directory.CreateDirectory(toolsFolder);

            var targetPath = Path.Combine(toolsFolder, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, targetPath, overwrite: true);

            Log($"[PathResolver] Copied PresentMon to tools folder: {targetPath}");
            return targetPath;
        }
        catch (Exception ex)
        {
            Log($"[PathResolver] Failed to copy to tools folder: {ex.Message}");
            return null;
        }
    }

    private static string GetToolsFolder()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZykeMark",
            "tools",
            "presentmon");
    }

    private static string GetDownloadsFolder()
    {
        // Try to get the actual Downloads folder path
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return string.Empty;
        }

        return Path.Combine(userProfile, "Downloads");
    }

    private string BuildFailureReason(string? userSettingsPath)
    {
        var locations = new List<string>();

        if (!string.IsNullOrWhiteSpace(userSettingsPath))
        {
            locations.Add($"User Settings: {userSettingsPath} (not found)");
        }

        if (!string.IsNullOrWhiteSpace(_appDirectory))
        {
            locations.Add($"App Directory: {_appDirectory}");
        }

        locations.Add($"Tools Folder: {GetToolsFolder()}");
        locations.Add("System PATH");
        locations.Add($"Downloads: {GetDownloadsFolder()} (scanned {DownloadsScanMaxFiles} files, depth {DownloadsScanMaxDepth})");

        return $"PresentMon.exe not found. Searched locations:\n• {string.Join("\n• ", locations)}\n\n" +
               $"Please download PresentMon from https://github.com/GameTechDev/PresentMon/releases " +
               $"and either:\n• Place it in {GetToolsFolder()}\n• Or configure the path in Settings.";
    }

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}

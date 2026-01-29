using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ZykeMark.Infrastructure.PresentMon;

public sealed class PresentMonRunner : IPresentMonRunner
{
    private readonly Action<string>? _logger;

    public PresentMonRunner(Action<string>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PresentMonRunResult> RunToFileAsync(
        PresentMonRunOptions options,
        CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.DurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DurationSeconds), "Duration must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.ProcessName) && options.ProcessId is null)
        {
            throw new ArgumentException("Process name or process ID must be specified.", nameof(options));
        }

        // Pre-check: Validate that process exists before running PresentMon
        ValidateProcessExists(options);

        var exePath = ResolveExecutablePath(options.PresentMonPath);
        var useFileOutput = !string.IsNullOrWhiteSpace(options.SessionFolder) && !string.IsNullOrWhiteSpace(options.SessionId);
        var csvPath = useFileOutput ? Path.Combine(options.SessionFolder!, "presentmon.csv") : null;
        var arguments = BuildArguments(options, csvPath);
        var workingDirectory = useFileOutput ? options.SessionFolder : null;

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        Log($"[PresentMon] Starting: {exePath} {arguments}");
        Log($"[PresentMon] WorkingDirectory: {workingDirectory ?? "(current)"}");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stderrBuilder = new StringBuilder();
        var stdoutBuilder = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start PresentMon.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start PresentMon. Ensure PresentMon.exe is available and you have permission to run it.",
                ex);
        }

        // Capture stderr asynchronously
        var stderrTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is not null)
                {
                    stderrBuilder.AppendLine(line);
                    Log($"[PresentMon stderr] {line}");
                }
            }
        }, cancellationToken);

        // Capture stdout asynchronously
        var stdoutTask = Task.Run(async () =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line is not null)
                {
                    stdoutBuilder.AppendLine(line);
                }
            }
        }, cancellationToken);

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }

        var exitCode = process.ExitCode;
        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        Log($"[PresentMon] Exit code: {exitCode}");
        Log($"[PresentMon] stdout captured: {stdout.Length} chars");
        Log($"[PresentMon] stderr captured: {stderr.Length} chars");

        // Discover and validate output file if file mode was used
        string? actualCsvPath = null;
        if (useFileOutput && csvPath is not null)
        {
            actualCsvPath = DiscoverAndValidateCsvOutput(csvPath, exePath, exitCode, stdout, stderr);
        }

        return new PresentMonRunResult(actualCsvPath, exitCode, stdout, stderr);
    }

    /// <summary>
    /// Discovers the CSV output file and returns its path. Throws if file not found or invalid.
    /// </summary>
    private string DiscoverAndValidateCsvOutput(string csvPath, string presentMonExePath, int exitCode, string stdout, string stderr)
    {
        // Wait for file system to finish flushing (PresentMon may take time to finalize output)
        Thread.Sleep(250);

        const int maxRetries = 5;
        const int maxLinesToRead = 50;

        // Get the directory of the PresentMon executable as a fallback search location
        var exeDirectory = Path.GetDirectoryName(presentMonExePath);

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var actualCsvPath = FindCsvOutputFile(csvPath, exeDirectory);
            if (actualCsvPath is null)
            {
                if (attempt < maxRetries - 1)
                {
                    var delay = 200 * (attempt + 1);
                    Log($"[PresentMon] CSV not found on attempt {attempt + 1}, waiting {delay}ms...");
                    Thread.Sleep(delay);
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(csvPath);
                var multiCsvPattern = $"{baseName}-*.csv";
                var expectedDir = Path.GetDirectoryName(csvPath) ?? Directory.GetCurrentDirectory();
                var searchLocations = string.IsNullOrEmpty(exeDirectory)
                    ? $"directory '{expectedDir}'"
                    : $"directories '{expectedDir}' and executable directory '{exeDirectory}'";
                throw new InvalidOperationException(
                    $"PresentMon did not create output file. Searched for multi_csv pattern '{multiCsvPattern}' and default pattern 'PresentMon-*.csv' in {searchLocations}. Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
            }

            try
            {
                Log($"[PresentMon] Selected CSV path: {actualCsvPath}");

                // If the file was found in a different directory, copy it to the expected location
                var expectedDirectory = Path.GetDirectoryName(csvPath);
                var actualDirectory = Path.GetDirectoryName(actualCsvPath);
                if (!string.IsNullOrEmpty(expectedDirectory) &&
                    !string.IsNullOrEmpty(actualDirectory) &&
                    !string.Equals(expectedDirectory, actualDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var targetPath = Path.Combine(expectedDirectory, Path.GetFileName(actualCsvPath));
                    Log($"[PresentMon] Copying CSV from '{actualCsvPath}' to '{targetPath}'");
                    File.Copy(actualCsvPath, targetPath, overwrite: true);
                    actualCsvPath = targetPath;
                }

                var lines = File.ReadAllLines(actualCsvPath);

                // Log first 3 lines for debugging
                var previewLines = lines.Take(3).ToArray();
                for (var i = 0; i < previewLines.Length; i++)
                {
                    Log($"[PresentMon] CSV line {i}: {previewLines[i]}");
                }

                // Discover the actual CSV header (skip preamble like "Started recording.")
                var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines.Take(maxLinesToRead), out var headerLineIndex);

                if (headerLine is null)
                {
                    var firstLines = string.Join(Environment.NewLine, lines.Take(5));
                    throw new InvalidOperationException(
                        $"PresentMon output file '{actualCsvPath}' does not contain a valid CSV header. " +
                        $"Expected header with 'Application,ProcessID,...'. First lines:\n{firstLines}\n" +
                        $"Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
                }

                Log($"[PresentMon] CSV header found at line {headerLineIndex}: {headerLine}");

                // Count data rows (lines after the header)
                var dataRows = lines.Length - headerLineIndex - 1;
                Log($"[PresentMon] CSV output: {actualCsvPath}, {dataRows} data rows (header at line {headerLineIndex})");

                if (dataRows <= 0)
                {
                    throw new InvalidOperationException(
                        $"PresentMon output file '{actualCsvPath}' contains only header (no data rows). Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
                }

                return actualCsvPath; // Success - return the discovered path
            }
            catch (IOException ex) when (attempt < maxRetries - 1)
            {
                var delay = 200 * (attempt + 1);
                Log($"[PresentMon] IOException on attempt {attempt + 1}: {ex.Message}, waiting {delay}ms...");
                Thread.Sleep(delay);
            }
        }

        throw new InvalidOperationException($"Failed to discover CSV output file after retries. Expected path: {csvPath}");
    }

    public async IAsyncEnumerable<string> RunAsync(
        PresentMonRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (options.DurationSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DurationSeconds), "Duration must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.ProcessName) && options.ProcessId is null)
        {
            throw new ArgumentException("Process name or process ID must be specified.", nameof(options));
        }

        // Pre-check: Validate that process exists before running PresentMon
        ValidateProcessExists(options);

        var exePath = ResolveExecutablePath(options.PresentMonPath);
        var useFileOutput = !string.IsNullOrWhiteSpace(options.SessionFolder) && !string.IsNullOrWhiteSpace(options.SessionId);
        var csvPath = useFileOutput ? Path.Combine(options.SessionFolder!, "presentmon.csv") : null;
        var arguments = BuildArguments(options, csvPath);
        var workingDirectory = useFileOutput ? options.SessionFolder : null;

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        Log($"[PresentMon] Starting: {exePath} {arguments}");
        Log($"[PresentMon] WorkingDirectory: {workingDirectory ?? "(current)"}");

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stderrBuilder = new StringBuilder();
        var stdoutBuilder = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Failed to start PresentMon.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start PresentMon. Ensure PresentMon.exe is available and you have permission to run it.",
                ex);
        }

        // Capture stderr asynchronously
        var stderrTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line is not null)
                {
                    stderrBuilder.AppendLine(line);
                    Log($"[PresentMon stderr] {line}");
                }
            }
        }, cancellationToken);

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        // Read stdout
        while (!process.StandardOutput.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            stdoutBuilder.AppendLine(line);

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }

        var exitCode = process.ExitCode;
        Log($"[PresentMon] Exit code: {exitCode}");
        Log($"[PresentMon] stdout captured: {stdoutBuilder.Length} chars");
        Log($"[PresentMon] stderr captured: {stderrBuilder.Length} chars");

        // Validate output file if file mode was used
        if (useFileOutput && csvPath is not null)
        {
            ValidateCsvOutput(csvPath, exePath, exitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }
    }

    private void ValidateProcessExists(PresentMonRunOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ProcessName))
        {
            var processName = options.ProcessName!;
            // Remove .exe extension if present for GetProcessesByName
            if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                processName = processName[..^4];
            }

            var processes = Process.GetProcessesByName(processName);
            try
            {
                if (processes.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"No running process found with name '{options.ProcessName}'. Ensure the target application is running before starting capture.");
                }

                Log($"[PresentMon] Found {processes.Length} process(es) with name '{options.ProcessName}'");
            }
            finally
            {
                // Dispose all Process objects to free system resources
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        else if (options.ProcessId.HasValue)
        {
            try
            {
                using var process = Process.GetProcessById(options.ProcessId.Value);
                Log($"[PresentMon] Found process with PID {options.ProcessId.Value}: {process.ProcessName}");
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(
                    $"No running process found with PID {options.ProcessId.Value}. Ensure the target application is running before starting capture.");
            }
        }
    }

    private void ValidateCsvOutput(string csvPath, string presentMonExePath, int exitCode, string stdout, string stderr)
    {
        // Wait for file system to finish flushing (PresentMon may take time to finalize output)
        Thread.Sleep(250);

        const int maxRetries = 5;
        const int maxLinesToRead = 50;

        // Get the directory of the PresentMon executable as a fallback search location
        var exeDirectory = Path.GetDirectoryName(presentMonExePath);

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var actualCsvPath = FindCsvOutputFile(csvPath, exeDirectory);
            if (actualCsvPath is null)
            {
                if (attempt < maxRetries - 1)
                {
                    var delay = 200 * (attempt + 1); // Exponential backoff: 200ms, 400ms, 600ms, 800ms
                    Log($"[PresentMon] CSV not found on attempt {attempt + 1}, waiting {delay}ms...");
                    Thread.Sleep(delay);
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(csvPath);
                var multiCsvPattern = $"{baseName}-*.csv";
                var expectedDir = Path.GetDirectoryName(csvPath) ?? Directory.GetCurrentDirectory();
                var searchLocations = string.IsNullOrEmpty(exeDirectory)
                    ? $"directory '{expectedDir}'"
                    : $"directories '{expectedDir}' and executable directory '{exeDirectory}'";
                throw new InvalidOperationException(
                    $"PresentMon did not create output file. Searched for multi_csv pattern '{multiCsvPattern}' and default pattern 'PresentMon-*.csv' in {searchLocations}. Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
            }

            try
            {
                Log($"[PresentMon] Selected CSV path: {actualCsvPath}");

                // If the file was found in a different directory, copy it to the expected location
                var expectedDirectory = Path.GetDirectoryName(csvPath);
                var actualDirectory = Path.GetDirectoryName(actualCsvPath);
                if (!string.IsNullOrEmpty(expectedDirectory) &&
                    !string.IsNullOrEmpty(actualDirectory) &&
                    !string.Equals(expectedDirectory, actualDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    var targetPath = Path.Combine(expectedDirectory, Path.GetFileName(actualCsvPath));
                    Log($"[PresentMon] Copying CSV from '{actualCsvPath}' to '{targetPath}'");
                    File.Copy(actualCsvPath, targetPath, overwrite: true);
                    actualCsvPath = targetPath;
                }

                var lines = File.ReadAllLines(actualCsvPath);

                // Log first 3 lines for debugging
                var previewLines = lines.Take(3).ToArray();
                for (var i = 0; i < previewLines.Length; i++)
                {
                    Log($"[PresentMon] CSV line {i}: {previewLines[i]}");
                }

                // Discover the actual CSV header (skip preamble like "Started recording.")
                var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines.Take(maxLinesToRead), out var headerLineIndex);

                if (headerLine is null)
                {
                    var firstLines = string.Join(Environment.NewLine, lines.Take(5));
                    throw new InvalidOperationException(
                        $"PresentMon output file '{actualCsvPath}' does not contain a valid CSV header. " +
                        $"Expected header with 'Application,ProcessID,...'. First lines:\n{firstLines}\n" +
                        $"Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
                }

                Log($"[PresentMon] CSV header found at line {headerLineIndex}: {headerLine}");

                // Count data rows (lines after the header)
                var dataRows = lines.Length - headerLineIndex - 1;
                Log($"[PresentMon] CSV output: {actualCsvPath}, {dataRows} data rows (header at line {headerLineIndex})");

                if (dataRows <= 0)
                {
                    throw new InvalidOperationException(
                        $"PresentMon output file '{actualCsvPath}' contains only header (no data rows). Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
                }

                return; // Success
            }
            catch (IOException ex) when (attempt < maxRetries - 1)
            {
                var delay = 200 * (attempt + 1); // Retry on file access issues
                Log($"[PresentMon] IOException on attempt {attempt + 1}: {ex.Message}, waiting {delay}ms...");
                Thread.Sleep(delay);
            }
        }
    }

    /// <summary>
    /// Finds the CSV output file, handling both standard output, multi_csv mode, and PresentMon's default naming.
    /// Search order:
    /// 1. Exact expected path (e.g., "presentmon.csv")
    /// 2. Multi_csv pattern in expected directory: {base}-*.csv (e.g., "presentmon-msedge.exe-9112.csv")
    /// 3. PresentMon default naming in expected directory: PresentMon-*.csv (e.g., "PresentMon-2024-01-29T19-55-23.csv")
    /// 4. Multi_csv pattern in executable directory (fallback for when PresentMon writes to its own directory)
    /// 5. PresentMon default naming in executable directory (fallback)
    /// </summary>
    private string? FindCsvOutputFile(string expectedCsvPath, string? exeDirectory = null)
    {
        // First, check if the exact file exists (standard mode)
        if (File.Exists(expectedCsvPath))
        {
            Log($"[PresentMon] Found exact CSV path: {expectedCsvPath}");
            return expectedCsvPath;
        }

        // Get the expected directory
        var expectedDirectory = Path.GetDirectoryName(expectedCsvPath);

        // Handle relative paths without directory separator by using current directory
        if (string.IsNullOrEmpty(expectedDirectory))
        {
            expectedDirectory = Directory.GetCurrentDirectory();
        }

        var baseName = Path.GetFileNameWithoutExtension(expectedCsvPath);

        // Search in the expected directory first
        var result = SearchForCsvInDirectory(expectedDirectory, baseName, "expected");
        if (result is not null)
        {
            return result;
        }

        // Fallback: Search in the PresentMon executable's directory
        // This handles the case where PresentMon writes to its own directory instead of the working directory
        if (!string.IsNullOrEmpty(exeDirectory) &&
            !string.Equals(exeDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Log($"[PresentMon] Searching fallback location: executable directory '{exeDirectory}'");
            result = SearchForCsvInDirectory(exeDirectory, baseName, "executable");
            if (result is not null)
            {
                return result;
            }
        }

        Log($"[PresentMon] No CSV files found matching expected patterns in any searched location");
        return null;
    }

    /// <summary>
    /// Searches for CSV files matching PresentMon output patterns in the specified directory.
    /// </summary>
    private string? SearchForCsvInDirectory(string directory, string baseName, string locationLabel)
    {
        if (!Directory.Exists(directory))
        {
            Log($"[PresentMon] {locationLabel} directory does not exist: {directory}");
            return null;
        }

        // Log directory contents for debugging
        try
        {
            var allCsvFiles = Directory.GetFiles(directory, "*.csv");
            Log($"[PresentMon] CSV files in {locationLabel} directory: [{string.Join(", ", allCsvFiles.Select(Path.GetFileName))}]");
        }
        catch (Exception ex)
        {
            Log($"[PresentMon] Error listing {locationLabel} directory: {ex.Message}");
        }

        // Pattern 1: Multi_csv naming: {base}-{processname}-{pid}.csv
        var multiCsvPattern = $"{baseName}-*.csv";

        try
        {
            var matchingFiles = Directory.GetFiles(directory, multiCsvPattern);
            if (matchingFiles.Length > 0)
            {
                // If multiple files exist, return the most recently modified one
                var mostRecent = matchingFiles
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .First();
                Log($"[PresentMon] Found multi_csv output in {locationLabel} directory: {mostRecent}");
                return mostRecent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"[PresentMon] Error searching for multi_csv files in {locationLabel} directory: {ex.Message}");
        }

        // Pattern 2: PresentMon default naming: PresentMon-*.csv (when --output_file is ignored)
        // PresentMon creates files named "PresentMon-<ISO8601-timestamp>.csv" by default
        const string defaultPattern = "PresentMon-*.csv";

        try
        {
            var defaultFiles = Directory.GetFiles(directory, defaultPattern);
            if (defaultFiles.Length > 0)
            {
                var mostRecent = defaultFiles
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .First();
                Log($"[PresentMon] Found default-named output in {locationLabel} directory: {mostRecent}");
                return mostRecent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"[PresentMon] Error searching for default-named files in {locationLabel} directory: {ex.Message}");
        }

        return null;
    }

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }

    private static string ResolveExecutablePath(string? providedPath)
    {
        if (!string.IsNullOrWhiteSpace(providedPath))
        {
            if (!File.Exists(providedPath))
            {
                throw new FileNotFoundException("PresentMon executable not found.", providedPath);
            }

            return providedPath;
        }

        return "PresentMon.exe";
    }

    private static string BuildArguments(PresentMonRunOptions options, string? outputFile)
    {
        var parts = new List<string>();

        // If we have an output file, use file output mode; otherwise use stdout
        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            parts.Add("--output_file");
            parts.Add(outputFile);
        }
        else
        {
            parts.Add("--output_stdout");
        }

        parts.Add("--no_console_stats");
        parts.Add("--v2_metrics");

        // Add unique session name and stop existing session to prevent ETW collisions
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            parts.Add("--session_name");
            parts.Add($"ZykeMark_{options.SessionId}");
            // Only stop existing session when we have a unique session name
            parts.Add("--stop_existing_session");
        }

        parts.Add("--timed");
        parts.Add(options.DurationSeconds.ToString());
        parts.Add("--terminate_after_timed");

        if (!string.IsNullOrWhiteSpace(options.ProcessName))
        {
            parts.Add("--process_name");
            parts.Add(options.ProcessName!);
        }
        else if (options.ProcessId.HasValue)
        {
            parts.Add("--process_id");
            parts.Add(options.ProcessId.Value.ToString());
        }

        return string.Join(' ', parts.Select(QuoteIfNeeded));
    }

    private static string QuoteIfNeeded(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }
}

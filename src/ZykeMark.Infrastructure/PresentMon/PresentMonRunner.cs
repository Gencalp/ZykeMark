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
            ValidateCsvOutput(csvPath, exitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
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

    private void ValidateCsvOutput(string csvPath, int exitCode, string stdout, string stderr)
    {
        // Wait a short time for file system to finish flushing
        Thread.Sleep(100);

        const int maxRetries = 3;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var actualCsvPath = FindCsvOutputFile(csvPath);
            if (actualCsvPath is null)
            {
                if (attempt < maxRetries - 1)
                {
                    Thread.Sleep(100 * (attempt + 1)); // Exponential backoff
                    continue;
                }

                var baseName = Path.GetFileNameWithoutExtension(csvPath);
                var multiCsvPattern = $"{baseName}-*.csv";
                throw new InvalidOperationException(
                    $"PresentMon did not create output file. Searched for '{csvPath}' and multi_csv pattern '{multiCsvPattern}'. Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
            }

            try
            {
                var lines = File.ReadAllLines(actualCsvPath);
                var dataRows = lines.Length > 1 ? lines.Length - 1 : 0; // Subtract header row

                Log($"[PresentMon] CSV output: {actualCsvPath}, {dataRows} data rows");

                if (dataRows == 0)
                {
                    throw new InvalidOperationException(
                        $"PresentMon output file '{actualCsvPath}' contains only header (no data rows). Exit code: {exitCode}\nstdout: {stdout}\nstderr: {stderr}");
                }

                return; // Success
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                Thread.Sleep(100 * (attempt + 1)); // Retry on file access issues
            }
        }
    }

    /// <summary>
    /// Finds the CSV output file, handling both standard output and multi_csv mode.
    /// When PresentMon uses --multi_csv, output files are named: {base}-{processname}-{pid}.csv
    /// (e.g., "presentmon-msedge.exe-9112.csv" for an Edge GPU process)
    /// </summary>
    private string? FindCsvOutputFile(string expectedCsvPath)
    {
        // First, check if the exact file exists (standard mode)
        if (File.Exists(expectedCsvPath))
        {
            return expectedCsvPath;
        }

        // Check for multi_csv output pattern: {base}-{processname}-{pid}.csv
        var directory = Path.GetDirectoryName(expectedCsvPath);

        // Handle relative paths without directory separator by using current directory
        if (string.IsNullOrEmpty(directory))
        {
            directory = Directory.GetCurrentDirectory();
        }

        if (!Directory.Exists(directory))
        {
            return null;
        }

        // Pattern matches PresentMon's multi_csv naming: {base}-{processname}-{pid}.csv
        var baseName = Path.GetFileNameWithoutExtension(expectedCsvPath);
        var pattern = $"{baseName}-*.csv";

        try
        {
            var matchingFiles = Directory.GetFiles(directory, pattern);
            if (matchingFiles.Length > 0)
            {
                // If multiple files exist, return the most recently modified one
                var mostRecent = matchingFiles
                    .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                    .First();
                Log($"[PresentMon] Found multi_csv output: {mostRecent}");
                return mostRecent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log($"[PresentMon] Error searching for CSV files: {ex.Message}");
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

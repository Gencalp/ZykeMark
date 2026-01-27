using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ZykeMark.Infrastructure.PresentMon;

public sealed class PresentMonRunner : IPresentMonRunner
{
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

        var exePath = ResolveExecutablePath(options.PresentMonPath);
        var arguments = BuildArguments(options);

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

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

        while (!process.StandardOutput.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                yield return line;
            }
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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

    private static string BuildArguments(PresentMonRunOptions options)
    {
        var parts = new List<string>
        {
            "--output_stdout",
            "--no_console_stats",
            "--qpc_time_ms",
            "--v2_metrics",
            "--exclude_dropped",
            "--timed",
            options.DurationSeconds.ToString(),
            "--terminate_after_timed"
        };

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

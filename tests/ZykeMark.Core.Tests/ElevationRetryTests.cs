using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

/// <summary>
/// Tests for the non-elevated --process_name failure detection and retry logic.
/// When PresentMon runs without admin privileges, --process_name may fail to match processes
/// started on another account. The system should detect this and retry with --process_id.
/// </summary>
public class ElevationRetryTests
{
    [Fact]
    public void IsNonElevatedProcessNameFailure_ReturnsTrueForElevationWarning()
    {
        var result = new PresentMonRunResult(
            CsvPath: null,
            ExitCode: 0,
            StdOut: "Started recording.\r\nStopped recording.\r\n",
            StdErr: "warning: PresentMon requires elevated privilege in order to query processes that are\r\n" +
                    "         short-running or started on another account.");

        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: 2012,
            DurationSeconds: 10,
            PreferProcessName: true);

        Assert.True(PresentMonRunner.IsNonElevatedProcessNameFailure(result, options));
    }

    [Fact]
    public void IsNonElevatedProcessNameFailure_ReturnsFalseWhenCsvExists()
    {
        // If CSV was produced, the capture succeeded - no retry needed
        var result = new PresentMonRunResult(
            CsvPath: "/some/path.csv",
            ExitCode: 0,
            StdOut: "Started recording.\r\nStopped recording.\r\n",
            StdErr: "warning: PresentMon requires elevated privilege in order to query processes.");

        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: 2012,
            DurationSeconds: 10,
            PreferProcessName: true);

        Assert.False(PresentMonRunner.IsNonElevatedProcessNameFailure(result, options));
    }

    [Fact]
    public void IsNonElevatedProcessNameFailure_ReturnsFalseWhenNotUsingProcessName()
    {
        // If --process_name was not used, this retry doesn't apply
        var result = new PresentMonRunResult(
            CsvPath: null,
            ExitCode: 0,
            StdOut: "Started recording.\r\nStopped recording.\r\n",
            StdErr: "warning: PresentMon requires elevated privilege.");

        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: 2012,
            DurationSeconds: 10,
            PreferProcessName: false);

        Assert.False(PresentMonRunner.IsNonElevatedProcessNameFailure(result, options));
    }

    [Fact]
    public void IsNonElevatedProcessNameFailure_ReturnsFalseWhenNoProcessIdFallback()
    {
        // If no PID is available to fall back to, retry won't help
        var result = new PresentMonRunResult(
            CsvPath: null,
            ExitCode: 0,
            StdOut: "Started recording.\r\nStopped recording.\r\n",
            StdErr: "warning: PresentMon requires elevated privilege.");

        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: null,
            DurationSeconds: 10,
            PreferProcessName: true);

        Assert.False(PresentMonRunner.IsNonElevatedProcessNameFailure(result, options));
    }

    [Fact]
    public void IsNonElevatedProcessNameFailure_ReturnsFalseForUnrelatedStderr()
    {
        // Stderr doesn't contain elevation warning
        var result = new PresentMonRunResult(
            CsvPath: null,
            ExitCode: 1,
            StdOut: "",
            StdErr: "error: some other error occurred");

        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: 2012,
            DurationSeconds: 10,
            PreferProcessName: true);

        Assert.False(PresentMonRunner.IsNonElevatedProcessNameFailure(result, options));
    }

    [Fact]
    public void IsNonElevatedProcessNameFailure_ReturnsFalseForEmptyStderr()
    {
        var result = new PresentMonRunResult(
            CsvPath: null,
            ExitCode: 0,
            StdOut: "Started recording.\r\nStopped recording.\r\n",
            StdErr: "");

        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: 2012,
            DurationSeconds: 10,
            PreferProcessName: true);

        Assert.False(PresentMonRunner.IsNonElevatedProcessNameFailure(result, options));
    }

    [Fact]
    public void IsNonElevatedProcessNameFailure_IsCaseInsensitive()
    {
        var result = new PresentMonRunResult(
            CsvPath: null,
            ExitCode: 0,
            StdOut: "",
            StdErr: "WARNING: PresentMon requires ELEVATED PRIVILEGE to query processes.");

        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: 2012,
            DurationSeconds: 10,
            PreferProcessName: true);

        Assert.True(PresentMonRunner.IsNonElevatedProcessNameFailure(result, options));
    }

    [Fact]
    public void ElevationDiagnosticsProperties_DefaultToNull()
    {
        var diagnostics = new CaptureDiagnostics();

        Assert.Null(diagnostics.ElevationRetryAttempted);
        Assert.Null(diagnostics.ElevationRetrySuccess);
    }

    [Fact]
    public void ElevationDiagnosticsProperties_CanBeSetAndRead()
    {
        var diagnostics = new CaptureDiagnostics
        {
            ElevationRetryAttempted = true,
            ElevationRetrySuccess = true
        };

        Assert.True(diagnostics.ElevationRetryAttempted);
        Assert.True(diagnostics.ElevationRetrySuccess);
    }
}

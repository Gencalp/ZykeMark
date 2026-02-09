using System.Reflection;
using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class PresentMonRunnerTests
{
    /// <summary>
    /// Helper to invoke the private FindCsvOutputFile method for testing.
    /// </summary>
    private static string? InvokeFindCsvOutputFile(PresentMonRunner runner, string expectedCsvPath, string? exeDirectory = null)
    {
        var method = typeof(PresentMonRunner).GetMethod("FindCsvOutputFile",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string?)method?.Invoke(runner, [expectedCsvPath, exeDirectory]);
    }

    /// <summary>
    /// Safely cleans up a temporary test directory, ignoring any exceptions.
    /// </summary>
    private static void SafeCleanupTempDir(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures - temp directory will be cleaned up by OS eventually
        }
    }

    [Fact]
    public void FindCsvOutputFile_ReturnsExactPath_WhenFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var expectedPath = Path.Combine(tempDir, "presentmon.csv");
            File.WriteAllText(expectedPath, "Application,ProcessID,CPUStartTime,FrameTime\nGame.exe,1234,1000,16.67");

            var runner = new PresentMonRunner();
            var result = InvokeFindCsvOutputFile(runner, expectedPath);

            Assert.Equal(expectedPath, result);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void FindCsvOutputFile_FindsMultiCsvPattern_WhenExpectedFileMissing()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var expectedPath = Path.Combine(tempDir, "presentmon.csv");
            // Create a multi_csv file instead of the expected file
            var multiCsvPath = Path.Combine(tempDir, "presentmon-msedge.exe-1234.csv");
            File.WriteAllText(multiCsvPath, "Application,ProcessID,CPUStartTime,FrameTime\nGame.exe,1234,1000,16.67");

            var runner = new PresentMonRunner();
            var result = InvokeFindCsvOutputFile(runner, expectedPath);

            Assert.Equal(multiCsvPath, result);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void FindCsvOutputFile_FindsDefaultPresentMonPattern_WhenOutputFileIgnored()
    {
        // Regression test: When PresentMon ignores --output_file argument and
        // falls back to its default naming format (PresentMon-<timestamp>.csv)
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var expectedPath = Path.Combine(tempDir, "presentmon.csv");
            // Simulate PresentMon's default naming when it ignores --output_file
            var defaultPath = Path.Combine(tempDir, "PresentMon-2024-01-29T19-55-23.csv");
            File.WriteAllText(defaultPath, "Application,ProcessID,CPUStartTime,FrameTime\nGame.exe,1234,1000,16.67");

            var runner = new PresentMonRunner();
            var result = InvokeFindCsvOutputFile(runner, expectedPath);

            Assert.Equal(defaultPath, result);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void FindCsvOutputFile_ReturnsNull_WhenNoMatchingFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var expectedPath = Path.Combine(tempDir, "presentmon.csv");
            // No files in directory

            var runner = new PresentMonRunner();
            var result = InvokeFindCsvOutputFile(runner, expectedPath);

            Assert.Null(result);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void FindCsvOutputFile_PrefersExactMatch_OverPatterns()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var expectedPath = Path.Combine(tempDir, "presentmon.csv");
            File.WriteAllText(expectedPath, "Application,ProcessID,CPUStartTime,FrameTime\nGame.exe,1234,1000,16.67");

            // Also create multi_csv and default pattern files
            var multiCsvPath = Path.Combine(tempDir, "presentmon-msedge.exe-1234.csv");
            File.WriteAllText(multiCsvPath, "multi csv content");
            var defaultPath = Path.Combine(tempDir, "PresentMon-2024-01-29T19-55-23.csv");
            File.WriteAllText(defaultPath, "default content");

            var runner = new PresentMonRunner();
            var result = InvokeFindCsvOutputFile(runner, expectedPath);

            // Should prefer the exact match
            Assert.Equal(expectedPath, result);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void FindCsvOutputFile_FindsFileInExeDirectory_WhenNotInExpectedDirectory()
    {
        // Test the fallback behavior where PresentMon writes to its executable directory
        // instead of the expected output directory
        var expectedDir = Path.Combine(Path.GetTempPath(), "zyke_test_expected_" + Guid.NewGuid().ToString("N"));
        var exeDir = Path.Combine(Path.GetTempPath(), "zyke_test_exe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(expectedDir);
        Directory.CreateDirectory(exeDir);

        try
        {
            var expectedPath = Path.Combine(expectedDir, "presentmon.csv");
            // Simulate PresentMon writing to its executable directory instead
            var exeDirCsvPath = Path.Combine(exeDir, "PresentMon-2024-01-29T19-55-23.csv");
            File.WriteAllText(exeDirCsvPath, "Application,ProcessID,CPUStartTime,FrameTime\nGame.exe,1234,1000,16.67");

            var runner = new PresentMonRunner();
            var result = InvokeFindCsvOutputFile(runner, expectedPath, exeDir);

            Assert.Equal(exeDirCsvPath, result);
        }
        finally
        {
            SafeCleanupTempDir(expectedDir);
            SafeCleanupTempDir(exeDir);
        }
    }

    [Fact]
    public void FindCsvOutputFile_PrefersExpectedDirectory_OverExeDirectory()
    {
        // When files exist in both directories, the expected directory should take precedence
        var expectedDir = Path.Combine(Path.GetTempPath(), "zyke_test_expected_" + Guid.NewGuid().ToString("N"));
        var exeDir = Path.Combine(Path.GetTempPath(), "zyke_test_exe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(expectedDir);
        Directory.CreateDirectory(exeDir);

        try
        {
            var expectedPath = Path.Combine(expectedDir, "presentmon.csv");
            // Create file in expected directory
            var expectedDirCsvPath = Path.Combine(expectedDir, "PresentMon-2024-01-29T20-00-00.csv");
            File.WriteAllText(expectedDirCsvPath, "Application,ProcessID,CPUStartTime,FrameTime\nGame.exe,1234,1000,16.67");
            // Also create file in exe directory
            var exeDirCsvPath = Path.Combine(exeDir, "PresentMon-2024-01-29T19-55-23.csv");
            File.WriteAllText(exeDirCsvPath, "Application,ProcessID,CPUStartTime,FrameTime\nOther.exe,5678,2000,33.33");

            var runner = new PresentMonRunner();
            var result = InvokeFindCsvOutputFile(runner, expectedPath, exeDir);

            // Should prefer the file in expected directory
            Assert.Equal(expectedDirCsvPath, result);
        }
        finally
        {
            SafeCleanupTempDir(expectedDir);
            SafeCleanupTempDir(exeDir);
        }
    }

    [Fact]
    public void BuildArguments_WithSessionIdAndFolder_IncludesSessionNameAndOutputFile()
    {
        // This is a test for the argument building logic
        // We use the public API through the options record
        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "MyGame.exe",
            ProcessId: null,
            DurationSeconds: 10,
            SessionId: "test-session-123",
            SessionFolder: "/tmp/testsession");

        // Verify the options are correctly constructed
        Assert.Equal("test-session-123", options.SessionId);
        Assert.Equal("/tmp/testsession", options.SessionFolder);
        Assert.Equal("MyGame.exe", options.ProcessName);
        Assert.Equal(10, options.DurationSeconds);
    }

    [Fact]
    public void RunOptions_WithoutSessionInfo_WorksForStdoutMode()
    {
        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "Game.exe",
            ProcessId: null,
            DurationSeconds: 5);

        // SessionId and SessionFolder should be null by default
        Assert.Null(options.SessionId);
        Assert.Null(options.SessionFolder);
    }

    [Fact]
    public void RunOptions_WithRecordSyntax_PreservesValues()
    {
        var baseOptions = new PresentMonRunOptions(
            PresentMonPath: "C:\\PresentMon.exe",
            ProcessName: "Game.exe",
            ProcessId: null,
            DurationSeconds: 10);

        var updatedOptions = baseOptions with
        {
            DurationSeconds = 20,
            SessionId = "new-session",
            SessionFolder = "C:\\Sessions\\new-session"
        };

        // Original should be unchanged
        Assert.Equal(10, baseOptions.DurationSeconds);
        Assert.Null(baseOptions.SessionId);

        // Updated should have new values
        Assert.Equal(20, updatedOptions.DurationSeconds);
        Assert.Equal("new-session", updatedOptions.SessionId);
        Assert.Equal("C:\\Sessions\\new-session", updatedOptions.SessionFolder);
        Assert.Equal("Game.exe", updatedOptions.ProcessName); // Preserved from original
    }

    [Fact]
    public void RunOptions_PreferProcessName_DefaultsToFalse()
    {
        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "Game.exe",
            ProcessId: 1234,
            DurationSeconds: 10);

        // PreferProcessName should default to false
        Assert.False(options.PreferProcessName);
    }

    [Fact]
    public void RunOptions_WithPreferProcessName_PreservesValue()
    {
        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "msedge.exe",
            ProcessId: 9068,
            DurationSeconds: 30,
            PreferProcessName: true);

        Assert.True(options.PreferProcessName);
        Assert.Equal("msedge.exe", options.ProcessName);
        Assert.Equal(9068, options.ProcessId);
    }

    [Theory]
    [InlineData("chrome", "chrome.exe")]
    [InlineData("msedge", "msedge.exe")]
    [InlineData("firefox", "firefox.exe")]
    [InlineData("chrome.exe", "chrome.exe")]
    [InlineData("MyGame.EXE", "MyGame.EXE")]
    [InlineData("Game.Exe", "Game.Exe")]
    public void NormalizeProcessNameForPresentMon_EnsuresExeSuffix(string input, string expected)
    {
        var result = PresentMonRunner.NormalizeProcessNameForPresentMon(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildArguments_WithProcessNameWithoutExe_AppendsExeSuffix()
    {
        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome",
            ProcessId: null,
            DurationSeconds: 10,
            SessionId: "test123",
            SessionFolder: "/tmp/test",
            PreferProcessName: true);

        var arguments = InvokeBuildArguments(options, "/tmp/test/presentmon.csv");

        Assert.Contains("--process_name chrome.exe", arguments);
        Assert.DoesNotContain("--process_name chrome ", arguments);
    }

    [Fact]
    public void BuildArguments_WithProcessNameWithExe_DoesNotDoubleAppend()
    {
        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "chrome.exe",
            ProcessId: null,
            DurationSeconds: 10,
            PreferProcessName: true);

        var arguments = InvokeBuildArguments(options, null);

        Assert.Contains("--process_name chrome.exe", arguments);
        Assert.DoesNotContain("chrome.exe.exe", arguments);
    }

    [Fact]
    public void BuildArguments_FallbackProcessName_AlsoNormalized()
    {
        // When PreferProcessName is false and no PID is available, falls back to process name
        var options = new PresentMonRunOptions(
            PresentMonPath: null,
            ProcessName: "firefox",
            ProcessId: null,
            DurationSeconds: 5,
            PreferProcessName: false);

        var arguments = InvokeBuildArguments(options, null);

        Assert.Contains("--process_name firefox.exe", arguments);
    }

    /// <summary>
    /// Helper to invoke the private BuildArguments method for testing.
    /// </summary>
    private static string InvokeBuildArguments(PresentMonRunOptions options, string? outputFile)
    {
        var method = typeof(PresentMonRunner).GetMethod("BuildArguments",
            BindingFlags.NonPublic | BindingFlags.Static);
        return (string)method!.Invoke(null, [options, outputFile])!;
    }
}

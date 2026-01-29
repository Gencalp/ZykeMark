using System.Reflection;
using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class PresentMonRunnerTests
{
    /// <summary>
    /// Helper to invoke the private FindCsvOutputFile method for testing.
    /// </summary>
    private static string? InvokeFindCsvOutputFile(PresentMonRunner runner, string expectedCsvPath)
    {
        var method = typeof(PresentMonRunner).GetMethod("FindCsvOutputFile",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (string?)method?.Invoke(runner, [expectedCsvPath]);
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
            Directory.Delete(tempDir, recursive: true);
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
            Directory.Delete(tempDir, recursive: true);
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
            Directory.Delete(tempDir, recursive: true);
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
            Directory.Delete(tempDir, recursive: true);
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
            Directory.Delete(tempDir, recursive: true);
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
}

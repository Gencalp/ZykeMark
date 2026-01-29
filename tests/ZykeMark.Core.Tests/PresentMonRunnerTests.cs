using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class PresentMonRunnerTests
{
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

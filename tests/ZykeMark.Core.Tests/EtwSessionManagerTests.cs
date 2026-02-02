using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

/// <summary>
/// Tests for ETW session manager functionality.
/// </summary>
public class EtwSessionManagerTests
{
    [Fact]
    public void ParseEtwWarnings_DetectsError1450InStderr()
    {
        // Arrange
        var stderr = "error: failed to start trace session: error code 1450.";
        
        // Act
        var (etwEventsLostCount, rawWarnings) = PresentMonRunner.ParseEtwWarnings("", stderr);
        
        // Assert - should not detect as ETW events lost (this is a different error)
        // The error 1450 detection is handled by IsError1450 method, not ParseEtwWarnings
        Assert.Null(etwEventsLostCount);
    }

    [Fact]
    public void EtwCleanupResult_HadStaleSessions_ReturnsTrueWhenSessionsStopped()
    {
        // Arrange
        var result = new EtwCleanupResult(
            Success: true,
            StoppedSessions: new[] { "ZykeMark_test123" },
            FailedSessions: Array.Empty<string>(),
            ErrorMessage: null);

        // Assert
        Assert.True(result.HadStaleSessions);
        Assert.Equal(1, result.TotalAttempted);
    }

    [Fact]
    public void EtwCleanupResult_HadStaleSessions_ReturnsFalseWhenNoSessions()
    {
        // Arrange
        var result = new EtwCleanupResult(
            Success: true,
            StoppedSessions: Array.Empty<string>(),
            FailedSessions: Array.Empty<string>(),
            ErrorMessage: null);

        // Assert
        Assert.False(result.HadStaleSessions);
        Assert.Equal(0, result.TotalAttempted);
    }

    [Fact]
    public void EtwSessionListResult_SuccessWithEmptyList()
    {
        // Arrange
        var result = new EtwSessionListResult(
            Success: true,
            SessionNames: Array.Empty<string>(),
            RawOutput: "Data Collector Set...",
            ErrorMessage: null);

        // Assert
        Assert.True(result.Success);
        Assert.Empty(result.SessionNames);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void EtwSessionStopResult_FailureWithError()
    {
        // Arrange
        var result = new EtwSessionStopResult(
            Success: false,
            SessionName: "ZykeMark_abc123",
            RawOutput: "Error: Session not found",
            ErrorMessage: "logman stop failed with exit code 1");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("ZykeMark_abc123", result.SessionName);
        Assert.NotNull(result.ErrorMessage);
    }
}

using ZykeMark.Core.Models;
using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class EtwWarningParsingTests
{
    [Fact]
    public void ParseEtwWarnings_DetectsStandardWarningFormat()
    {
        var stdout = "";
        var stderr = "warning: 4651 ETW events were lost.";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Equal(4651, count);
        Assert.Single(warnings);
        Assert.Contains("4651", warnings[0]);
    }

    [Fact]
    public void ParseEtwWarnings_DetectsWarningInStdout()
    {
        var stdout = "warning: 1234 ETW events were lost.";
        var stderr = "";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Equal(1234, count);
        Assert.Single(warnings);
    }

    [Fact]
    public void ParseEtwWarnings_HandlesCaseInsensitiveMatch()
    {
        var stdout = "";
        var stderr = "WARNING: 500 ETW EVENTS WERE LOST.";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Equal(500, count);
        Assert.Single(warnings);
    }

    [Fact]
    public void ParseEtwWarnings_HandlesSingularEvent()
    {
        var stdout = "";
        var stderr = "warning: 1 ETW event was lost.";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Equal(1, count);
        Assert.Single(warnings);
    }

    [Fact]
    public void ParseEtwWarnings_AccumulatesMultipleWarnings()
    {
        var stdout = "";
        var stderr = "warning: 1000 ETW events were lost.\nwarning: 500 ETW events were lost.";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Equal(1500, count); // Total of both warnings
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void ParseEtwWarnings_ReturnsNullCount_WhenNoWarnings()
    {
        var stdout = "Started recording.";
        var stderr = "";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Null(count);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ParseEtwWarnings_HandlesPartialMatch_WithoutCount()
    {
        var stdout = "";
        var stderr = "ETW events were lost during capture.";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Equal(1, count); // Minimum count when detected but not parsed
        Assert.Single(warnings);
    }

    [Fact]
    public void ParseEtwWarnings_IgnoresUnrelatedWarnings()
    {
        var stdout = "";
        var stderr = "warning: Something unrelated happened.";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        Assert.Null(count);
        Assert.Empty(warnings);
    }
}

public class DataQualityTests
{
    [Fact]
    public void ComputeRiskLevel_ReturnsUnknown_ForNullCount()
    {
        var level = DataQuality.ComputeRiskLevel(null);
        Assert.Equal(EtwRiskLevel.Unknown, level);
    }

    [Fact]
    public void ComputeRiskLevel_ReturnsNone_ForZeroCount()
    {
        var level = DataQuality.ComputeRiskLevel(0);
        Assert.Equal(EtwRiskLevel.None, level);
    }

    [Fact]
    public void ComputeRiskLevel_ReturnsLow_ForSmallCount()
    {
        Assert.Equal(EtwRiskLevel.Low, DataQuality.ComputeRiskLevel(1));
        Assert.Equal(EtwRiskLevel.Low, DataQuality.ComputeRiskLevel(500));
        Assert.Equal(EtwRiskLevel.Low, DataQuality.ComputeRiskLevel(1000));
    }

    [Fact]
    public void ComputeRiskLevel_ReturnsModerate_ForMediumCount()
    {
        Assert.Equal(EtwRiskLevel.Moderate, DataQuality.ComputeRiskLevel(1001));
        Assert.Equal(EtwRiskLevel.Moderate, DataQuality.ComputeRiskLevel(5000));
        Assert.Equal(EtwRiskLevel.Moderate, DataQuality.ComputeRiskLevel(10000));
    }

    [Fact]
    public void ComputeRiskLevel_ReturnsHigh_ForLargeCount()
    {
        Assert.Equal(EtwRiskLevel.High, DataQuality.ComputeRiskLevel(10001));
        Assert.Equal(EtwRiskLevel.High, DataQuality.ComputeRiskLevel(50000));
        Assert.Equal(EtwRiskLevel.High, DataQuality.ComputeRiskLevel(100000));
    }

    [Fact]
    public void Create_SetsCorrectRiskLevel()
    {
        var quality = DataQuality.Create(5000, new[] { "test warning" });

        Assert.Equal(5000, quality.EtwEventsLostCount);
        Assert.Equal(EtwRiskLevel.Moderate, quality.EtwEventsLostRiskLevel);
        Assert.Single(quality.CaptureWarnings);
    }

    [Fact]
    public void Empty_ReturnsUnknownRiskLevel()
    {
        var empty = DataQuality.Empty;

        Assert.Null(empty.EtwEventsLostCount);
        Assert.Equal(EtwRiskLevel.Unknown, empty.EtwEventsLostRiskLevel);
        Assert.Empty(empty.CaptureWarnings);
    }
}

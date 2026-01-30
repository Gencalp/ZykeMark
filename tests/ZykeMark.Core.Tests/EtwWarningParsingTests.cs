using System.Text.Json;
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
    public void Create_WithNullCount_DefaultsToZeroAndNone()
    {
        var quality = DataQuality.Create(null);

        Assert.Equal(0, quality.EtwEventsLostCount);
        Assert.Equal(EtwRiskLevel.None, quality.EtwEventsLostRiskLevel);
        Assert.Empty(quality.CaptureWarnings);
    }

    [Fact]
    public void Empty_ReturnsNoneRiskLevel()
    {
        var empty = DataQuality.Empty;

        Assert.Equal(0, empty.EtwEventsLostCount);
        Assert.Equal(EtwRiskLevel.None, empty.EtwEventsLostRiskLevel);
        Assert.Empty(empty.CaptureWarnings);
    }

    [Fact]
    public void DataQuality_Defaults_NoWarnings()
    {
        // When no warnings are parsed (null count from parser), defaults should be:
        // - Count = 0
        // - Risk = None
        // - Warnings = empty array
        var quality = DataQuality.Create(null);

        Assert.Equal(0, quality.EtwEventsLostCount);
        Assert.Equal(EtwRiskLevel.None, quality.EtwEventsLostRiskLevel);
        Assert.NotNull(quality.CaptureWarnings);
        Assert.Empty(quality.CaptureWarnings);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(quality.CaptureWarnings);
    }

    [Fact]
    public void DataQuality_ParsesWarnings_FromStdoutAndStderr()
    {
        // Test that warnings from both stdout and stderr are accumulated
        var stdout = "warning: 1000 ETW events were lost.";
        var stderr = "warning: 500 ETW events were lost.\nwarning: 300 ETW events were lost.";

        var (count, warnings) = PresentMonRunner.ParseEtwWarnings(stdout, stderr);

        // Total count should be sum of all warnings
        Assert.Equal(1800, count);
        Assert.Equal(3, warnings.Count);

        // Create DataQuality and verify risk level
        var quality = DataQuality.Create(count, warnings.ToList());
        Assert.Equal(1800, quality.EtwEventsLostCount);
        Assert.Equal(EtwRiskLevel.Moderate, quality.EtwEventsLostRiskLevel);
        Assert.Equal(3, quality.CaptureWarnings.Count);
    }

    [Fact]
    public void CaptureWarnings_IsAlwaysArray_NotNullOrDictionary()
    {
        // Test with null warnings
        var quality1 = DataQuality.Create(0, null);
        Assert.NotNull(quality1.CaptureWarnings);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(quality1.CaptureWarnings);

        // Test Empty property
        var quality2 = DataQuality.Empty;
        Assert.NotNull(quality2.CaptureWarnings);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(quality2.CaptureWarnings);

        // Test with explicit empty list
        var quality3 = DataQuality.Create(0, new List<string>());
        Assert.NotNull(quality3.CaptureWarnings);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(quality3.CaptureWarnings);
    }

    [Fact]
    public void EtwRiskLevel_None_IsDefaultValue()
    {
        // Ensure None = 0 so it's the default enum value
        Assert.Equal(0, (int)EtwRiskLevel.None);
        Assert.Equal(EtwRiskLevel.None, default(EtwRiskLevel));
    }

    [Fact]
    public void DataQuality_Serialization_CaptureWarningsIsArray()
    {
        // Create DataQuality with various warning states
        var qualityEmpty = DataQuality.Empty;
        var qualityWithWarnings = DataQuality.Create(1500, new List<string> { "warning1", "warning2" });

        // Serialize using the same pattern as SessionManager
        var emptyPayload = new
        {
            EtwEventsLostCount = qualityEmpty.EtwEventsLostCount,
            EtwEventsLostRiskLevel = qualityEmpty.EtwEventsLostRiskLevel.ToString(),
            CaptureWarnings = qualityEmpty.CaptureWarnings
        };

        var warningsPayload = new
        {
            EtwEventsLostCount = qualityWithWarnings.EtwEventsLostCount,
            EtwEventsLostRiskLevel = qualityWithWarnings.EtwEventsLostRiskLevel.ToString(),
            CaptureWarnings = qualityWithWarnings.CaptureWarnings
        };

        var emptyJson = JsonSerializer.Serialize(emptyPayload);
        var warningsJson = JsonSerializer.Serialize(warningsPayload);

        // Parse back and verify CaptureWarnings is an array
        var emptyDoc = JsonDocument.Parse(emptyJson);
        var warningsDoc = JsonDocument.Parse(warningsJson);

        // Verify empty case: CaptureWarnings should be an array []
        Assert.True(emptyDoc.RootElement.TryGetProperty("CaptureWarnings", out var emptyWarnings));
        Assert.Equal(JsonValueKind.Array, emptyWarnings.ValueKind);
        Assert.Equal(0, emptyWarnings.GetArrayLength());

        // Verify EtwEventsLostCount is 0 (not null) for empty
        Assert.True(emptyDoc.RootElement.TryGetProperty("EtwEventsLostCount", out var emptyCount));
        Assert.Equal(JsonValueKind.Number, emptyCount.ValueKind);
        Assert.Equal(0, emptyCount.GetInt32());

        // Verify EtwEventsLostRiskLevel is "None" (not "Unknown")
        Assert.True(emptyDoc.RootElement.TryGetProperty("EtwEventsLostRiskLevel", out var emptyRisk));
        Assert.Equal("None", emptyRisk.GetString());

        // Verify warnings case: CaptureWarnings should be an array with elements
        Assert.True(warningsDoc.RootElement.TryGetProperty("CaptureWarnings", out var captureWarnings));
        Assert.Equal(JsonValueKind.Array, captureWarnings.ValueKind);
        Assert.Equal(2, captureWarnings.GetArrayLength());
    }
}

namespace ZykeMark.Core.Models;

/// <summary>
/// Represents the risk level associated with ETW event loss during data capture.
/// </summary>
public enum EtwRiskLevel
{
    /// <summary>ETW event loss status is unknown (not detected or parsing failed).</summary>
    Unknown,

    /// <summary>No ETW events were lost.</summary>
    None,

    /// <summary>Low number of ETW events lost (1-1000). Minor impact on data quality.</summary>
    Low,

    /// <summary>Moderate number of ETW events lost (1001-10000). May affect data accuracy.</summary>
    Moderate,

    /// <summary>High number of ETW events lost (>10000). Significant impact on data reliability.</summary>
    High
}

/// <summary>
/// Represents data quality information for a capture session, including ETW event loss detection.
/// </summary>
/// <param name="EtwEventsLostCount">The number of ETW events lost during capture. Null if not detected.</param>
/// <param name="EtwEventsLostRiskLevel">The risk level based on ETW events lost count.</param>
/// <param name="CaptureWarnings">List of capture-related warnings detected during the session.</param>
public sealed record DataQuality(
    int? EtwEventsLostCount,
    EtwRiskLevel EtwEventsLostRiskLevel,
    IReadOnlyList<string> CaptureWarnings)
{
    /// <summary>
    /// Default thresholds for ETW risk level classification.
    /// </summary>
    public const int LowThreshold = 1;
    public const int ModerateThreshold = 1001;
    public const int HighThreshold = 10001;

    /// <summary>
    /// Creates a DataQuality instance with computed risk level based on the ETW events lost count.
    /// </summary>
    public static DataQuality Create(int? etwEventsLostCount, IReadOnlyList<string>? captureWarnings = null)
    {
        var riskLevel = ComputeRiskLevel(etwEventsLostCount);
        return new DataQuality(etwEventsLostCount, riskLevel, captureWarnings ?? Array.Empty<string>());
    }

    /// <summary>
    /// Computes the risk level based on the ETW events lost count.
    /// </summary>
    public static EtwRiskLevel ComputeRiskLevel(int? etwEventsLostCount)
    {
        return etwEventsLostCount switch
        {
            null => EtwRiskLevel.Unknown,
            0 => EtwRiskLevel.None,
            >= HighThreshold => EtwRiskLevel.High,
            >= ModerateThreshold => EtwRiskLevel.Moderate,
            >= LowThreshold => EtwRiskLevel.Low,
            _ => EtwRiskLevel.Unknown
        };
    }

    /// <summary>
    /// Returns an empty DataQuality instance indicating no quality data is available.
    /// </summary>
    public static DataQuality Empty => new(null, EtwRiskLevel.Unknown, Array.Empty<string>());
}

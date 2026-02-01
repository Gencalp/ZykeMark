namespace ZykeMark.Core.Models;

/// <summary>
/// Represents a single telemetry sample containing system performance metrics.
/// </summary>
public sealed record TelemetrySample(
    /// <summary>GPU Utilization percentage (0-100)</summary>
    double? GpuUtilizationPercent,
    /// <summary>Dedicated VRAM usage in MB</summary>
    double? VramDedicatedMB,
    /// <summary>Shared VRAM usage in MB</summary>
    double? VramSharedMB,
    /// <summary>Process CPU usage percentage (0-100)</summary>
    double? CpuProcessPercent,
    /// <summary>Top thread CPU usage percentage (0-100)</summary>
    double? TopThreadCpuPercent,
    /// <summary>Process Working Set memory in MB</summary>
    double? RamWorkingSetMB,
    /// <summary>Process Private Bytes in MB</summary>
    double? RamPrivateBytesMB,
    /// <summary>Disk read rate in MB/s</summary>
    double? DiskReadMBps,
    /// <summary>Timestamp when this sample was collected (milliseconds since session start)</summary>
    double? TimestampMs = null)
{
    /// <summary>
    /// Gets the total VRAM usage (Dedicated + Shared) in MB.
    /// </summary>
    public double? VramTotalMB => VramDedicatedMB.HasValue || VramSharedMB.HasValue
        ? (VramDedicatedMB ?? 0) + (VramSharedMB ?? 0)
        : null;

    /// <summary>
    /// Creates an empty telemetry sample with all null values.
    /// </summary>
    public static TelemetrySample Empty => new(null, null, null, null, null, null, null, null, null);
}

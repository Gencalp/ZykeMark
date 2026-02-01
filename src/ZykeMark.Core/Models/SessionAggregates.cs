namespace ZykeMark.Core.Models;

public sealed record SessionAggregates(
    int FrameCount,
    double DurationMs,
    double AvgFps,
    double AvgFrameTimeMs,
    double P99FrameTimeMs,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double? AvgCpuFrameTimeMs,
    double? AvgGpuFrameTimeMs,
    // Telemetry averages
    double? AvgGpuUtilizationPercent = null,
    double? AvgVramDedicatedMB = null,
    double? AvgVramSharedMB = null,
    double? AvgCpuProcessPercent = null,
    double? AvgTopThreadCpuPercent = null,
    double? AvgRamWorkingSetMB = null,
    double? AvgRamPrivateBytesMB = null,
    double? AvgDiskReadMBps = null);

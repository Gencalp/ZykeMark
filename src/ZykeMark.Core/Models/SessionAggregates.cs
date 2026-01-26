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
    double? AvgGpuFrameTimeMs);

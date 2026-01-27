namespace ZykeMark.Core.Models;

public sealed record FrameSample(
    double TimestampMs,
    double FrameTimeMs,
    double? CpuFrameTimeMs = null,
    double? GpuFrameTimeMs = null);

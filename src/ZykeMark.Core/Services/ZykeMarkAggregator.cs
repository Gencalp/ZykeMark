using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;

namespace ZykeMark.Core.Services;

public sealed class ZykeMarkAggregator : IAggregator
{
    public SessionAggregates Aggregate(IReadOnlyList<FrameSample> samples)
    {
        if (samples is null)
        {
            throw new ArgumentNullException(nameof(samples));
        }

        if (samples.Count == 0)
        {
            throw new InvalidOperationException("At least one frame sample is required to aggregate metrics.");
        }

        var frameTimes = samples.Select(sample => sample.FrameTimeMs).OrderBy(value => value).ToArray();
        var avgFrameTimeMs = frameTimes.Average();
        var p99FrameTimeMs = PercentileNearestRank(frameTimes, 0.99);
        var p99Point9FrameTimeMs = PercentileNearestRank(frameTimes, 0.999);

        var cpuTimes = samples.Where(sample => sample.CpuFrameTimeMs.HasValue)
            .Select(sample => sample.CpuFrameTimeMs!.Value)
            .ToArray();
        var gpuTimes = samples.Where(sample => sample.GpuFrameTimeMs.HasValue)
            .Select(sample => sample.GpuFrameTimeMs!.Value)
            .ToArray();

        var durationMs = samples.Max(sample => sample.TimestampMs) - samples.Min(sample => sample.TimestampMs);

        // Compute telemetry averages
        var telemetrySamples = samples.Where(s => s.Telemetry is not null).Select(s => s.Telemetry!).ToArray();
        
        var avgGpuUtil = ComputeNullableAverage(telemetrySamples, t => t.GpuUtilizationPercent);
        var avgVramDedicated = ComputeNullableAverage(telemetrySamples, t => t.VramDedicatedMB);
        var avgVramShared = ComputeNullableAverage(telemetrySamples, t => t.VramSharedMB);
        var avgCpuProcess = ComputeNullableAverage(telemetrySamples, t => t.CpuProcessPercent);
        var avgTopThreadCpu = ComputeNullableAverage(telemetrySamples, t => t.TopThreadCpuPercent);
        var avgRamWorkingSet = ComputeNullableAverage(telemetrySamples, t => t.RamWorkingSetMB);
        var avgRamPrivateBytes = ComputeNullableAverage(telemetrySamples, t => t.RamPrivateBytesMB);
        var avgDiskRead = ComputeNullableAverage(telemetrySamples, t => t.DiskReadMBps);

        return new SessionAggregates(
            FrameCount: samples.Count,
            DurationMs: durationMs,
            AvgFps: 1000.0 / avgFrameTimeMs,
            AvgFrameTimeMs: avgFrameTimeMs,
            P99FrameTimeMs: p99FrameTimeMs,
            OnePercentLowFps: 1000.0 / p99FrameTimeMs,
            PointOnePercentLowFps: 1000.0 / p99Point9FrameTimeMs,
            AvgCpuFrameTimeMs: cpuTimes.Length > 0 ? cpuTimes.Average() : null,
            AvgGpuFrameTimeMs: gpuTimes.Length > 0 ? gpuTimes.Average() : null,
            AvgGpuUtilizationPercent: avgGpuUtil,
            AvgVramDedicatedMB: avgVramDedicated,
            AvgVramSharedMB: avgVramShared,
            AvgCpuProcessPercent: avgCpuProcess,
            AvgTopThreadCpuPercent: avgTopThreadCpu,
            AvgRamWorkingSetMB: avgRamWorkingSet,
            AvgRamPrivateBytesMB: avgRamPrivateBytes,
            AvgDiskReadMBps: avgDiskRead);
    }

    private static double? ComputeNullableAverage(IReadOnlyList<TelemetrySample> samples, Func<TelemetrySample, double?> selector)
    {
        var values = samples.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        return values.Length > 0 ? values.Average() : null;
    }

    // Deterministic percentile calculation using the nearest-rank method.
    // Rank = ceil(p * N), where p is in (0,1], and N is the sample count.
    private static double PercentileNearestRank(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues is null)
        {
            throw new ArgumentNullException(nameof(sortedValues));
        }

        if (sortedValues.Count == 0)
        {
            throw new InvalidOperationException("Cannot compute percentiles for an empty dataset.");
        }

        if (percentile <= 0 || percentile > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be within (0, 1].");
        }

        var rank = (int)Math.Ceiling(percentile * sortedValues.Count);
        var index = Math.Clamp(rank - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }
}

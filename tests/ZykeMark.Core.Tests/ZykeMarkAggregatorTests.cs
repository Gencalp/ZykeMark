using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using Xunit;

namespace ZykeMark.Core.Tests;

public class ZykeMarkAggregatorTests
{
    [Fact]
    public void Aggregate_ComputesExpectedMetrics_ForSimpleDataset()
    {
        var samples = new[]
        {
            new FrameSample(0, 10, 8, 9),
            new FrameSample(10, 20, 9, 11),
            new FrameSample(20, 30, null, 12),
            new FrameSample(30, 40, 10, null),
            new FrameSample(40, 50, 11, 13)
        };

        var aggregator = new ZykeMarkAggregator();
        var result = aggregator.Aggregate(samples);

        Assert.Equal(5, result.FrameCount);
        Assert.Equal(40.0, result.DurationMs, 5);
        Assert.Equal(30.0, result.AvgFrameTimeMs, 5);
        Assert.Equal(1000.0 / 30.0, result.AvgFps, 5);
        Assert.Equal(50.0, result.P99FrameTimeMs, 5);
        Assert.Equal(20.0, result.OnePercentLowFps, 5);
        Assert.Equal(20.0, result.PointOnePercentLowFps, 5);
        Assert.NotNull(result.AvgCpuFrameTimeMs);
        Assert.NotNull(result.AvgGpuFrameTimeMs);
        Assert.Equal((8 + 9 + 10 + 11) / 4.0, result.AvgCpuFrameTimeMs!.Value, 5);
        Assert.Equal((9 + 11 + 12 + 13) / 4.0, result.AvgGpuFrameTimeMs!.Value, 5);
    }

    [Fact]
    public void Aggregate_ComputesExpectedMetrics_ForUniformDataset()
    {
        var samples = new[]
        {
            new FrameSample(0, 16),
            new FrameSample(16, 16),
            new FrameSample(32, 16),
            new FrameSample(48, 16)
        };

        var aggregator = new ZykeMarkAggregator();
        var result = aggregator.Aggregate(samples);

        Assert.Equal(4, result.FrameCount);
        Assert.Equal(48.0, result.DurationMs, 5);
        Assert.Equal(16.0, result.AvgFrameTimeMs, 5);
        Assert.Equal(62.5, result.AvgFps, 5);
        Assert.Equal(16.0, result.P99FrameTimeMs, 5);
        Assert.Equal(62.5, result.OnePercentLowFps, 5);
        Assert.Equal(62.5, result.PointOnePercentLowFps, 5);
        Assert.Null(result.AvgCpuFrameTimeMs);
        Assert.Null(result.AvgGpuFrameTimeMs);
    }

    [Fact]
    public void Aggregate_ComputesExpectedMetrics_ForIncreasingDataset()
    {
        var samples = new[]
        {
            new FrameSample(0, 5),
            new FrameSample(5, 10),
            new FrameSample(10, 15),
            new FrameSample(15, 20)
        };

        var aggregator = new ZykeMarkAggregator();
        var result = aggregator.Aggregate(samples);

        Assert.Equal(4, result.FrameCount);
        Assert.Equal(15.0, result.DurationMs, 5);
        Assert.Equal(12.5, result.AvgFrameTimeMs, 5);
        Assert.Equal(80.0, result.AvgFps, 5);
        Assert.Equal(20.0, result.P99FrameTimeMs, 5);
        Assert.Equal(50.0, result.OnePercentLowFps, 5);
        Assert.Equal(50.0, result.PointOnePercentLowFps, 5);
    }

    [Fact]
    public void Aggregate_Throws_WhenSamplesEmpty()
    {
        var aggregator = new ZykeMarkAggregator();

        var exception = Assert.Throws<InvalidOperationException>(() => aggregator.Aggregate(Array.Empty<FrameSample>()));
        Assert.Contains("At least one frame sample", exception.Message);
    }

    [Fact]
    public void Aggregate_ComputesTelemetryAverages_ForSamplesWithTelemetry()
    {
        var telemetry1 = new TelemetrySample(
            GpuUtilizationPercent: 80.0,
            VramDedicatedMB: 2000.0,
            VramSharedMB: 200.0,
            CpuProcessPercent: 25.0,
            TopThreadCpuPercent: 10.0,
            RamWorkingSetMB: 1500.0,
            RamPrivateBytesMB: 1000.0,
            DiskReadMBps: 20.0);

        var telemetry2 = new TelemetrySample(
            GpuUtilizationPercent: 90.0,
            VramDedicatedMB: 2200.0,
            VramSharedMB: 300.0,
            CpuProcessPercent: 35.0,
            TopThreadCpuPercent: 15.0,
            RamWorkingSetMB: 1600.0,
            RamPrivateBytesMB: 1100.0,
            DiskReadMBps: 30.0);

        var samples = new[]
        {
            new FrameSample(0, 16, null, null, telemetry1),
            new FrameSample(16, 16, null, null, telemetry2),
            new FrameSample(32, 16, null, null, null), // No telemetry on this sample
            new FrameSample(48, 16)
        };

        var aggregator = new ZykeMarkAggregator();
        var result = aggregator.Aggregate(samples);

        // Telemetry averages should be computed only from samples with telemetry
        Assert.NotNull(result.AvgGpuUtilizationPercent);
        Assert.Equal(85.0, result.AvgGpuUtilizationPercent!.Value, 5); // (80 + 90) / 2
        
        Assert.NotNull(result.AvgVramDedicatedMB);
        Assert.Equal(2100.0, result.AvgVramDedicatedMB!.Value, 5); // (2000 + 2200) / 2
        
        Assert.NotNull(result.AvgVramSharedMB);
        Assert.Equal(250.0, result.AvgVramSharedMB!.Value, 5); // (200 + 300) / 2
        
        Assert.NotNull(result.AvgCpuProcessPercent);
        Assert.Equal(30.0, result.AvgCpuProcessPercent!.Value, 5); // (25 + 35) / 2
        
        Assert.NotNull(result.AvgTopThreadCpuPercent);
        Assert.Equal(12.5, result.AvgTopThreadCpuPercent!.Value, 5); // (10 + 15) / 2
        
        Assert.NotNull(result.AvgRamWorkingSetMB);
        Assert.Equal(1550.0, result.AvgRamWorkingSetMB!.Value, 5); // (1500 + 1600) / 2
        
        Assert.NotNull(result.AvgRamPrivateBytesMB);
        Assert.Equal(1050.0, result.AvgRamPrivateBytesMB!.Value, 5); // (1000 + 1100) / 2
        
        Assert.NotNull(result.AvgDiskReadMBps);
        Assert.Equal(25.0, result.AvgDiskReadMBps!.Value, 5); // (20 + 30) / 2
    }

    [Fact]
    public void Aggregate_ReturnsNullTelemetry_WhenNoSamplesHaveTelemetry()
    {
        var samples = new[]
        {
            new FrameSample(0, 16),
            new FrameSample(16, 16),
            new FrameSample(32, 16)
        };

        var aggregator = new ZykeMarkAggregator();
        var result = aggregator.Aggregate(samples);

        Assert.Null(result.AvgGpuUtilizationPercent);
        Assert.Null(result.AvgVramDedicatedMB);
        Assert.Null(result.AvgVramSharedMB);
        Assert.Null(result.AvgCpuProcessPercent);
        Assert.Null(result.AvgTopThreadCpuPercent);
        Assert.Null(result.AvgRamWorkingSetMB);
        Assert.Null(result.AvgRamPrivateBytesMB);
        Assert.Null(result.AvgDiskReadMBps);
    }

    [Fact]
    public void TelemetrySample_VramTotalMB_ReturnsSum()
    {
        var telemetry = new TelemetrySample(
            GpuUtilizationPercent: 80.0,
            VramDedicatedMB: 2000.0,
            VramSharedMB: 200.0,
            CpuProcessPercent: null,
            TopThreadCpuPercent: null,
            RamWorkingSetMB: null,
            RamPrivateBytesMB: null,
            DiskReadMBps: null);

        Assert.NotNull(telemetry.VramTotalMB);
        Assert.Equal(2200.0, telemetry.VramTotalMB!.Value, 5);
    }

    [Fact]
    public void TelemetrySample_VramTotalMB_ReturnsNull_WhenBothAreNull()
    {
        var telemetry = new TelemetrySample(
            GpuUtilizationPercent: 80.0,
            VramDedicatedMB: null,
            VramSharedMB: null,
            CpuProcessPercent: null,
            TopThreadCpuPercent: null,
            RamWorkingSetMB: null,
            RamPrivateBytesMB: null,
            DiskReadMBps: null);

        Assert.Null(telemetry.VramTotalMB);
    }

    [Fact]
    public void TelemetrySample_Empty_ReturnsAllNulls()
    {
        var empty = TelemetrySample.Empty;

        Assert.Null(empty.GpuUtilizationPercent);
        Assert.Null(empty.VramDedicatedMB);
        Assert.Null(empty.VramSharedMB);
        Assert.Null(empty.CpuProcessPercent);
        Assert.Null(empty.TopThreadCpuPercent);
        Assert.Null(empty.RamWorkingSetMB);
        Assert.Null(empty.RamPrivateBytesMB);
        Assert.Null(empty.DiskReadMBps);
        Assert.Null(empty.VramTotalMB);
    }
}

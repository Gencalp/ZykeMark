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
        Assert.Equal((8 + 9 + 10 + 11) / 4.0, result.AvgCpuFrameTimeMs, 5);
        Assert.Equal((9 + 11 + 12 + 13) / 4.0, result.AvgGpuFrameTimeMs, 5);
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
}

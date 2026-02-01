using System.Text.Json;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using ZykeMark.Infrastructure.Collectors;
using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class TelemetryAttachmentTests
{
    [Fact]
    public void Collector_AttachesTelemetryByTimestamp_NotJustLatest()
    {
        // Arrange: Create telemetry samples at different timestamps
        var telemetry0 = new TelemetrySample(
            GpuUtilizationPercent: 50.0,
            VramDedicatedMB: 1000.0,
            VramSharedMB: 100.0,
            CpuProcessPercent: 10.0,
            TopThreadCpuPercent: 5.0,
            RamWorkingSetMB: 500.0,
            RamPrivateBytesMB: 400.0,
            DiskReadMBps: 5.0,
            TimestampMs: 0);

        var telemetry500 = new TelemetrySample(
            GpuUtilizationPercent: 60.0,
            VramDedicatedMB: 1100.0,
            VramSharedMB: 110.0,
            CpuProcessPercent: 15.0,
            TopThreadCpuPercent: 7.0,
            RamWorkingSetMB: 550.0,
            RamPrivateBytesMB: 450.0,
            DiskReadMBps: 7.0,
            TimestampMs: 500);

        var telemetry1000 = new TelemetrySample(
            GpuUtilizationPercent: 70.0,
            VramDedicatedMB: 1200.0,
            VramSharedMB: 120.0,
            CpuProcessPercent: 20.0,
            TopThreadCpuPercent: 10.0,
            RamWorkingSetMB: 600.0,
            RamPrivateBytesMB: 500.0,
            DiskReadMBps: 10.0,
            TimestampMs: 1000);

        var fakeTelemetrySampler = new FakeTelemetrySampler();
        fakeTelemetrySampler.AddSamples(telemetry0, telemetry500, telemetry1000);

        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("TelemetryTest", "1.0.0", new RunConfig());

        // CSV with raw timestamps: 1000ms, 1500ms, 2000ms (first frame at 1000ms)
        // After normalization (subtracting first timestamp): 0ms, 500ms, 1000ms
        // These normalized timestamps match the telemetry sample timestamps above
        var csvLines = new[]
        {
            "Application,ProcessID,SwapChainAddress,Runtime,CPUStartTime,FrameTime,MsCPUBusy,MsGPUTime",
            "MyGame.exe,4242,0x1,DXGI,1000.0,16.67,5.2,6.1",  // Raw 1000ms → Normalized 0ms → matches telemetry0
            "MyGame.exe,4242,0x1,DXGI,1500.0,16.67,5.1,6.0",  // Raw 1500ms → Normalized 500ms → matches telemetry500
            "MyGame.exe,4242,0x1,DXGI,2000.0,16.67,5.0,5.9"   // Raw 2000ms → Normalized 1000ms → matches telemetry1000
        };

        var runner = new FakePresentMonRunner(csvLines);
        var parser = new PresentMonCsvParser();
        var runOptions = new PresentMonRunOptions("PresentMon.exe", "MyGame.exe", null, 1);

        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            runner,
            parser,
            runOptions,
            telemetrySampler: fakeTelemetrySampler);

        // Act: Collect samples
        var samples = collector.Collect(TimeSpan.FromSeconds(1));

        // Assert: Each frame should have telemetry attached based on timestamp proximity
        Assert.Equal(3, samples.Count);

        // Frame at 0ms should get telemetry at 0ms (GPU 50%)
        Assert.NotNull(samples[0].Telemetry);
        Assert.Equal(50.0, samples[0].Telemetry!.GpuUtilizationPercent);

        // Frame at 500ms should get telemetry at 500ms (GPU 60%)
        Assert.NotNull(samples[1].Telemetry);
        Assert.Equal(60.0, samples[1].Telemetry!.GpuUtilizationPercent);

        // Frame at 1000ms should get telemetry at 1000ms (GPU 70%)
        Assert.NotNull(samples[2].Telemetry);
        Assert.Equal(70.0, samples[2].Telemetry!.GpuUtilizationPercent);

        sessionManager.StopSession(metadata.SessionId);
    }

    [Fact]
    public void Collector_AttachesTelemetry_ToChunksInSummary()
    {
        // Arrange: Create telemetry sample
        var telemetry = new TelemetrySample(
            GpuUtilizationPercent: 65.0,
            VramDedicatedMB: 1500.0,
            VramSharedMB: 150.0,
            CpuProcessPercent: 25.0,
            TopThreadCpuPercent: 12.0,
            RamWorkingSetMB: 800.0,
            RamPrivateBytesMB: 600.0,
            DiskReadMBps: 15.0,
            TimestampMs: 0);

        var fakeTelemetrySampler = new FakeTelemetrySampler();
        fakeTelemetrySampler.AddSamples(telemetry);

        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("TelemetryTest", "1.0.0", new RunConfig());

        var csvLines = new[]
        {
            "Application,ProcessID,SwapChainAddress,Runtime,CPUStartTime,FrameTime,MsCPUBusy,MsGPUTime",
            "MyGame.exe,4242,0x1,DXGI,1000.0,16.67,5.2,6.1",
            "MyGame.exe,4242,0x1,DXGI,1016.67,16.67,5.1,6.0",
            "MyGame.exe,4242,0x1,DXGI,1033.34,16.67,5.0,5.9"
        };

        var runner = new FakePresentMonRunner(csvLines);
        var parser = new PresentMonCsvParser();
        var runOptions = new PresentMonRunOptions("PresentMon.exe", "MyGame.exe", null, 1);

        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            runner,
            parser,
            runOptions,
            telemetrySampler: fakeTelemetrySampler);

        collector.Collect(TimeSpan.FromSeconds(1));
        var summaryPath = sessionManager.StopSession(metadata.SessionId);

        // Assert: Summary should include telemetry aggregates
        using var summaryStream = File.OpenRead(summaryPath);
        using var document = JsonDocument.Parse(summaryStream);
        var aggregates = document.RootElement.GetProperty("aggregates");

        // Telemetry aggregates should be non-null
        Assert.True(aggregates.TryGetProperty("AvgGpuUtilizationPercent", out var gpuUtil));
        Assert.Equal(65.0, gpuUtil.GetDouble(), 1);

        Assert.True(aggregates.TryGetProperty("AvgRamWorkingSetMB", out var ramWs));
        Assert.Equal(800.0, ramWs.GetDouble(), 1);

        Assert.True(aggregates.TryGetProperty("AvgCpuProcessPercent", out var cpuProc));
        Assert.Equal(25.0, cpuProc.GetDouble(), 1);

        Assert.True(aggregates.TryGetProperty("AvgDiskReadMBps", out var diskRead));
        Assert.Equal(15.0, diskRead.GetDouble(), 1);
    }

    [Fact]
    public void TelemetrySample_TimestampMs_IsOptional()
    {
        // Existing code should work without specifying TimestampMs
        var sample = new TelemetrySample(
            GpuUtilizationPercent: 80.0,
            VramDedicatedMB: 2000.0,
            VramSharedMB: 200.0,
            CpuProcessPercent: 25.0,
            TopThreadCpuPercent: 10.0,
            RamWorkingSetMB: 1500.0,
            RamPrivateBytesMB: 1000.0,
            DiskReadMBps: 20.0);

        // TimestampMs should default to null
        Assert.Null(sample.TimestampMs);
    }

    [Fact]
    public void TelemetrySample_Empty_HasNullTimestamp()
    {
        var empty = TelemetrySample.Empty;
        Assert.Null(empty.TimestampMs);
    }

    [Fact]
    public void FakeTelemetrySampler_GetSampleAt_ReturnsClosestSample()
    {
        var sample0 = new TelemetrySample(null, null, null, 10.0, null, null, null, null, 0);
        var sample500 = new TelemetrySample(null, null, null, 20.0, null, null, null, null, 500);
        var sample1000 = new TelemetrySample(null, null, null, 30.0, null, null, null, null, 1000);

        var sampler = new FakeTelemetrySampler();
        sampler.AddSamples(sample0, sample500, sample1000);

        // Exact match
        Assert.Equal(10.0, sampler.GetSampleAt(0)?.CpuProcessPercent);
        Assert.Equal(20.0, sampler.GetSampleAt(500)?.CpuProcessPercent);
        Assert.Equal(30.0, sampler.GetSampleAt(1000)?.CpuProcessPercent);

        // Closest match
        Assert.Equal(10.0, sampler.GetSampleAt(100)?.CpuProcessPercent);   // Closer to 0
        Assert.Equal(20.0, sampler.GetSampleAt(400)?.CpuProcessPercent);   // Closer to 500
        Assert.Equal(20.0, sampler.GetSampleAt(600)?.CpuProcessPercent);   // Closer to 500
        Assert.Equal(30.0, sampler.GetSampleAt(900)?.CpuProcessPercent);   // Closer to 1000
        Assert.Equal(30.0, sampler.GetSampleAt(1500)?.CpuProcessPercent);  // Closest to 1000
    }
}

using System.Text.Json;
using ZykeMark.App.Cli.Collectors;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using Xunit;

namespace ZykeMark.Core.Tests;

public class SessionFlowTests
{
    [Fact]
    public void DemoRun_WritesMetadataChunksAndSummary()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("TestGame", "1.0.0", new RunConfig());

        var collector = new SimulatedCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            seed: 123,
            frameTimePatternMs: new[] { 16.67, 16.67, 33.3, 16.67 },
            cpuFrameTimeChance: 0.7,
            gpuFrameTimeChance: 0.7);

        collector.Collect(TimeSpan.FromSeconds(6));
        var summaryPath = sessionManager.StopSession(metadata.SessionId);

        var sessionFolder = Path.Combine(baseDir, metadata.SessionId);
        Assert.True(File.Exists(Path.Combine(sessionFolder, "metadata.json")));
        Assert.True(Directory.Exists(Path.Combine(sessionFolder, "chunks")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(sessionFolder, "chunks"), "chunk_*.json"));
        Assert.True(File.Exists(summaryPath));

        using var summaryStream = File.OpenRead(summaryPath);
        using var document = JsonDocument.Parse(summaryStream);
        var aggregates = document.RootElement.GetProperty("aggregates");

        Assert.True(aggregates.GetProperty("AvgFps").GetDouble() > 0);
        Assert.True(aggregates.GetProperty("AvgFrameTimeMs").GetDouble() > 0);
    }

    [Fact]
    public void StopSession_WithNoSamples_WritesZeroedSummary()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("EmptyGame", "1.0.0", new RunConfig());

        var summaryPath = sessionManager.StopSession(metadata.SessionId);

        Assert.True(File.Exists(summaryPath));
        using var summaryStream = File.OpenRead(summaryPath);
        using var document = JsonDocument.Parse(summaryStream);
        var aggregates = document.RootElement.GetProperty("aggregates");
        Assert.Equal(0, aggregates.GetProperty("FrameCount").GetInt32());
        Assert.Equal(0, aggregates.GetProperty("AvgFps").GetDouble());
    }
}

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

    [Fact]
    public void StartSession_WithCaptureTarget_PersistsCaptureTargetInSummary()
    {
        // Arrange
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var captureTarget = new CaptureTarget("game.exe", 12345, CaptureTarget.ModePid, "Game Window");

        // Act
        var metadata = sessionManager.StartSession("TestGame", "1.0.0", new RunConfig(), captureTarget);
        var summaryPath = sessionManager.StopSession(metadata.SessionId);

        // Assert - Verify CaptureTarget in metadata.json
        var sessionFolder = Path.Combine(baseDir, metadata.SessionId);
        var metadataPath = Path.Combine(sessionFolder, "metadata.json");
        Assert.True(File.Exists(metadataPath));

        using var metadataStream = File.OpenRead(metadataPath);
        using var metadataDoc = JsonDocument.Parse(metadataStream);
        var captureTargetProp = metadataDoc.RootElement.GetProperty("CaptureTarget");
        Assert.Equal("game.exe", captureTargetProp.GetProperty("ProcessName").GetString());
        Assert.Equal(12345, captureTargetProp.GetProperty("ProcessId").GetInt32());
        Assert.Equal("pid", captureTargetProp.GetProperty("SelectionMode").GetString());
        Assert.Equal("Game Window", captureTargetProp.GetProperty("WindowTitle").GetString());

        // Assert - Verify CaptureTarget in summary.json
        Assert.True(File.Exists(summaryPath));
        using var summaryStream = File.OpenRead(summaryPath);
        using var summaryDoc = JsonDocument.Parse(summaryStream);
        var summaryMetadata = summaryDoc.RootElement.GetProperty("metadata");
        var summaryCaptureTarget = summaryMetadata.GetProperty("CaptureTarget");
        Assert.Equal("game.exe", summaryCaptureTarget.GetProperty("ProcessName").GetString());
        Assert.Equal(12345, summaryCaptureTarget.GetProperty("ProcessId").GetInt32());
        Assert.Equal("pid", summaryCaptureTarget.GetProperty("SelectionMode").GetString());
    }

    [Fact]
    public void CaptureTarget_ToDisplayString_ReturnsFormattedString()
    {
        // Arrange
        var targetWithPid = new CaptureTarget("game.exe", 12345, CaptureTarget.ModePid, "Game Window");
        var targetByName = new CaptureTarget("app.exe", null, CaptureTarget.ModeName, null);
        var targetAuto = new CaptureTarget(null, 9999, CaptureTarget.ModeAuto, null);

        // Act & Assert
        Assert.Contains("game.exe", targetWithPid.ToDisplayString());
        Assert.Contains("PID 12345", targetWithPid.ToDisplayString());
        Assert.Contains("Game Window", targetWithPid.ToDisplayString());
        Assert.Contains("by PID", targetWithPid.ToDisplayString());

        Assert.Contains("app.exe", targetByName.ToDisplayString());
        Assert.Contains("by name", targetByName.ToDisplayString());

        Assert.Contains("PID 9999", targetAuto.ToDisplayString());
        Assert.Contains("auto", targetAuto.ToDisplayString());
    }
}

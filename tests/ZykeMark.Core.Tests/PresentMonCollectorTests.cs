using System.Text.Json;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using ZykeMark.Infrastructure.Collectors;
using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class PresentMonCollectorTests
{
    [Fact]
    public void Collector_WritesChunksAndSummary_FromFixture()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("RealGame", "1.0.0", new RunConfig());

        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "presentmon_sample_alt_header.csv");
        var lines = File.ReadAllLines(fixturePath);
        var runner = new FakePresentMonRunner(lines);
        var parser = new PresentMonCsvParser();
        var runOptions = new PresentMonRunOptions("PresentMon.exe", "MyGame.exe", null, 1);

        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            runner,
            parser,
            runOptions);

        collector.Collect(TimeSpan.FromSeconds(1));
        var summaryPath = sessionManager.StopSession(metadata.SessionId);

        var sessionFolder = Path.Combine(baseDir, metadata.SessionId);
        Assert.True(File.Exists(Path.Combine(sessionFolder, "metadata.json")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(sessionFolder, "chunks"), "chunk_*.json"));
        Assert.True(File.Exists(summaryPath));

        using var summaryStream = File.OpenRead(summaryPath);
        using var document = JsonDocument.Parse(summaryStream);
        var aggregates = document.RootElement.GetProperty("aggregates");
        Assert.True(aggregates.GetProperty("AvgFps").GetDouble() > 0);
        Assert.True(aggregates.GetProperty("AvgFrameTimeMs").GetDouble() > 0);
    }
}

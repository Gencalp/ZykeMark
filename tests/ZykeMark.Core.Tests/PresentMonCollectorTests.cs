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

    [Fact]
    public void Collector_SkipsPreambleLines_BeforeHeader()
    {
        // Regression test: PresentMon stdout may include preamble lines like "Started recording."
        // before the actual CSV header. The collector should skip these lines.
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("RealGame", "1.0.0", new RunConfig());

        // Simulate PresentMon output with preamble lines before the CSV header
        var linesWithPreamble = new[]
        {
            "Started recording.",
            "Stopped recording.",
            "Application,ProcessID,SwapChainAddress,Runtime,CPUStartTime,FrameTime,MsCPUBusy,MsGPUTime",
            "MyGame.exe,4242,0x1,DXGI,1000.0,16.67,5.2,6.1",
            "MyGame.exe,4242,0x1,DXGI,1016.67,16.67,5.1,6.0",
            "MyGame.exe,4242,0x1,DXGI,1033.34,16.67,5.0,5.9"
        };

        var runner = new FakePresentMonRunner(linesWithPreamble);
        var parser = new PresentMonCsvParser();
        var runOptions = new PresentMonRunOptions("PresentMon.exe", "MyGame.exe", null, 1);

        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            runner,
            parser,
            runOptions);

        // This should not throw - previously it would throw because "Started recording."
        // was being parsed as the header
        var samples = collector.Collect(TimeSpan.FromSeconds(1));

        // Verify samples were collected successfully
        Assert.Equal(3, samples.Count);
        Assert.All(samples, sample => Assert.True(sample.FrameTimeMs > 0));

        sessionManager.StopSession(metadata.SessionId);
    }

    [Fact]
    public void Collector_LogsDiagnostics_WhenNoSamplesParsed()
    {
        // Test that diagnostic logging is invoked when all data rows fail to parse
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("RealGame", "1.0.0", new RunConfig());

        // CSV with valid header but unparseable data rows (invalid timestamps)
        var linesWithBadData = new[]
        {
            "Application,ProcessID,SwapChainAddress,Runtime,CPUStartTime,FrameTime,MsCPUBusy,MsGPUTime",
            "MyGame.exe,4242,0x1,DXGI,invalid_timestamp,16.67,5.2,6.1",
            "MyGame.exe,4242,0x1,DXGI,not_a_number,16.67,5.1,6.0",
            "MyGame.exe,4242,0x1,DXGI,bad,16.67,5.0,5.9"
        };

        var logMessages = new List<string>();
        void Logger(string message) => logMessages.Add(message);

        var runner = new FakePresentMonRunner(linesWithBadData);
        var parser = new PresentMonCsvParser();
        var runOptions = new PresentMonRunOptions("PresentMon.exe", "MyGame.exe", null, 1);

        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            runner,
            parser,
            runOptions,
            logger: Logger);

        var samples = collector.Collect(TimeSpan.FromSeconds(1));

        // Should have no samples since all data rows have invalid timestamps
        Assert.Empty(samples);

        // Diagnostic logs should be present
        Assert.Contains(logMessages, m => m.Contains("WARNING: 0 samples parsed"));
        Assert.Contains(logMessages, m => m.Contains("Header columns:"));
        Assert.Contains(logMessages, m => m.Contains("CPUStartTime"));
        Assert.Contains(logMessages, m => m.Contains("Total data rows: 3"));
        Assert.Contains(logMessages, m => m.Contains("TimestampParseFailed"));

        sessionManager.StopSession(metadata.SessionId);
    }
}

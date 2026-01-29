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

    [Fact]
    public void Collector_ParsesCsvFromFile_WhenSessionFolderProvided()
    {
        // Regression test: When sessionFolder is provided, the collector should parse
        // the CSV file directly (from result.CsvPath), not from stdout.
        // This was the original bug - stdout contains only preamble messages in file mode.
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("RealGame", "1.0.0", new RunConfig());

        var sessionFolder = Path.Combine(baseDir, metadata.SessionId);
        var csvPath = Path.Combine(sessionFolder, "presentmon.csv");

        // Simulate PresentMon CSV output with header and data rows
        var csvLines = new[]
        {
            "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,AnimationTime",
            "msedge.exe,9112,0x1E027F47C70,DXGI,1,256,0,Hardware Composed: Independent Flip,50.2753,20.8094,20.5596,0.2498,6.5586,15.7191,3.8256,11.8935,23.6925,33.3071,NA,50.2753",
            "msedge.exe,9112,0x1E027F47C70,DXGI,1,256,0,Hardware Composed: Independent Flip,71.0847,33.5327,33.2808,0.2519,31.9800,2.9410,2.2812,0.6598,36.1902,50.0480,NA,71.0847",
            "msedge.exe,9112,0x1E027F47C70,DXGI,1,256,0,Hardware Composed: Independent Flip,104.6174,16.6667,16.3333,0.3334,12.0000,8.5000,4.2500,4.2500,20.0000,16.6667,NA,104.6174"
        };

        var logMessages = new List<string>();
        void Logger(string message) => logMessages.Add(message);

        // Create a FakePresentMonRunner that writes to the CSV file (simulating file output mode)
        var runner = new FakePresentMonRunner(csvLines, csvPath);
        var parser = new PresentMonCsvParser();
        var runOptions = new PresentMonRunOptions("PresentMon.exe", null, 9112, 1);

        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            runner,
            parser,
            runOptions,
            sessionFolder: sessionFolder,
            logger: Logger);

        var samples = collector.Collect(TimeSpan.FromSeconds(1));

        // Verify samples were collected from the CSV file
        Assert.Equal(3, samples.Count);
        Assert.All(samples, sample => Assert.True(sample.FrameTimeMs > 0));

        // Verify the file-based parsing logs
        Assert.Contains(logMessages, m => m.Contains("CSV path from runner:"));
        Assert.Contains(logMessages, m => m.Contains("CSV file exists:"));
        Assert.Contains(logMessages, m => m.Contains("parsedSamplesCount=3"));
        Assert.Contains(logMessages, m => m.Contains("totalDataRows=3"));

        sessionManager.StopSession(metadata.SessionId);
    }

    [Fact]
    public void Collector_ThrowsWithDiagnostics_WhenFileHasDataButNoParsedSamples()
    {
        // Test that the collector fails with a clear error message when:
        // - totalDataRows > 0 (file has data)
        // - parsedSamplesCount == 0 (all rows failed to parse)
        var baseDir = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        var store = new FileSystemLocalStore(baseDir);
        var sessionManager = new SessionManager(store, new ZykeMarkAggregator());
        var metadata = sessionManager.StartSession("RealGame", "1.0.0", new RunConfig());

        var sessionFolder = Path.Combine(baseDir, metadata.SessionId);
        var csvPath = Path.Combine(sessionFolder, "presentmon.csv");

        // CSV with valid header but unparseable data rows
        var csvLines = new[]
        {
            "Application,ProcessID,SwapChainAddress,Runtime,CPUStartTime,FrameTime,MsCPUBusy,MsGPUTime",
            "MyGame.exe,4242,0x1,DXGI,invalid_timestamp,16.67,5.2,6.1",
            "MyGame.exe,4242,0x1,DXGI,not_a_number,16.67,5.1,6.0",
            "MyGame.exe,4242,0x1,DXGI,bad,16.67,5.0,5.9"
        };

        var logMessages = new List<string>();
        void Logger(string message) => logMessages.Add(message);

        var runner = new FakePresentMonRunner(csvLines, csvPath);
        var parser = new PresentMonCsvParser();
        var runOptions = new PresentMonRunOptions("PresentMon.exe", "MyGame.exe", null, 1);

        var collector = new PresentMonCollector(
            store,
            metadata.SessionId,
            metadata.StartedAtUtc,
            runner,
            parser,
            runOptions,
            sessionFolder: sessionFolder,
            logger: Logger);

        // Should throw with clear error message
        var ex = Assert.Throws<InvalidOperationException>(() => collector.Collect(TimeSpan.FromSeconds(1)));

        Assert.Contains("Failed to parse any samples", ex.Message);
        Assert.Contains("3 data rows", ex.Message);
        Assert.Contains("0 samples were parsed", ex.Message);
        Assert.Contains("Header columns:", ex.Message);

        sessionManager.StopSession(metadata.SessionId);
    }
}

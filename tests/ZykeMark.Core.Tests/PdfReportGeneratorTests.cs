using System.Text.Json;
using ZykeMark.Core.Models;
using ZykeMark.Infrastructure.Reporting;
using Xunit;

namespace ZykeMark.Core.Tests;

public class PdfReportGeneratorTests
{
    [Fact]
    public void Generate_CreatesPdf_WithSummaryOnly()
    {
        var sessionFolder = CreateSessionFolder();
        WriteSummary(sessionFolder, includeCpuGpu: true, frameCount: 120);

        var pdfPath = GenerateReport(sessionFolder);

        Assert.True(File.Exists(pdfPath));
        Assert.True(new FileInfo(pdfPath).Length > 5_000);
    }

    [Fact]
    public void Generate_CreatesPdf_WithChunks()
    {
        var sessionFolder = CreateSessionFolder();
        WriteSummary(sessionFolder, includeCpuGpu: true, frameCount: 600);
        WriteChunk(sessionFolder);

        var pdfPath = GenerateReport(sessionFolder);

        Assert.True(File.Exists(pdfPath));
        Assert.True(new FileInfo(pdfPath).Length > 5_000);
    }

    [Fact]
    public void Generate_DoesNotThrow_ForLowSampleCounts()
    {
        var sessionFolder = CreateSessionFolder();
        WriteSummary(sessionFolder, includeCpuGpu: false, frameCount: 120);

        var pdfPath = GenerateReport(sessionFolder);

        Assert.True(File.Exists(pdfPath));
    }

    private static string GenerateReport(string sessionFolder)
    {
        var tokensPath = ReportGenerator.FindBrandTokensPath(AppContext.BaseDirectory);
        var theme = BrandTheme.LoadFromTokens(tokensPath);
        var generator = new ReportGenerator(theme);
        return generator.Generate(sessionFolder);
    }

    private static string CreateSessionFolder()
    {
        var sessionFolder = Path.Combine(Path.GetTempPath(), "ZykeMarkTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionFolder);
        return sessionFolder;
    }

    private static void WriteSummary(string sessionFolder, bool includeCpuGpu, int frameCount)
    {
        var metadata = new SessionMetadata(
            SessionId: Guid.NewGuid().ToString("N"),
            StartedAtUtc: DateTime.UtcNow.AddMinutes(-1),
            EndedAtUtc: DateTime.UtcNow,
            DurationMs: 60_000,
            GameName: "SampleGame",
            BuildVersion: "1.0.0",
            RunConfig: new RunConfig("DX12", "1920x1080", "High"));

        var aggregates = new SessionAggregates(
            FrameCount: frameCount,
            DurationMs: 60_000,
            AvgFps: 60,
            AvgFrameTimeMs: 16.67,
            P99FrameTimeMs: 25.0,
            OnePercentLowFps: 40.0,
            PointOnePercentLowFps: 35.0,
            AvgCpuFrameTimeMs: includeCpuGpu ? 12.5 : null,
            AvgGpuFrameTimeMs: includeCpuGpu ? 14.2 : null);

        var payload = new
        {
            metadata,
            aggregates
        };

        var summaryPath = Path.Combine(sessionFolder, "summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(payload));
    }

    private static void WriteChunk(string sessionFolder)
    {
        var chunksFolder = Path.Combine(sessionFolder, "chunks");
        Directory.CreateDirectory(chunksFolder);

        var chunk = new RawSampleChunk(
            ChunkIndex: 1,
            StartUtc: DateTime.UtcNow,
            EndUtc: DateTime.UtcNow.AddSeconds(1),
            Samples: new[]
            {
                new FrameSample(0, 16.67),
                new FrameSample(16.67, 52.5),
                new FrameSample(69.17, 120.0)
            });

        var chunkPath = Path.Combine(chunksFolder, "chunk_0001.json");
        File.WriteAllText(chunkPath, JsonSerializer.Serialize(chunk));
    }
}

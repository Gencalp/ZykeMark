using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class PresentMonCsvParserTests
{
    [Fact]
    public void Parser_ReadsFrameSamples_FromFixture()
    {
        AssertFixtureParses("presentmon_sample.csv");
        AssertFixtureParses("presentmon_sample_alt_header.csv");
        AssertFixtureParses("presentmon_sample_timeinseconds.csv");
        AssertFixtureParses("presentmon_sample_cpustarttime.csv");
        AssertFixtureParses("presentmon_sample_qpctime.csv");
    }

    [Fact]
    public void Parser_ConvertsTimeInSecondsToMilliseconds()
    {
        var parser = new PresentMonCsvParser();
        parser.ParseHeader("Application,ProcessID,TimeInSeconds,MsBetweenPresents");

        Assert.True(parser.TryParse("Game.exe,1234,1.0,16.67", out var sample));
        Assert.Equal(1000.0, sample.TimestampMs, precision: 2);

        Assert.True(parser.TryParse("Game.exe,1234,2.5,16.67", out sample));
        Assert.Equal(2500.0, sample.TimestampMs, precision: 2);
    }

    [Fact]
    public void Parser_ParsesCPUStartTimeAsMilliseconds()
    {
        var parser = new PresentMonCsvParser();
        parser.ParseHeader("Application,ProcessID,CPUStartTime,FrameTime");

        Assert.True(parser.TryParse("Game.exe,1234,1000.0,16.67", out var sample));
        Assert.Equal(1000.0, sample.TimestampMs, precision: 2);
        Assert.Equal(16.67, sample.FrameTimeMs, precision: 2);

        Assert.True(parser.TryParse("Game.exe,1234,2500.0,16.67", out sample));
        Assert.Equal(2500.0, sample.TimestampMs, precision: 2);
    }

    [Fact]
    public void Parser_ErrorMessage_ListsHeaderColumns()
    {
        var parser = new PresentMonCsvParser();
        var exception = Assert.Throws<InvalidOperationException>(
            () => parser.ParseHeader("Application,ProcessID,UnknownColumn,SomeOtherColumn"));

        Assert.Contains("Header columns:", exception.Message);
        Assert.Contains("Application", exception.Message);
        Assert.Contains("ProcessID", exception.Message);
        Assert.Contains("UnknownColumn", exception.Message);
        Assert.Contains("CPUStartTime", exception.Message);
    }

    private static void AssertFixtureParses(string fixtureFileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName);
        var lines = File.ReadAllLines(fixturePath);

        var parser = new PresentMonCsvParser();
        parser.ParseHeader(lines[0]);

        var samples = new List<ZykeMark.Core.Models.FrameSample>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (parser.TryParse(lines[i], out var sample))
            {
                samples.Add(sample);
            }
        }

        Assert.NotEmpty(samples);
        Assert.True(samples.Zip(samples.Skip(1), (a, b) => b.TimestampMs > a.TimestampMs).All(x => x));
        Assert.All(samples, sample => Assert.True(sample.FrameTimeMs > 0));
        Assert.Contains(samples, sample => sample.CpuFrameTimeMs is null);
        Assert.Contains(samples, sample => sample.GpuFrameTimeMs is null);
    }

    [Fact]
    public void DiscoverHeaderLine_SkipsPreambleAndFindsHeader()
    {
        // Regression test: CSV file with preamble lines like "Started recording."
        var lines = new[]
        {
            "Started recording.",
            "Stopped recording.",
            "Application,ProcessID,SwapChainAddress,Runtime,CPUStartTime,FrameTime",
            "MyGame.exe,1234,0x1,DXGI,1000.0,16.67"
        };

        var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines, out var headerLineIndex);

        Assert.NotNull(headerLine);
        Assert.Equal(2, headerLineIndex);
        Assert.Contains("Application", headerLine);
        Assert.Contains("ProcessID", headerLine);
    }

    [Fact]
    public void DiscoverHeaderLine_ReturnsNullForInvalidContent()
    {
        var lines = new[]
        {
            "Started recording.",
            "Stopped recording.",
            "Some other garbage"
        };

        var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines, out var headerLineIndex);

        Assert.Null(headerLine);
        Assert.Equal(-1, headerLineIndex);
    }

    [Fact]
    public void DiscoverHeaderLine_FindsHeaderAtFirstLine()
    {
        var lines = new[]
        {
            "Application,ProcessID,CPUStartTime,FrameTime",
            "MyGame.exe,1234,1000.0,16.67"
        };

        var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines, out var headerLineIndex);

        Assert.NotNull(headerLine);
        Assert.Equal(0, headerLineIndex);
    }

    [Fact]
    public void IsValidCsvHeaderLine_ReturnsTrueForValidHeader()
    {
        Assert.True(PresentMonCsvParser.IsValidCsvHeaderLine("Application,ProcessID,CPUStartTime,FrameTime"));
        Assert.True(PresentMonCsvParser.IsValidCsvHeaderLine("Application,ProcessID,SwapChainAddress,Runtime"));
    }

    [Fact]
    public void IsValidCsvHeaderLine_ReturnsFalseForPreambleLines()
    {
        Assert.False(PresentMonCsvParser.IsValidCsvHeaderLine("Started recording."));
        Assert.False(PresentMonCsvParser.IsValidCsvHeaderLine("Stopped recording."));
        Assert.False(PresentMonCsvParser.IsValidCsvHeaderLine(""));
        Assert.False(PresentMonCsvParser.IsValidCsvHeaderLine(null));
        Assert.False(PresentMonCsvParser.IsValidCsvHeaderLine("   "));
    }

    [Fact]
    public void Parser_ParsesFixtureWithPreamble_UsingDiscoverHeaderLine()
    {
        // Regression test: Ensure parsing succeeds for CSV with preamble
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "presentmon_sample_with_preamble.csv");
        var lines = File.ReadAllLines(fixturePath);

        // Discover the header line (skip preamble)
        var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines, out var headerLineIndex);
        Assert.NotNull(headerLine);
        Assert.True(headerLineIndex >= 0, "Header should be found");

        // Parse using discovered header
        var parser = new PresentMonCsvParser();
        parser.ParseHeader(headerLine!);

        var samples = new List<ZykeMark.Core.Models.FrameSample>();
        for (var i = headerLineIndex + 1; i < lines.Length; i++)
        {
            if (parser.TryParse(lines[i], out var sample))
            {
                samples.Add(sample);
            }
        }

        // Verify we parsed data rows successfully
        Assert.NotEmpty(samples);
        Assert.True(samples.Count >= 5, $"Expected at least 5 samples, got {samples.Count}");
        Assert.All(samples, sample => Assert.True(sample.FrameTimeMs > 0));
        Assert.All(samples, sample => Assert.True(sample.TimestampMs > 0));
    }
}

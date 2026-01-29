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
}

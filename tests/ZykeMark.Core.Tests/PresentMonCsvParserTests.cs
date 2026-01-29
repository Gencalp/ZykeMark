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

using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;

namespace ZykeMark.App.Cli.Collectors;

public sealed class SimulatedCollector : ICollector
{
    private const double ChunkDurationMs = 5000;
    private const int ChunkMaxFrames = 10_000;

    private readonly ILocalStore _localStore;
    private readonly string _sessionId;
    private readonly DateTime _sessionStartUtc;
    private readonly int _seed;
    private readonly IReadOnlyList<double> _frameTimePatternMs;
    private readonly double _cpuFrameTimeChance;
    private readonly double _gpuFrameTimeChance;

    public SimulatedCollector(
        ILocalStore localStore,
        string sessionId,
        DateTime sessionStartUtc,
        int seed,
        IReadOnlyList<double> frameTimePatternMs,
        double cpuFrameTimeChance = 0.6,
        double gpuFrameTimeChance = 0.6)
    {
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("Session ID is required.", nameof(sessionId))
            : sessionId;
        _sessionStartUtc = sessionStartUtc;
        _seed = seed;
        _frameTimePatternMs = frameTimePatternMs is { Count: > 0 }
            ? frameTimePatternMs
            : throw new ArgumentException("Frame time pattern must contain at least one value.", nameof(frameTimePatternMs));
        _cpuFrameTimeChance = cpuFrameTimeChance;
        _gpuFrameTimeChance = gpuFrameTimeChance;
    }

    public IReadOnlyList<FrameSample> Collect(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var random = new Random(_seed);
        var samples = new List<FrameSample>();
        var chunkSamples = new List<FrameSample>();
        double timestampMs = 0;
        double chunkStartTimestampMs = 0;
        var patternIndex = 0;
        var chunkIndex = 1;

        while (timestampMs < duration.TotalMilliseconds)
        {
            var frameTimeMs = _frameTimePatternMs[patternIndex];
            patternIndex = (patternIndex + 1) % _frameTimePatternMs.Count;

            double? cpuFrameTimeMs = null;
            double? gpuFrameTimeMs = null;

            if (random.NextDouble() <= _cpuFrameTimeChance)
            {
                cpuFrameTimeMs = Math.Max(0.1, frameTimeMs * (0.8 + random.NextDouble() * 0.4));
            }

            if (random.NextDouble() <= _gpuFrameTimeChance)
            {
                gpuFrameTimeMs = Math.Max(0.1, frameTimeMs * (0.85 + random.NextDouble() * 0.35));
            }

            var sample = new FrameSample(timestampMs, frameTimeMs, cpuFrameTimeMs, gpuFrameTimeMs);
            samples.Add(sample);
            chunkSamples.Add(sample);

            timestampMs += frameTimeMs;

            if (timestampMs - chunkStartTimestampMs >= ChunkDurationMs || chunkSamples.Count >= ChunkMaxFrames)
            {
                FlushChunk(chunkSamples, chunkIndex, chunkStartTimestampMs, timestampMs);
                chunkIndex++;
                chunkSamples = new List<FrameSample>();
                chunkStartTimestampMs = timestampMs;
            }
        }

        if (chunkSamples.Count > 0)
        {
            FlushChunk(chunkSamples, chunkIndex, chunkStartTimestampMs, timestampMs);
        }

        return samples;
    }

    private void FlushChunk(List<FrameSample> samples, int chunkIndex, double chunkStartTimestampMs, double chunkEndTimestampMs)
    {
        var chunk = new RawSampleChunk(
            chunkIndex,
            _sessionStartUtc.AddMilliseconds(chunkStartTimestampMs),
            _sessionStartUtc.AddMilliseconds(chunkEndTimestampMs),
            samples.ToArray());

        _localStore.AppendChunk(_sessionId, chunk);
    }
}

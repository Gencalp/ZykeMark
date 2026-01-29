using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;
using ZykeMark.Infrastructure.PresentMon;

namespace ZykeMark.Infrastructure.Collectors;

public sealed class PresentMonCollector : ICollector
{
    private const double ChunkDurationMs = 5000;
    private const int ChunkMaxFrames = 10_000;

    private readonly ILocalStore _localStore;
    private readonly string _sessionId;
    private readonly string? _sessionFolder;
    private readonly DateTime _sessionStartUtc;
    private readonly IPresentMonRunner _runner;
    private readonly PresentMonCsvParser _parser;
    private readonly PresentMonRunOptions _runOptions;

    public PresentMonCollector(
        ILocalStore localStore,
        string sessionId,
        DateTime sessionStartUtc,
        IPresentMonRunner runner,
        PresentMonCsvParser parser,
        PresentMonRunOptions runOptions,
        string? sessionFolder = null)
    {
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? throw new ArgumentException("Session ID is required.", nameof(sessionId))
            : sessionId;
        _sessionFolder = sessionFolder;
        _sessionStartUtc = sessionStartUtc;
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _runOptions = runOptions ?? throw new ArgumentNullException(nameof(runOptions));
    }

    public IReadOnlyList<FrameSample> Collect(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
        }

        var samples = new List<FrameSample>();
        var chunkSamples = new List<FrameSample>();
        double chunkStartTimestampMs = 0;
        var chunkIndex = 1;
        var firstDataTimestampMs = (double?)null;
        var headerParsed = false;

        // Construct run options with session info for file output and unique session name
        var runOptionsWithSession = _runOptions with
        {
            DurationSeconds = (int)Math.Ceiling(duration.TotalSeconds),
            SessionId = _sessionId,
            SessionFolder = _sessionFolder
        };

        using var cts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(5)); // Add buffer for process cleanup
        var lines = _runner.RunAsync(runOptionsWithSession, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        try
        {
            while (lines.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var line = lines.Current;

                if (!headerParsed)
                {
                    // Skip preamble lines (e.g., "Started recording.", "Stopped recording.")
                    // Only treat the line as a header if it looks like a valid CSV header
                    if (!PresentMonCsvParser.IsValidCsvHeaderLine(line))
                    {
                        continue;
                    }

                    _parser.ParseHeader(line);
                    headerParsed = true;
                    continue;
                }

                if (!_parser.TryParse(line, out var sample))
                {
                    continue;
                }

                if (firstDataTimestampMs is null)
                {
                    firstDataTimestampMs = sample.TimestampMs;
                    chunkStartTimestampMs = 0;
                }

                var relativeTimestampMs = sample.TimestampMs - firstDataTimestampMs.GetValueOrDefault();
                var normalizedSample = sample with { TimestampMs = relativeTimestampMs };

                samples.Add(normalizedSample);
                chunkSamples.Add(normalizedSample);

                if (relativeTimestampMs - chunkStartTimestampMs >= ChunkDurationMs || chunkSamples.Count >= ChunkMaxFrames)
                {
                    FlushChunk(chunkSamples, chunkIndex, chunkStartTimestampMs, relativeTimestampMs);
                    chunkIndex++;
                    chunkSamples = new List<FrameSample>();
                    chunkStartTimestampMs = relativeTimestampMs;
                }
            }
        }
        finally
        {
            lines.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (chunkSamples.Count > 0)
        {
            var finalTimestamp = samples.Count > 0 ? samples[^1].TimestampMs : 0;
            FlushChunk(chunkSamples, chunkIndex, chunkStartTimestampMs, finalTimestamp);
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

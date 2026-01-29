using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;
using ZykeMark.Infrastructure.PresentMon;

namespace ZykeMark.Infrastructure.Collectors;

public sealed class PresentMonCollector : ICollector
{
    private const double ChunkDurationMs = 5000;
    private const int ChunkMaxFrames = 10_000;
    private const int MaxSkippedRowsToTrack = 10;
    private const int MaxDataRowsToTrack = 3;

    private readonly ILocalStore _localStore;
    private readonly string _sessionId;
    private readonly string? _sessionFolder;
    private readonly DateTime _sessionStartUtc;
    private readonly IPresentMonRunner _runner;
    private readonly PresentMonCsvParser _parser;
    private readonly PresentMonRunOptions _runOptions;
    private readonly Action<string>? _logger;

    public PresentMonCollector(
        ILocalStore localStore,
        string sessionId,
        DateTime sessionStartUtc,
        IPresentMonRunner runner,
        PresentMonCsvParser parser,
        PresentMonRunOptions runOptions,
        string? sessionFolder = null,
        Action<string>? logger = null)
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
        _logger = logger;
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

        // Diagnostics tracking
        var totalDataRows = 0;
        var totalSkippedRows = 0;
        var firstDataRows = new List<string>();
        var skippedRowDiagnostics = new List<(string Row, ParseFailureReason Reason)>();
        var skipReasonCounts = new Dictionary<ParseFailureReason, int>();

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

                // Track data rows for diagnostics
                totalDataRows++;
                if (firstDataRows.Count < MaxDataRowsToTrack)
                {
                    firstDataRows.Add(line);
                }

                if (!_parser.TryParse(line, out var sample, out var failureReason))
                {
                    // Track skipped rows for diagnostics
                    totalSkippedRows++;
                    skipReasonCounts[failureReason] = skipReasonCounts.GetValueOrDefault(failureReason) + 1;
                    if (skippedRowDiagnostics.Count < MaxSkippedRowsToTrack)
                    {
                        skippedRowDiagnostics.Add((line, failureReason));
                    }
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

        // Always log parsing summary so we can diagnose issues
        Log($"[Collector] Parsing complete: parsedSamplesCount={samples.Count}, totalDataRows={totalDataRows}, skippedRows={totalSkippedRows}");
        if (totalSkippedRows > 0)
        {
            var reasonCounts = skipReasonCounts
                .Select(kvp => $"{kvp.Key}={kvp.Value}")
                .ToArray();
            Log($"[Collector] Skip reasons: {string.Join(", ", reasonCounts)}");
        }

        // Log detailed diagnostics if no samples were collected
        if (samples.Count == 0 && totalDataRows > 0)
        {
            LogParsingDiagnostics(totalDataRows, firstDataRows, skippedRowDiagnostics);
        }

        return samples;
    }

    private void LogParsingDiagnostics(
        int totalDataRows,
        List<string> firstDataRows,
        List<(string Row, ParseFailureReason Reason)> skippedRowDiagnostics)
    {
        Log("[Collector] WARNING: 0 samples parsed from PresentMon CSV data.");
        Log($"[Collector] Header columns: [{string.Join(", ", _parser.HeaderColumns)}]");
        Log($"[Collector] Total data rows: {totalDataRows}");

        if (firstDataRows.Count > 0)
        {
            Log("[Collector] First data rows:");
            for (var i = 0; i < firstDataRows.Count; i++)
            {
                Log($"[Collector]   Row {i + 1}: {firstDataRows[i]}");
            }
        }

        if (skippedRowDiagnostics.Count > 0)
        {
            Log($"[Collector] First {skippedRowDiagnostics.Count} skipped rows and reasons:");
            foreach (var (row, reason) in skippedRowDiagnostics)
            {
                Log($"[Collector]   Reason: {reason}");
                Log($"[Collector]     Row: {row}");
            }
        }

        Log("[Collector] Possible causes: numeric parsing failed due to locale mismatch (e.g., comma vs dot decimal separator), delimiter mismatch, or missing required columns.");
    }

    private void Log(string message)
    {
        _logger?.Invoke(message);
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

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

    /// <summary>
    /// Gets the data quality information from the last collection run.
    /// Available after calling <see cref="Collect"/>.
    /// </summary>
    public DataQuality? LastCollectionDataQuality { get; private set; }

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

        // Construct run options with session info for file output and unique session name
        var runOptionsWithSession = _runOptions with
        {
            DurationSeconds = (int)Math.Ceiling(duration.TotalSeconds),
            SessionId = _sessionId,
            SessionFolder = _sessionFolder
        };

        // Determine if we're using file output mode (when session folder is set)
        var useFileOutput = !string.IsNullOrWhiteSpace(_sessionFolder);

        if (useFileOutput)
        {
            // File output mode: run PresentMon, then parse the CSV file directly
            return CollectFromFile(duration, runOptionsWithSession);
        }
        else
        {
            // Stdout mode: stream lines from PresentMon (for backward compatibility / tests)
            return CollectFromStdout(duration, runOptionsWithSession);
        }
    }

    /// <summary>
    /// Collects samples by running PresentMon and parsing the CSV file output directly.
    /// This is the preferred mode when a session folder is configured.
    /// </summary>
    private IReadOnlyList<FrameSample> CollectFromFile(TimeSpan duration, PresentMonRunOptions runOptions)
    {
        using var cts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(10)); // Add buffer for process cleanup + file write

        // Run PresentMon to file and get the result
        var result = _runner.RunToFileAsync(runOptions, cts.Token).GetAwaiter().GetResult();

        // Capture data quality from the run result
        LastCollectionDataQuality = DataQuality.Create(
            result.EtwEventsLostCount,
            result.RawWarnings?.ToList());

        if (result.EtwEventsLostCount.HasValue)
        {
            Log($"[Collector] ETW events lost: {result.EtwEventsLostCount.Value}, Risk level: {LastCollectionDataQuality.EtwEventsLostRiskLevel}");
        }

        // Validate we got a CSV path
        if (string.IsNullOrWhiteSpace(result.CsvPath))
        {
            Log($"[Collector] ERROR: No CSV path returned from PresentMon runner. Exit code: {result.ExitCode}");
            Log($"[Collector] stdout: {result.StdOut}");
            Log($"[Collector] stderr: {result.StdErr}");
            return Array.Empty<FrameSample>();
        }

        var csvPath = result.CsvPath;
        Log($"[Collector] CSV path from runner: {csvPath}");

        // Verify file exists and log details
        if (!File.Exists(csvPath))
        {
            Log($"[Collector] ERROR: CSV file does not exist: {csvPath}");
            return Array.Empty<FrameSample>();
        }

        var fileInfo = new FileInfo(csvPath);
        Log($"[Collector] CSV file exists: {csvPath}, size: {fileInfo.Length} bytes");

        // Read all lines from the CSV file
        var lines = File.ReadAllLines(csvPath);
        Log($"[Collector] CSV file contains {lines.Length} total lines");

        // Log first 3 lines for debugging
        for (var i = 0; i < Math.Min(3, lines.Length); i++)
        {
            Log($"[Collector] CSV line {i}: {lines[i]}");
        }

        // Discover header using robust header detection
        var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines.Take(50), out var headerLineIndex);
        if (headerLine is null)
        {
            Log("[Collector] ERROR: No valid CSV header found in file");
            if (lines.Length > 0)
            {
                Log($"[Collector] First 5 lines: {string.Join(Environment.NewLine, lines.Take(5))}");
            }
            return Array.Empty<FrameSample>();
        }

        Log($"[Collector] Header found at line {headerLineIndex}: {headerLine}");
        _parser.ParseHeader(headerLine);

        // Calculate data rows (lines after header)
        var totalDataRows = lines.Length - headerLineIndex - 1;
        Log($"[Collector] Total data rows in file: {totalDataRows}");

        // Parse all data rows
        var samples = new List<FrameSample>();
        var chunkSamples = new List<FrameSample>();
        double chunkStartTimestampMs = 0;
        var chunkIndex = 1;
        var firstDataTimestampMs = (double?)null;

        // Diagnostics tracking
        var parsedSamplesCount = 0;
        var skippedRows = 0;
        var firstDataRows = new List<string>();
        var skippedRowDiagnostics = new List<(string Row, ParseFailureReason Reason)>();
        var skipReasonCounts = new Dictionary<ParseFailureReason, int>();

        // Start parsing from the line after the header
        for (var i = headerLineIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];

            // Track first few data rows for diagnostics
            if (firstDataRows.Count < MaxDataRowsToTrack)
            {
                firstDataRows.Add(line);
            }

            if (!_parser.TryParse(line, out var sample, out var failureReason))
            {
                skippedRows++;
                skipReasonCounts[failureReason] = skipReasonCounts.GetValueOrDefault(failureReason) + 1;
                if (skippedRowDiagnostics.Count < MaxSkippedRowsToTrack)
                {
                    skippedRowDiagnostics.Add((line, failureReason));
                }
                continue;
            }

            parsedSamplesCount++;

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

        // Flush remaining samples
        if (chunkSamples.Count > 0)
        {
            var finalTimestamp = samples.Count > 0 ? samples[^1].TimestampMs : 0;
            FlushChunk(chunkSamples, chunkIndex, chunkStartTimestampMs, finalTimestamp);
        }

        // Log parsing summary
        Log($"[Collector] Parsing complete: parsedSamplesCount={parsedSamplesCount}, totalDataRows={totalDataRows}, skippedRows={skippedRows}");
        if (skippedRows > 0)
        {
            var reasonCounts = skipReasonCounts
                .Select(kvp => $"{kvp.Key}={kvp.Value}")
                .ToArray();
            Log($"[Collector] Skip reasons: {string.Join(", ", reasonCounts)}");
        }

        // If we have data rows but couldn't parse any samples, log detailed diagnostics and fail with clear error
        if (totalDataRows > 0 && parsedSamplesCount == 0)
        {
            LogParsingDiagnostics(totalDataRows, firstDataRows, skippedRowDiagnostics);
            throw new InvalidOperationException(
                $"Failed to parse any samples from PresentMon CSV file. " +
                $"File contains {totalDataRows} data rows but 0 samples were parsed. " +
                $"Header columns: [{string.Join(", ", _parser.HeaderColumns)}]. " +
                $"First data rows: [{string.Join("; ", firstDataRows.Take(3))}]");
        }

        return samples;
    }

    /// <summary>
    /// Collects samples by streaming stdout lines from PresentMon.
    /// Used for backward compatibility with tests and stdout mode.
    /// </summary>
    private IReadOnlyList<FrameSample> CollectFromStdout(TimeSpan duration, PresentMonRunOptions runOptions)
    {
        // Stdout mode doesn't capture ETW loss info (it's in stderr which we don't parse in streaming mode)
        // Set to empty/unknown
        LastCollectionDataQuality = DataQuality.Empty;

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

        using var cts = new CancellationTokenSource(duration + TimeSpan.FromSeconds(5)); // Add buffer for process cleanup
        var lines = _runner.RunAsync(runOptions, cts.Token)
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

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ZykeMark.App.Desktop.Services;
using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using ZykeMark.Infrastructure.Collectors;
using ZykeMark.Infrastructure.PresentMon;
using ZykeMark.Infrastructure.Reporting;
using ZykeMark.Infrastructure.Telemetry;

namespace ZykeMark.App.Desktop.Services;

public sealed class SessionOrchestrationService : IDisposable
{
    private const int RealCaptureChunkIntervalSeconds = 10;
    private const int PresentMonTimeoutBufferSeconds = 30;
    private const int RealCaptureStopTimeoutSeconds = 5;
    
    private readonly FileSystemLocalStore _localStore;
    private readonly SessionManager _sessionManager;
    private readonly ZykeMarkAggregator _aggregator;
    private readonly DesktopLogger _logger;
    private SimulatedCollector? _simulatedCollector;
    private FileSystemWatcher? _watcher;
    private string? _sessionId;
    private string? _sessionFolder;
    private string? _chunksFolder;
    private SessionMetadata? _sessionMetadata;
    private DataQuality? _lastCollectionDataQuality;

    // Configuration for collector mode
    private readonly bool _useSimulatedMode;
    private readonly string? _processName;
    private readonly int? _processId;
    private readonly string? _presentMonPath;

    // Real capture mode state
    private CancellationTokenSource? _realCaptureCts;
    private Task? _realCaptureTask;
    private WindowsTelemetrySampler? _telemetrySampler;

    public event EventHandler<RawSampleChunk>? ChunkReceived;
    public event EventHandler<Exception>? Error;

    /// <summary>
    /// Gets whether GPU telemetry is available on this system.
    /// Only valid after StartSession is called in real capture mode.
    /// </summary>
    public bool IsGpuTelemetryAvailable => _telemetrySampler?.IsGpuTelemetryAvailable ?? false;

    /// <summary>
    /// Gets the latest telemetry sample (for real-time UI updates).
    /// </summary>
    public TelemetrySample? LatestTelemetry => _telemetrySampler?.TryGetLatest();

    public SessionOrchestrationService(
        bool useSimulatedMode = true,
        string? processName = null,
        int? processId = null,
        string? presentMonPath = null)
    {
        _localStore = new FileSystemLocalStore();
        _aggregator = new ZykeMarkAggregator();
        _sessionManager = new SessionManager(_localStore, _aggregator);
        _logger = new DesktopLogger();
        _useSimulatedMode = useSimulatedMode;
        _processName = processName;
        _processId = processId;
        _presentMonPath = presentMonPath;

        _logger.Log("SessionOrchestrationService initialized");
        _logger.Log($"Mode: {(_useSimulatedMode ? "Simulated (Demo)" : "Real Capture (PresentMon + Telemetry)")}");
        if (!_useSimulatedMode)
        {
            _logger.Log($"Target process: {_processName ?? "(by PID)"}, PID: {_processId?.ToString() ?? "(by name)"}");
            _logger.Log($"PresentMon path: {_presentMonPath ?? "(auto-detect)"}");
        }
    }

    public string? CurrentSessionFolder => _sessionFolder;

    public SessionMetadata StartSession(string? gameName, string? buildVersion, RunConfig? runConfig = null, CaptureTarget? captureTarget = null)
    {
        try
        {
            _logger.Log($"Starting session - Game: {gameName ?? "(none)"}, Build: {buildVersion ?? "(none)"}");
            if (captureTarget is not null)
            {
                _logger.Log($"Capture target: {captureTarget.ToDisplayString()}");
            }

            var metadata = _sessionManager.StartSession(gameName, buildVersion, runConfig, captureTarget);
            _sessionMetadata = metadata;
            _sessionId = metadata.SessionId;
            _sessionFolder = Path.Combine(GetSessionsRoot(), metadata.SessionId);
            _chunksFolder = Path.Combine(_sessionFolder, "chunks");

            // Ensure session folder and chunks folder are created
            Directory.CreateDirectory(_sessionFolder);
            Directory.CreateDirectory(_chunksFolder);

            _logger.Log($"Session created: {_sessionId}");
            _logger.Log($"Session folder: {_sessionFolder}");

            if (_useSimulatedMode)
            {
                // SIMULATED MODE (Demo): Use the simulated collector which generates fake frame data
                _simulatedCollector = new SimulatedCollector(_localStore, metadata.SessionId);
                _simulatedCollector.Start();
                _logger.Log("Simulated (demo mode) collector started");
            }
            else
            {
                // REAL CAPTURE MODE: Start PresentMon collection and telemetry sampling in background
                StartRealCapture(metadata);
            }

            StartWatcher();
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to start session", ex);
            throw;
        }
    }

    /// <summary>
    /// Starts real capture mode with PresentMon frame collection and telemetry sampling.
    /// Frame capture runs in short intervals to produce chunks continuously.
    /// Telemetry sampling runs in parallel on a timer.
    /// </summary>
    private void StartRealCapture(SessionMetadata metadata)
    {
        _logger.Log("Starting real capture mode...");

        // Resolve the target process ID for telemetry
        var targetPid = ResolveTargetProcessId();
        
        // Start telemetry sampler if we have a valid PID
        if (targetPid.HasValue)
        {
            try
            {
                _telemetrySampler = new WindowsTelemetrySampler(msg => _logger.Log(msg));
                _telemetrySampler.Start(targetPid.Value);
                _logger.Log($"Telemetry sampler started for PID {targetPid.Value}");
                _logger.Log($"GPU telemetry available: {_telemetrySampler.IsGpuTelemetryAvailable}");
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to start telemetry sampler (frame capture will continue)", ex);
                _telemetrySampler?.Dispose();
                _telemetrySampler = null;
            }
        }
        else
        {
            _logger.Log("Warning: No valid target PID for telemetry sampling. Telemetry will be unavailable.");
        }

        // Start background frame capture task
        _realCaptureCts = new CancellationTokenSource();
        _realCaptureTask = Task.Run(() => RunRealCaptureLoopAsync(_realCaptureCts.Token));
        
        _logger.Log("Real capture background task started");
    }

    /// <summary>
    /// Resolves the target process ID from configured process name or ID.
    /// </summary>
    private int? ResolveTargetProcessId()
    {
        // If PID is directly specified, use it
        if (_processId.HasValue)
        {
            return _processId.Value;
        }

        // Try to find process by name
        if (!string.IsNullOrWhiteSpace(_processName))
        {
            try
            {
                // Remove .exe extension if present for Process.GetProcessesByName
                var searchName = _processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? _processName[..^4]
                    : _processName;

                var processes = Process.GetProcessesByName(searchName);
                if (processes.Length > 0)
                {
                    var pid = processes[0].Id;
                    _logger.Log($"Resolved process '{_processName}' to PID {pid}");
                    return pid;
                }

                _logger.Log($"Warning: Process '{_processName}' not found");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to resolve process '{_processName}'", ex);
            }
        }

        return null;
    }

    /// <summary>
    /// Background loop that runs PresentMon capture in short intervals, producing chunks continuously.
    /// This allows the session to run indefinitely until StopSession is called.
    /// </summary>
    private async Task RunRealCaptureLoopAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_sessionFolder) || _sessionMetadata is null)
        {
            _logger.Log("Error: No active session for real capture loop");
            return;
        }

        var chunkIndex = 1;
        var allSamples = new List<FrameSample>();
        var firstTimestampMs = (double?)null;
        var sessionStartUtc = _sessionMetadata.StartedAtUtc;

        _logger.Log($"Real capture loop starting - collecting in {RealCaptureChunkIntervalSeconds}s intervals");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var chunkStartUtc = DateTime.UtcNow;
                
                // Collect samples for one interval
                var samples = await CollectFrameSamplesAsync(
                    TimeSpan.FromSeconds(RealCaptureChunkIntervalSeconds),
                    cancellationToken);

                var chunkEndUtc = DateTime.UtcNow;

                if (samples.Count == 0)
                {
                    _logger.Log("Warning: No samples collected in this interval");
                    continue;
                }

                // Normalize timestamps and attach telemetry
                var chunkSamples = new List<FrameSample>();

                foreach (var sample in samples)
                {
                    firstTimestampMs ??= sample.TimestampMs;
                    var relativeTimestampMs = sample.TimestampMs - firstTimestampMs.Value;
                    
                    // Attach latest telemetry sample to frame samples
                    var telemetry = _telemetrySampler?.TryGetLatest();
                    var enrichedSample = sample with 
                    { 
                        TimestampMs = relativeTimestampMs,
                        Telemetry = telemetry
                    };
                    
                    chunkSamples.Add(enrichedSample);
                    allSamples.Add(enrichedSample);
                }

                // Create and save chunk with accurate start/end times
                var chunk = new RawSampleChunk(
                    chunkIndex,
                    chunkStartUtc,
                    chunkEndUtc,
                    chunkSamples.ToArray());

                _localStore.AppendChunk(_sessionId, chunk);
                
                // Log chunk write with telemetry attachment count for diagnostics
                var telemetryAttachedCount = chunkSamples.Count(s => s.Telemetry != null);
                _logger.Log($"ChunkWrite: samples={chunkSamples.Count}, telemetryAttached={telemetryAttachedCount}");
                chunkIndex++;
            }
            catch (OperationCanceledException)
            {
                _logger.Log("Real capture loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Error in real capture loop", ex);
                Error?.Invoke(this, ex);
                
                // Brief delay before retry
                await Task.Delay(1000, cancellationToken);
            }
        }

        _logger.Log($"Real capture loop ended. Total samples collected: {allSamples.Count}");
    }

    /// <summary>
    /// Collects frame samples using PresentMon for the specified duration.
    /// </summary>
    private async Task<IReadOnlyList<FrameSample>> CollectFrameSamplesAsync(
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_processName) && _processId is null)
        {
            _logger.Log("Error: No process configured for PresentMon collection");
            return Array.Empty<FrameSample>();
        }

        var runOptions = new PresentMonRunOptions(
            _presentMonPath,
            _processName,
            _processId,
            (int)Math.Ceiling(duration.TotalSeconds),
            SessionId: _sessionId,
            SessionFolder: _sessionFolder);

        var runner = new PresentMonRunner(msg => _logger.Log(msg));
        var parser = new PresentMonCsvParser();

        // Run PresentMon to file with timeout buffer for process cleanup
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(duration + TimeSpan.FromSeconds(PresentMonTimeoutBufferSeconds));

        try
        {
            var result = await runner.RunToFileAsync(runOptions, timeoutCts.Token);
            
            // Capture data quality
            _lastCollectionDataQuality = DataQuality.Create(
                result.EtwEventsLostCount,
                result.RawWarnings?.ToList());

            _logger.Log($"PresentMon exit code: {result.ExitCode}");

            if (string.IsNullOrWhiteSpace(result.CsvPath) || !File.Exists(result.CsvPath))
            {
                _logger.Log("Error: No CSV output from PresentMon");
                return Array.Empty<FrameSample>();
            }

            // Parse CSV
            var lines = await File.ReadAllLinesAsync(result.CsvPath, cancellationToken);
            _logger.Log($"CSV file contains {lines.Length} lines");

            var headerLine = PresentMonCsvParser.DiscoverHeaderLine(lines.Take(50), out var headerLineIndex);
            if (headerLine is null)
            {
                _logger.Log("Error: No valid CSV header found");
                return Array.Empty<FrameSample>();
            }

            parser.ParseHeader(headerLine);

            var samples = new List<FrameSample>();
            for (var i = headerLineIndex + 1; i < lines.Length; i++)
            {
                if (parser.TryParse(lines[i], out var sample, out _))
                {
                    samples.Add(sample);
                }
            }

            _logger.Log($"Parsed {samples.Count} frame samples from CSV");
            return samples;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("PresentMon collection failed", ex);
            return Array.Empty<FrameSample>();
        }
    }

    /// <summary>
    /// Starts PresentMon collection for the specified duration.
    /// Only applicable when not in simulated mode.
    /// </summary>
    public async Task<IReadOnlyList<FrameSample>> CollectAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (_useSimulatedMode)
        {
            throw new InvalidOperationException("CollectAsync is not applicable in simulated mode.");
        }

        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_sessionFolder) || _sessionMetadata is null)
        {
            throw new InvalidOperationException("No active session. Call StartSession first.");
        }

        if (string.IsNullOrWhiteSpace(_processName) && _processId is null)
        {
            throw new InvalidOperationException("ProcessName or ProcessId must be configured for PresentMon collection.");
        }

        try
        {
            _logger.Log($"Starting PresentMon collection for {duration.TotalSeconds} seconds");
            _logger.Log($"Target process: {_processName ?? $"PID {_processId}"}");

            var runOptions = new PresentMonRunOptions(
                _presentMonPath,
                _processName,
                _processId,
                (int)Math.Ceiling(duration.TotalSeconds));

            var runner = new PresentMonRunner(msg => _logger.Log(msg));
            var parser = new PresentMonCsvParser();
            var collector = new PresentMonCollector(
                _localStore,
                _sessionId,
                _sessionMetadata.StartedAtUtc,
                runner,
                parser,
                runOptions,
                _sessionFolder);

            var samples = await Task.Run(() => collector.Collect(duration), cancellationToken);
            _lastCollectionDataQuality = collector.LastCollectionDataQuality;
            _logger.Log($"PresentMon collection complete: {samples.Count} samples");

            return samples;
        }
        catch (Exception ex)
        {
            _logger.LogError("PresentMon collection failed", ex);
            throw;
        }
    }

    public string StopSession()
    {
        try
        {
            _logger.Log("Stopping session");

            // Stop simulated collector if running
            _simulatedCollector?.Stop();
            _simulatedCollector = null;

            // Stop real capture if running
            StopRealCapture();

            StopWatcher();
            var summaryPath = _sessionManager.StopSession(_sessionId, _lastCollectionDataQuality);
            _lastCollectionDataQuality = null; // Clear after use

            _logger.Log($"Session stopped: {_sessionId}");
            _logger.Log($"Summary: {summaryPath}");

            return summaryPath;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to stop session", ex);
            throw;
        }
    }

    public string ExportPdf(string outputPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_sessionFolder))
            {
                throw new InvalidOperationException("No active session folder is available.");
            }

            _logger.Log($"Exporting PDF to: {outputPath}");

            var tokensPath = ReportGenerator.FindBrandTokensPath(AppContext.BaseDirectory);
            var theme = BrandTheme.LoadFromTokens(tokensPath);
            var generator = new ReportGenerator(theme);
            var result = generator.Generate(_sessionFolder, outputPath);

            _logger.Log($"PDF exported: {result}");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to export PDF", ex);
            throw;
        }
    }

    public void OpenSessionFolder()
    {
        if (string.IsNullOrWhiteSpace(_sessionFolder))
        {
            return;
        }

        try
        {
            _logger.Log($"Opening session folder: {_sessionFolder}");
            Process.Start(new ProcessStartInfo
            {
                FileName = _sessionFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to open session folder", ex);
            throw;
        }
    }

    public void Dispose()
    {
        StopWatcher();
        _simulatedCollector?.Stop();
        StopRealCapture();
        _logger.Log("SessionOrchestrationService disposed");
        _logger.Dispose();
    }

    /// <summary>
    /// Stops real capture mode, including telemetry sampling and the background collection task.
    /// </summary>
    private void StopRealCapture()
    {
        // Stop telemetry sampler
        if (_telemetrySampler != null)
        {
            _telemetrySampler.Stop();
            var sampleCount = _telemetrySampler.GetSamples().Count;
            _logger.Log($"Telemetry sampler stopped. Collected {sampleCount} samples.");
            _telemetrySampler.Dispose();
            _telemetrySampler = null;
        }

        // Cancel and wait for real capture task
        if (_realCaptureCts != null)
        {
            _realCaptureCts.Cancel();
            try
            {
                _realCaptureTask?.Wait(TimeSpan.FromSeconds(RealCaptureStopTimeoutSeconds));
            }
            catch (AggregateException)
            {
                // Expected when task is cancelled
            }
            _realCaptureCts.Dispose();
            _realCaptureCts = null;
            _realCaptureTask = null;
            _logger.Log("Real capture task stopped");
        }
    }

    private void StartWatcher()
    {
        if (string.IsNullOrWhiteSpace(_chunksFolder))
        {
            return;
        }

        Directory.CreateDirectory(_chunksFolder);
        _watcher = new FileSystemWatcher(_chunksFolder, "chunk_*.json")
        {
            EnableRaisingEvents = true
        };
        _watcher.Created += OnChunkCreated;
        _logger.Log($"Chunk watcher started: {_chunksFolder}");
    }

    private void StopWatcher()
    {
        if (_watcher == null)
        {
            return;
        }

        _watcher.Created -= OnChunkCreated;
        _watcher.Dispose();
        _watcher = null;
        _logger.Log("Chunk watcher stopped");
    }

    private async void OnChunkCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            var chunk = await ReadChunkWithRetryAsync(e.FullPath).ConfigureAwait(false);
            if (chunk != null)
            {
                _logger.Log($"Chunk received: {e.Name}");
                ChunkReceived?.Invoke(this, chunk);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error reading chunk: {e.Name}", ex);
            Error?.Invoke(this, ex);
        }
    }

    private static async Task<RawSampleChunk?> ReadChunkWithRetryAsync(string path)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                await Task.Delay(80).ConfigureAwait(false);
                using var stream = File.OpenRead(path);
                return await JsonSerializer.DeserializeAsync<RawSampleChunk>(stream).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(80).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static string GetSessionsRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZykeMark",
            "sessions");
    }
}

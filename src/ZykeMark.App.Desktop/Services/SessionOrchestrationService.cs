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

namespace ZykeMark.App.Desktop.Services;

public sealed class SessionOrchestrationService : IDisposable
{
    private readonly FileSystemLocalStore _localStore;
    private readonly SessionManager _sessionManager;
    private readonly ZykeMarkAggregator _aggregator;
    private readonly DesktopLogger _logger;
    private SimulatedCollector? _simulatedCollector;
    private FileSystemWatcher? _watcher;
    private string? _sessionId;
    private string? _sessionFolder;
    private string? _chunksFolder;

    // Configuration for collector mode
    private readonly bool _useSimulatedMode;
    private readonly string? _processName;
    private readonly int? _processId;
    private readonly string? _presentMonPath;

    public event EventHandler<RawSampleChunk>? ChunkReceived;
    public event EventHandler<Exception>? Error;

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
        _logger.Log($"Mode: {(_useSimulatedMode ? "Simulated" : "PresentMon")}");
    }

    public string? CurrentSessionFolder => _sessionFolder;

    public SessionMetadata StartSession(string? gameName, string? buildVersion, RunConfig? runConfig = null)
    {
        try
        {
            _logger.Log($"Starting session - Game: {gameName ?? "(none)"}, Build: {buildVersion ?? "(none)"}");

            var metadata = _sessionManager.StartSession(gameName, buildVersion, runConfig);
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
                _simulatedCollector = new SimulatedCollector(_localStore, metadata.SessionId);
                _simulatedCollector.Start();
                _logger.Log("Simulated collector started");
            }
            else
            {
                // For real PresentMon, we need to handle async collection differently
                // Since PresentMon runs for a timed duration, we don't start it in StartSession
                // The real collection would be triggered separately or run inline
                _logger.Log("PresentMon mode configured - use CollectAsync to start capture");
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
    /// Starts PresentMon collection for the specified duration.
    /// Only applicable when not in simulated mode.
    /// </summary>
    public async Task<IReadOnlyList<FrameSample>> CollectAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (_useSimulatedMode)
        {
            throw new InvalidOperationException("CollectAsync is not applicable in simulated mode.");
        }

        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_sessionFolder))
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
                DateTime.UtcNow,
                runner,
                parser,
                runOptions,
                _sessionFolder);

            var samples = await Task.Run(() => collector.Collect(duration), cancellationToken);
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

            _simulatedCollector?.Stop();
            _simulatedCollector = null;
            StopWatcher();
            var summaryPath = _sessionManager.StopSession(_sessionId);

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
        _logger.Log("SessionOrchestrationService disposed");
        _logger.Dispose();
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

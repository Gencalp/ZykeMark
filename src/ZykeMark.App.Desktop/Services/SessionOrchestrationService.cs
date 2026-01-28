using System.Diagnostics;
using System.Text.Json;
using ZykeMark.App.Desktop.Services;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;
using ZykeMark.Infrastructure.Reporting;

namespace ZykeMark.App.Desktop.Services;

public sealed class SessionOrchestrationService : IDisposable
{
    private readonly FileSystemLocalStore _localStore;
    private readonly SessionManager _sessionManager;
    private readonly ZykeMarkAggregator _aggregator;
    private SimulatedCollector? _collector;
    private FileSystemWatcher? _watcher;
    private string? _sessionId;
    private string? _sessionFolder;
    private string? _chunksFolder;

    public event EventHandler<RawSampleChunk>? ChunkReceived;
    public event EventHandler<Exception>? Error;

    public SessionOrchestrationService()
    {
        _localStore = new FileSystemLocalStore();
        _aggregator = new ZykeMarkAggregator();
        _sessionManager = new SessionManager(_localStore, _aggregator);
    }

    public string? CurrentSessionFolder => _sessionFolder;

    public SessionMetadata StartSession(string? gameName, string? buildVersion, RunConfig? runConfig = null)
    {
        var metadata = _sessionManager.StartSession(gameName, buildVersion, runConfig);
        _sessionId = metadata.SessionId;
        _sessionFolder = Path.Combine(GetSessionsRoot(), metadata.SessionId);
        _chunksFolder = Path.Combine(_sessionFolder, "chunks");

        _collector = new SimulatedCollector(_localStore, metadata.SessionId);
        _collector.Start();

        StartWatcher();
        return metadata;
    }

    public string StopSession()
    {
        _collector?.Stop();
        _collector = null;
        StopWatcher();
        return _sessionManager.StopSession(_sessionId);
    }

    public string ExportPdf(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(_sessionFolder))
        {
            throw new InvalidOperationException("No active session folder is available.");
        }

        var tokensPath = ReportGenerator.FindBrandTokensPath(AppContext.BaseDirectory);
        var theme = BrandTheme.LoadFromTokens(tokensPath);
        var generator = new ReportGenerator(theme);
        return generator.Generate(_sessionFolder, outputPath);
    }

    public void OpenSessionFolder()
    {
        if (string.IsNullOrWhiteSpace(_sessionFolder))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _sessionFolder,
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        StopWatcher();
        _collector?.Stop();
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
    }

    private async void OnChunkCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            var chunk = await ReadChunkWithRetryAsync(e.FullPath).ConfigureAwait(false);
            if (chunk != null)
            {
                ChunkReceived?.Invoke(this, chunk);
            }
        }
        catch (Exception ex)
        {
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

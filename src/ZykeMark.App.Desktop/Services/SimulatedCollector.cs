using System.Timers;   
using Timer = System.Timers.Timer;
using ZykeMark.Core.Models;
using ZykeMark.Core.Services;

namespace ZykeMark.App.Desktop.Services;

public sealed class SimulatedCollector
{
    private readonly FileSystemLocalStore _localStore;
    private readonly string _sessionId;
    private readonly Timer _timer;
    private readonly List<FrameSample> _buffer = new();
    private DateTime _chunkStartUtc;
    private int _chunkIndex;
    private double _timestampMs;
    private int _frameIndex;

    public SimulatedCollector(FileSystemLocalStore localStore, string sessionId)
    {
        _localStore = localStore ?? throw new ArgumentNullException(nameof(localStore));
        _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        _timer = new Timer(16);
        _timer.Elapsed += OnTick;
        _chunkIndex = 1;
        _chunkStartUtc = DateTime.UtcNow;
    }

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        FlushChunk();
    }

    private void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var frameTimeMs = 16.67 + (_frameIndex % 120 == 0 ? 20.0 : 0.0);
        var cpu = _frameIndex % 4 == 0 ? frameTimeMs * 0.8 : (double?)null;
        var gpu = _frameIndex % 5 == 0 ? frameTimeMs * 0.9 : (double?)null;
        _timestampMs += frameTimeMs;
        _frameIndex++;

        _buffer.Add(new FrameSample(_timestampMs, frameTimeMs, cpu, gpu));

        var elapsed = (DateTime.UtcNow - _chunkStartUtc).TotalSeconds;
        if (elapsed >= 5 || _buffer.Count >= 10_000)
        {
            FlushChunk();
        }
    }

    private void FlushChunk()
    {
        if (_buffer.Count == 0)
        {
            _chunkStartUtc = DateTime.UtcNow;
            return;
        }

        var chunk = new RawSampleChunk(
            ChunkIndex: _chunkIndex,
            StartUtc: _chunkStartUtc,
            EndUtc: DateTime.UtcNow,
            Samples: _buffer.ToArray());

        _localStore.AppendChunk(_sessionId, chunk);
        _buffer.Clear();
        _chunkIndex++;
        _chunkStartUtc = DateTime.UtcNow;
    }
}

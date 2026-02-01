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
    private readonly Random _random = new();
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

        // Generate simulated telemetry data
        var telemetry = GenerateSimulatedTelemetry();

        _buffer.Add(new FrameSample(_timestampMs, frameTimeMs, cpu, gpu, telemetry));

        var elapsed = (DateTime.UtcNow - _chunkStartUtc).TotalSeconds;
        if (elapsed >= 5 || _buffer.Count >= 10_000)
        {
            FlushChunk();
        }
    }

    private TelemetrySample GenerateSimulatedTelemetry()
    {
        // Base values with some variation to simulate real-world scenarios
        var gpuUtil = 50.0 + _random.NextDouble() * 40.0; // 50-90%
        var vramDedicated = 2000.0 + _random.NextDouble() * 2000.0; // 2000-4000 MB
        var vramShared = 100.0 + _random.NextDouble() * 200.0; // 100-300 MB
        var cpuProcess = 10.0 + _random.NextDouble() * 30.0; // 10-40%
        var topThreadCpu = 5.0 + _random.NextDouble() * 20.0; // 5-25%
        var ramWorkingSet = 1000.0 + _random.NextDouble() * 1000.0; // 1000-2000 MB
        var ramPrivateBytes = 800.0 + _random.NextDouble() * 800.0; // 800-1600 MB
        var diskReadMBps = _random.NextDouble() * 50.0; // 0-50 MB/s

        // Add occasional spikes
        if (_frameIndex % 60 == 0)
        {
            gpuUtil = Math.Min(100.0, gpuUtil + 20.0);
            cpuProcess = Math.Min(100.0, cpuProcess + 15.0);
        }

        return new TelemetrySample(
            GpuUtilizationPercent: gpuUtil,
            VramDedicatedMB: vramDedicated,
            VramSharedMB: vramShared,
            CpuProcessPercent: cpuProcess,
            TopThreadCpuPercent: topThreadCpu,
            RamWorkingSetMB: ramWorkingSet,
            RamPrivateBytesMB: ramPrivateBytes,
            DiskReadMBps: diskReadMBps);
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

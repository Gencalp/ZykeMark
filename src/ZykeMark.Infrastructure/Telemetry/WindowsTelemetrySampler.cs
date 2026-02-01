using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;

namespace ZykeMark.Infrastructure.Telemetry;

/// <summary>
/// Windows implementation of ITelemetrySampler that collects real system telemetry data.
/// 
/// TELEMETRY SOURCES:
/// - CPU process %: Process.TotalProcessorTime delta / elapsed / ProcessorCount * 100
/// - Top thread CPU %: Enumerate Process.Threads, max thread CPU using TotalProcessorTime delta
/// - RAM: Process.WorkingSet64 and Process.PrivateMemorySize64 (bytes → MB)
/// - Disk read MB/s: GetProcessIoCounters P/Invoke → IO_COUNTERS.ReadTransferCount delta → MB/s
/// - GPU Utilization %: Windows Performance Counters "GPU Engine(*)\Utilization Percentage"
/// - VRAM: Windows Performance Counters "GPU Process Memory(*)\Dedicated/Shared Usage"
/// 
/// FALLBACK: If GPU counters are unavailable (missing category, permissions), GPU/VRAM metrics
/// return null. CPU/RAM/Disk metrics should always work if the process is accessible.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTelemetrySampler : ITelemetrySampler
{
    private const int SampleIntervalMs = 500; // Sample every 500ms
    private const double BytesToMB = 1.0 / (1024.0 * 1024.0);
    
    private readonly object _lock = new();
    private readonly List<TelemetrySample> _samples = new();
    private readonly Action<string>? _logger;
    
    private System.Threading.Timer? _timer;
    private int _processId;
    private Process? _process;
    private bool _isRunning;
    private bool _isGpuTelemetryAvailable;
    private TelemetrySample? _latestSample;
    
    // CPU tracking
    private DateTime _lastCpuSampleTime;
    private TimeSpan _lastProcessCpuTime;
    private readonly Dictionary<int, TimeSpan> _threadCpuTimes = new();
    
    // Disk tracking
    private DateTime _lastDiskSampleTime;
    private ulong _lastDiskReadBytes;
    
    // GPU Performance Counters
    private PerformanceCounterCategory? _gpuEngineCategory;
    private PerformanceCounterCategory? _gpuProcessMemoryCategory;

    public WindowsTelemetrySampler(Action<string>? logger = null)
    {
        _logger = logger;
    }

    public bool IsRunning => _isRunning;
    public bool IsGpuTelemetryAvailable => _isGpuTelemetryAvailable;

    public void Start(int processId)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Sampler is already running.");
        }

        _processId = processId;
        
        try
        {
            _process = Process.GetProcessById(processId);
            Log($"[Telemetry] Started monitoring process: {_process.ProcessName} (PID {processId})");
        }
        catch (ArgumentException ex)
        {
            Log($"[Telemetry] Warning: Process {processId} not found: {ex.Message}");
            _process = null;
        }

        // Initialize CPU tracking
        if (_process != null)
        {
            try
            {
                _lastCpuSampleTime = DateTime.UtcNow;
                _lastProcessCpuTime = _process.TotalProcessorTime;
                InitializeThreadCpuTracking();
            }
            catch (Exception ex)
            {
                Log($"[Telemetry] Warning: Could not initialize CPU tracking: {ex.Message}");
            }
        }

        // Initialize disk tracking
        InitializeDiskTracking();

        // Initialize GPU counters (may fail if not available)
        InitializeGpuCounters();

        lock (_lock)
        {
            _samples.Clear();
            _latestSample = null;
        }

        _isRunning = true;
        _timer = new System.Threading.Timer(OnTimerTick, null, 0, SampleIntervalMs);
        
        Log($"[Telemetry] Sampler started. GPU telemetry available: {_isGpuTelemetryAvailable}");
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _timer?.Dispose();
        _timer = null;
        
        lock (_lock)
        {
            Log($"[Telemetry] Sampler stopped. Collected {_samples.Count} samples.");
        }
    }

    public TelemetrySample? TryGetLatest()
    {
        lock (_lock)
        {
            return _latestSample;
        }
    }

    public IReadOnlyList<TelemetrySample> GetSamples()
    {
        lock (_lock)
        {
            return _samples.ToArray();
        }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
        _process = null;
    }

    private void OnTimerTick(object? state)
    {
        if (!_isRunning)
        {
            return;
        }

        try
        {
            var sample = CollectSample();
            if (sample != null)
            {
                lock (_lock)
                {
                    _samples.Add(sample);
                    _latestSample = sample;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Error collecting sample: {ex.Message}");
        }
    }

    private TelemetrySample? CollectSample()
    {
        if (_process == null)
        {
            return TelemetrySample.Empty;
        }

        try
        {
            // Refresh process info to get latest values
            _process.Refresh();
            
            if (_process.HasExited)
            {
                Log("[Telemetry] Process has exited, returning empty sample");
                return TelemetrySample.Empty;
            }
        }
        catch (InvalidOperationException)
        {
            // Process has exited
            return TelemetrySample.Empty;
        }

        // Capture timestamp once for consistent elapsed time calculations
        var sampleTime = DateTime.UtcNow;

        // Collect CPU metrics (pass the sample time to ensure consistency)
        var cpuProcessPercent = CollectProcessCpuPercent(sampleTime);
        var topThreadCpuPercent = CollectTopThreadCpuPercent(sampleTime);

        // Collect RAM metrics
        var ramWorkingSetMB = CollectRamWorkingSetMB();
        var ramPrivateBytesMB = CollectRamPrivateBytesMB();

        // Collect Disk metrics
        var diskReadMBps = CollectDiskReadMBps();

        // Collect GPU metrics (may be null if counters unavailable)
        var gpuUtilPercent = CollectGpuUtilizationPercent();
        var vramDedicatedMB = CollectVramDedicatedMB();
        var vramSharedMB = CollectVramSharedMB();

        return new TelemetrySample(
            GpuUtilizationPercent: gpuUtilPercent,
            VramDedicatedMB: vramDedicatedMB,
            VramSharedMB: vramSharedMB,
            CpuProcessPercent: cpuProcessPercent,
            TopThreadCpuPercent: topThreadCpuPercent,
            RamWorkingSetMB: ramWorkingSetMB,
            RamPrivateBytesMB: ramPrivateBytesMB,
            DiskReadMBps: diskReadMBps);
    }

    #region CPU Metrics

    private double? CollectProcessCpuPercent(DateTime sampleTime)
    {
        if (_process == null)
        {
            return null;
        }

        try
        {
            var currentCpuTime = _process.TotalProcessorTime;
            
            var elapsedSeconds = (sampleTime - _lastCpuSampleTime).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return null;
            }

            var cpuUsedSeconds = (currentCpuTime - _lastProcessCpuTime).TotalSeconds;
            var cpuPercent = (cpuUsedSeconds / elapsedSeconds / Environment.ProcessorCount) * 100.0;

            _lastCpuSampleTime = sampleTime;
            _lastProcessCpuTime = currentCpuTime;

            return Math.Clamp(cpuPercent, 0, 100);
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect CPU percent: {ex.Message}");
            return null;
        }
    }

    private void InitializeThreadCpuTracking()
    {
        if (_process == null)
        {
            return;
        }

        try
        {
            _threadCpuTimes.Clear();
            foreach (ProcessThread thread in _process.Threads)
            {
                try
                {
                    _threadCpuTimes[thread.Id] = thread.TotalProcessorTime;
                }
                catch
                {
                    // Thread may have exited or be inaccessible
                }
            }
        }
        catch
        {
            // Threads collection may not be accessible
        }
    }

    private double? CollectTopThreadCpuPercent(DateTime sampleTime)
    {
        if (_process == null)
        {
            return null;
        }

        try
        {
            var elapsedSeconds = (sampleTime - _lastCpuSampleTime).TotalSeconds;
            // Note: _lastCpuSampleTime was already updated by CollectProcessCpuPercent,
            // so for thread calculations we use the previous sample time stored before the update.
            // Since we call CollectProcessCpuPercent first, we need to use the elapsed time from that call.
            // Actually, both methods now receive the same sampleTime, so elapsed is based on the same start.
            // The _lastCpuSampleTime is updated in CollectProcessCpuPercent, so this method should be called
            // after that and will see the updated time. We'll use the elapsed from process CPU method.
            if (elapsedSeconds <= 0)
            {
                // Use a fallback elapsed time based on sample interval
                elapsedSeconds = SampleIntervalMs / 1000.0;
            }

            double maxThreadCpuPercent = 0;
            var newThreadTimes = new Dictionary<int, TimeSpan>();

            foreach (ProcessThread thread in _process.Threads)
            {
                try
                {
                    var currentThreadCpuTime = thread.TotalProcessorTime;
                    newThreadTimes[thread.Id] = currentThreadCpuTime;

                    if (_threadCpuTimes.TryGetValue(thread.Id, out var previousTime))
                    {
                        var threadCpuUsedSeconds = (currentThreadCpuTime - previousTime).TotalSeconds;
                        var threadCpuPercent = (threadCpuUsedSeconds / (SampleIntervalMs / 1000.0)) * 100.0;
                        maxThreadCpuPercent = Math.Max(maxThreadCpuPercent, threadCpuPercent);
                    }
                }
                catch
                {
                    // Thread may have exited or be inaccessible - continue to next thread
                }
            }

            // Update thread tracking for next sample
            _threadCpuTimes.Clear();
            foreach (var kvp in newThreadTimes)
            {
                _threadCpuTimes[kvp.Key] = kvp.Value;
            }

            return Math.Clamp(maxThreadCpuPercent, 0, 100);
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect top thread CPU: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region RAM Metrics

    private double? CollectRamWorkingSetMB()
    {
        if (_process == null)
        {
            return null;
        }

        try
        {
            return _process.WorkingSet64 * BytesToMB;
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect working set: {ex.Message}");
            return null;
        }
    }

    private double? CollectRamPrivateBytesMB()
    {
        if (_process == null)
        {
            return null;
        }

        try
        {
            return _process.PrivateMemorySize64 * BytesToMB;
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect private bytes: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Disk Metrics

    private void InitializeDiskTracking()
    {
        if (_process == null)
        {
            return;
        }

        try
        {
            var ioCounters = GetProcessIoCounters(_process.Handle);
            if (ioCounters != null)
            {
                _lastDiskSampleTime = DateTime.UtcNow;
                _lastDiskReadBytes = ioCounters.Value.ReadTransferCount;
            }
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not initialize disk tracking: {ex.Message}");
        }
    }

    private double? CollectDiskReadMBps()
    {
        if (_process == null)
        {
            return null;
        }

        try
        {
            var ioCounters = GetProcessIoCounters(_process.Handle);
            if (ioCounters == null)
            {
                return null;
            }

            var currentTime = DateTime.UtcNow;
            var currentReadBytes = ioCounters.Value.ReadTransferCount;
            
            var elapsedSeconds = (currentTime - _lastDiskSampleTime).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return null;
            }

            var bytesRead = currentReadBytes - _lastDiskReadBytes;
            var readMBps = (bytesRead * BytesToMB) / elapsedSeconds;

            _lastDiskSampleTime = currentTime;
            _lastDiskReadBytes = currentReadBytes;

            return Math.Max(0, readMBps);
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect disk read: {ex.Message}");
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    private static IO_COUNTERS? GetProcessIoCounters(IntPtr processHandle)
    {
        if (GetProcessIoCounters(processHandle, out var counters))
        {
            return counters;
        }
        return null;
    }

    #endregion

    #region GPU Metrics

    private void InitializeGpuCounters()
    {
        try
        {
            // Check if GPU Engine category exists
            if (PerformanceCounterCategory.Exists("GPU Engine"))
            {
                _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
                Log("[Telemetry] GPU Engine performance counters available");
            }
            else
            {
                Log("[Telemetry] GPU Engine performance counters not available");
            }

            // Check if GPU Process Memory category exists
            if (PerformanceCounterCategory.Exists("GPU Process Memory"))
            {
                _gpuProcessMemoryCategory = new PerformanceCounterCategory("GPU Process Memory");
                Log("[Telemetry] GPU Process Memory performance counters available");
            }
            else
            {
                Log("[Telemetry] GPU Process Memory performance counters not available");
            }

            _isGpuTelemetryAvailable = _gpuEngineCategory != null && _gpuProcessMemoryCategory != null;
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not initialize GPU counters: {ex.Message}");
            _isGpuTelemetryAvailable = false;
        }
    }

    private double? CollectGpuUtilizationPercent()
    {
        if (_gpuEngineCategory == null)
        {
            return null;
        }

        try
        {
            var instanceNames = _gpuEngineCategory.GetInstanceNames();
            var pidPattern = $"pid_{_processId}_";
            
            double totalUtilization = 0;
            var matchingInstances = 0;

            foreach (var instanceName in instanceNames)
            {
                if (!instanceName.Contains(pidPattern, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instanceName, readOnly: true);
                    var value = counter.NextValue();
                    totalUtilization += value;
                    matchingInstances++;
                }
                catch
                {
                    // Counter may not exist for this instance
                }
            }

            if (matchingInstances == 0)
            {
                return null;
            }

            return Math.Clamp(totalUtilization, 0, 100);
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect GPU utilization: {ex.Message}");
            return null;
        }
    }

    private double? CollectVramDedicatedMB()
    {
        if (_gpuProcessMemoryCategory == null)
        {
            return null;
        }

        try
        {
            var instanceNames = _gpuProcessMemoryCategory.GetInstanceNames();
            var pidPattern = $"pid_{_processId}_";
            
            double totalDedicated = 0;
            var matchingInstances = 0;

            foreach (var instanceName in instanceNames)
            {
                if (!instanceName.Contains(pidPattern, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var counter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instanceName, readOnly: true);
                    var value = counter.NextValue();
                    totalDedicated += value;
                    matchingInstances++;
                }
                catch
                {
                    // Counter may not exist for this instance
                }
            }

            if (matchingInstances == 0)
            {
                return null;
            }

            return totalDedicated * BytesToMB;
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect VRAM dedicated: {ex.Message}");
            return null;
        }
    }

    private double? CollectVramSharedMB()
    {
        if (_gpuProcessMemoryCategory == null)
        {
            return null;
        }

        try
        {
            var instanceNames = _gpuProcessMemoryCategory.GetInstanceNames();
            var pidPattern = $"pid_{_processId}_";
            
            double totalShared = 0;
            var matchingInstances = 0;

            foreach (var instanceName in instanceNames)
            {
                if (!instanceName.Contains(pidPattern, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    using var counter = new PerformanceCounter("GPU Process Memory", "Shared Usage", instanceName, readOnly: true);
                    var value = counter.NextValue();
                    totalShared += value;
                    matchingInstances++;
                }
                catch
                {
                    // Counter may not exist for this instance
                }
            }

            if (matchingInstances == 0)
            {
                return null;
            }

            return totalShared * BytesToMB;
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not collect VRAM shared: {ex.Message}");
            return null;
        }
    }

    #endregion

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}

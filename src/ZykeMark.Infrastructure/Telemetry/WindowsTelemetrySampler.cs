using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
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
    private const int PidSetRefreshIntervalMs = 5000; // Refresh PID set every 5 seconds for multi-process apps
    
    // Regex pattern to extract PID from performance counter instance names
    // Matches patterns like "pid_1234_", "pid_9068_luid_...", etc.
    private static readonly System.Text.RegularExpressions.Regex PidRegex = new(
        @"pid_(\d+)_",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    
    private readonly object _lock = new();
    private readonly List<TelemetrySample> _samples = new();
    private readonly Action<string>? _logger;
    
    private System.Threading.Timer? _timer;
    private int _processId;
    private Process? _process;
    private bool _isRunning;
    private bool _isGpuTelemetryAvailable;
    private TelemetrySample? _latestSample;
    private DateTime _startTime;
    
    // Multi-process app support
    private string? _processName;
    private bool _isMultiProcessApp;
    private HashSet<int> _targetPidSet = new();
    private DateTime _lastPidSetRefresh;
    
    // CPU tracking
    private DateTime _lastCpuSampleTime;
    private TimeSpan _lastProcessCpuTime;
    private readonly Dictionary<int, TimeSpan> _threadCpuTimes = new();
    
    // Disk tracking
    private DateTime _lastDiskSampleTime;
    private ulong _lastDiskReadBytes;
    
    // GPU Performance Counters - cached for efficiency and proper priming
    private PerformanceCounterCategory? _gpuEngineCategory;
    private PerformanceCounterCategory? _gpuProcessMemoryCategory;
    
    // Cached GPU counters - created once on Start() and primed
    private readonly List<PerformanceCounter> _gpuUtilizationCounters = new();
    private readonly List<PerformanceCounter> _vramDedicatedCounters = new();
    private readonly List<PerformanceCounter> _vramSharedCounters = new();
    
    // Diagnostics for telemetry debugging
    private TelemetryDiagnostics _diagnostics = new();
    private string? _sessionFolder;

    public WindowsTelemetrySampler(Action<string>? logger = null, string? sessionFolder = null)
    {
        _logger = logger;
        _sessionFolder = sessionFolder;
    }

    public bool IsRunning => _isRunning;
    public bool IsGpuTelemetryAvailable => _isGpuTelemetryAvailable;
    public double StartTimestampMs { get; private set; }

    public void Start(int processId)
    {
        Start(processId, processName: null, isMultiProcessApp: false);
    }

    /// <summary>
    /// Starts telemetry sampling with optional multi-process app support.
    /// </summary>
    /// <param name="processId">The primary process ID to monitor.</param>
    /// <param name="processName">The process name (for multi-process apps, used to find all related PIDs).</param>
    /// <param name="isMultiProcessApp">Whether this is a multi-process application (browsers, Electron apps, etc.).</param>
    public void Start(int processId, string? processName, bool isMultiProcessApp)
    {
        if (_isRunning)
        {
            throw new InvalidOperationException("Sampler is already running.");
        }

        _processId = processId;
        _processName = processName;
        _isMultiProcessApp = isMultiProcessApp;
        
        // Build initial PID set
        BuildTargetPidSet();
        
        try
        {
            _process = Process.GetProcessById(processId);
            Log($"[Telemetry] Started monitoring process: {_process.ProcessName} (PID {processId})");
            
            // If no process name was provided, use the one from the process
            if (string.IsNullOrWhiteSpace(_processName))
            {
                _processName = _process.ProcessName;
            }
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

        // Initialize timing
        _startTime = DateTime.UtcNow;
        StartTimestampMs = 0; // Relative timestamps start at 0

        lock (_lock)
        {
            _samples.Clear();
            _latestSample = null;
        }

        _isRunning = true;
        _timer = new System.Threading.Timer(OnTimerTick, null, 0, SampleIntervalMs);
        
        Log($"[Telemetry] Sampler started. GPU telemetry available: {_isGpuTelemetryAvailable}, Multi-process: {_isMultiProcessApp}, PIDs: {string.Join(",", _targetPidSet)}");
    }

    /// <summary>
    /// Builds the target PID set for GPU counter matching.
    /// For multi-process apps, finds all processes with the same name.
    /// For single-process apps, just uses the primary PID.
    /// </summary>
    private void BuildTargetPidSet()
    {
        _targetPidSet.Clear();
        _targetPidSet.Add(_processId);
        _lastPidSetRefresh = DateTime.UtcNow;

        if (_isMultiProcessApp && !string.IsNullOrWhiteSpace(_processName))
        {
            try
            {
                var processName = _processName!;
                // Remove .exe extension if present
                if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    processName = processName[..^4];
                }

                var relatedProcesses = Process.GetProcessesByName(processName);
                try
                {
                    foreach (var process in relatedProcesses)
                    {
                        try
                        {
                            _targetPidSet.Add(process.Id);
                        }
                        catch
                        {
                            // Process may have exited
                        }
                    }
                    
                    Log($"[Telemetry] Multi-process app '{processName}': found {_targetPidSet.Count} related PIDs");
                }
                finally
                {
                    foreach (var process in relatedProcesses)
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Telemetry] Warning: Could not enumerate related processes: {ex.Message}");
            }
        }

        // Update diagnostics
        _diagnostics.IsMultiProcessApp = _isMultiProcessApp;
        _diagnostics.ProcessName = _processName;
        _diagnostics.TargetPidSet = _targetPidSet.ToList();
    }

    /// <summary>
    /// Refreshes the PID set for multi-process apps (called periodically during sampling).
    /// </summary>
    private void RefreshPidSetIfNeeded()
    {
        if (!_isMultiProcessApp)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastPidSetRefresh).TotalMilliseconds < PidSetRefreshIntervalMs)
        {
            return;
        }

        BuildTargetPidSet();
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
        
        // Dispose cached GPU counters
        DisposeGpuCounters();
        
        // Write telemetry diagnostics
        WriteTelemetryDiagnostics();
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

    public TelemetrySample? GetSampleAt(double timestampMs)
    {
        lock (_lock)
        {
            if (_samples.Count == 0)
            {
                return null;
            }

            // Binary search for the closest sample (samples are already sorted by timestamp)
            // This provides O(log n) performance instead of O(n) linear search
            var left = 0;
            var right = _samples.Count - 1;

            // Handle edge cases
            if (!_samples[left].TimestampMs.HasValue)
            {
                return _latestSample;
            }
            if (timestampMs <= _samples[left].TimestampMs!.Value)
            {
                return _samples[left];
            }
            if (!_samples[right].TimestampMs.HasValue)
            {
                return _latestSample;
            }
            if (timestampMs >= _samples[right].TimestampMs!.Value)
            {
                return _samples[right];
            }

            // Binary search to find the two samples bracketing the target timestamp
            while (right - left > 1)
            {
                var mid = (left + right) / 2;
                var midTs = _samples[mid].TimestampMs ?? 0;

                if (midTs <= timestampMs)
                {
                    left = mid;
                }
                else
                {
                    right = mid;
                }
            }

            // Return the closer of the two bracketing samples
            var leftDistance = Math.Abs((_samples[left].TimestampMs ?? 0) - timestampMs);
            var rightDistance = Math.Abs((_samples[right].TimestampMs ?? 0) - timestampMs);

            return leftDistance <= rightDistance ? _samples[left] : _samples[right];
        }
    }

    public void Dispose()
    {
        Stop();
        DisposeGpuCounters();
        _process?.Dispose();
        _process = null;
    }
    
    private void DisposeGpuCounters()
    {
        foreach (var counter in _gpuUtilizationCounters)
        {
            try 
            { 
                counter.Dispose(); 
            } 
            catch (ObjectDisposedException) 
            {
                // Counter was already disposed
            }
            catch (Exception ex)
            {
                // Log but don't throw - cleanup must continue
                _diagnostics.LastExceptionMessage = $"GPU counter disposal error: {ex.Message}";
            }
        }
        _gpuUtilizationCounters.Clear();
        
        foreach (var counter in _vramDedicatedCounters)
        {
            try 
            { 
                counter.Dispose(); 
            } 
            catch (ObjectDisposedException) 
            {
                // Counter was already disposed
            }
            catch (Exception ex)
            {
                _diagnostics.LastExceptionMessage = $"VRAM counter disposal error: {ex.Message}";
            }
        }
        _vramDedicatedCounters.Clear();
        
        foreach (var counter in _vramSharedCounters)
        {
            try 
            { 
                counter.Dispose(); 
            } 
            catch (ObjectDisposedException) 
            {
                // Counter was already disposed
            }
            catch (Exception ex)
            {
                _diagnostics.LastExceptionMessage = $"VRAM counter disposal error: {ex.Message}";
            }
        }
        _vramSharedCounters.Clear();
    }

    private void OnTimerTick(object? state)
    {
        if (!_isRunning)
        {
            return;
        }

        try
        {
            // Refresh PID set for multi-process apps periodically
            RefreshPidSetIfNeeded();
            
            var sample = CollectSample();
            if (sample != null)
            {
                lock (_lock)
                {
                    _samples.Add(sample);
                    _latestSample = sample;
                }
                
                // Log telemetry tick for diagnostics
                Log($"[Telemetry] tick: pid={_processId}, ws={sample.RamWorkingSetMB:F1}MB, private={sample.RamPrivateBytesMB:F1}MB, cpu={sample.CpuProcessPercent:F1}%, ioReadMBps={sample.DiskReadMBps:F2}");
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
        var timestampMs = (sampleTime - _startTime).TotalMilliseconds;

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
            DiskReadMBps: diskReadMBps,
            TimestampMs: timestampMs);
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
        _diagnostics.GpuEngineAvailable = false;
        _diagnostics.GpuProcessMemoryAvailable = false;
        
        try
        {
            // Check if GPU Engine category exists
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                _diagnostics.GpuEngineError = "GPU Engine category not found";
                Log("[Telemetry] GPU Engine performance counters not available - category missing");
            }
            else
            {
                _gpuEngineCategory = new PerformanceCounterCategory("GPU Engine");
                _diagnostics.GpuEngineAvailable = true;
                Log("[Telemetry] GPU Engine performance counters available");
            }

            // Check if GPU Process Memory category exists
            if (!PerformanceCounterCategory.Exists("GPU Process Memory"))
            {
                _diagnostics.GpuProcessMemoryError = "GPU Process Memory category not found";
                Log("[Telemetry] GPU Process Memory performance counters not available - category missing");
            }
            else
            {
                _gpuProcessMemoryCategory = new PerformanceCounterCategory("GPU Process Memory");
                _diagnostics.GpuProcessMemoryAvailable = true;
                Log("[Telemetry] GPU Process Memory performance counters available");
            }

            // Now cache and prime the counters for this specific PID
            CacheAndPrimeGpuCounters();
            
            _isGpuTelemetryAvailable = _gpuUtilizationCounters.Count > 0 || 
                                       _vramDedicatedCounters.Count > 0 || 
                                       _vramSharedCounters.Count > 0;
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Warning: Could not initialize GPU counters: {ex.Message}");
            _diagnostics.GpuEngineError = ex.Message;
            _isGpuTelemetryAvailable = false;
        }
    }
    
    private void CacheAndPrimeGpuCounters()
    {
        var matchedEngineInstances = new List<string>();
        var matchedMemoryInstances = new List<string>();
        var scannedInstanceExamples = new List<string>();
        var extractedPids = new HashSet<int>();
        
        // Cache GPU Engine counters (Utilization Percentage)
        if (_gpuEngineCategory != null)
        {
            try
            {
                var instanceNames = _gpuEngineCategory.GetInstanceNames();
                _diagnostics.TotalGpuEngineInstances = instanceNames.Length;
                Log($"[Telemetry] GPU Engine has {instanceNames.Length} total instances");
                
                // First pass: extract all PIDs from instances to understand what's available
                foreach (var instanceName in instanceNames.Take(20))
                {
                    scannedInstanceExamples.Add(instanceName);
                    var extractedPid = ExtractPidFromInstanceName(instanceName);
                    if (extractedPid.HasValue)
                    {
                        extractedPids.Add(extractedPid.Value);
                    }
                }
                
                // Second pass: match instances against our target PID set
                foreach (var instanceName in instanceNames)
                {
                    var instancePid = ExtractPidFromInstanceName(instanceName);
                    if (!instancePid.HasValue || !_targetPidSet.Contains(instancePid.Value))
                    {
                        continue;
                    }
                    
                    // Prefer 3D engine instances, but collect all for matching PIDs
                    var is3DEngine = instanceName.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) ||
                                     instanceName.Contains("engtype_Graphics", StringComparison.OrdinalIgnoreCase);
                    
                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instanceName, readOnly: true);
                        // PRIME the counter - first call returns 0 for rate counters
                        counter.NextValue();
                        _gpuUtilizationCounters.Add(counter);
                        matchedEngineInstances.Add(instanceName);
                        
                        if (is3DEngine)
                        {
                            Log($"[Telemetry] Cached 3D GPU Engine counter: {instanceName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Telemetry] Failed to create GPU Engine counter for {instanceName}: {ex.Message}");
                    }
                }
                
                Log($"[Telemetry] Cached {_gpuUtilizationCounters.Count} GPU utilization counters for PID set [{string.Join(",", _targetPidSet)}]");
            }
            catch (Exception ex)
            {
                Log($"[Telemetry] Error enumerating GPU Engine instances: {ex.Message}");
                _diagnostics.GpuEngineError = ex.Message;
            }
        }
        
        // Cache GPU Process Memory counters (Dedicated and Shared Usage)
        if (_gpuProcessMemoryCategory != null)
        {
            try
            {
                var instanceNames = _gpuProcessMemoryCategory.GetInstanceNames();
                _diagnostics.TotalGpuProcessMemoryInstances = instanceNames.Length;
                Log($"[Telemetry] GPU Process Memory has {instanceNames.Length} total instances");
                
                foreach (var instanceName in instanceNames)
                {
                    var instancePid = ExtractPidFromInstanceName(instanceName);
                    if (!instancePid.HasValue || !_targetPidSet.Contains(instancePid.Value))
                    {
                        continue;
                    }
                    
                    try
                    {
                        // Create Dedicated Usage counter
                        var dedicatedCounter = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", instanceName, readOnly: true);
                        dedicatedCounter.NextValue(); // Prime
                        _vramDedicatedCounters.Add(dedicatedCounter);
                        
                        // Create Shared Usage counter
                        var sharedCounter = new PerformanceCounter("GPU Process Memory", "Shared Usage", instanceName, readOnly: true);
                        sharedCounter.NextValue(); // Prime
                        _vramSharedCounters.Add(sharedCounter);
                        
                        matchedMemoryInstances.Add(instanceName);
                    }
                    catch (Exception ex)
                    {
                        Log($"[Telemetry] Failed to create VRAM counter for {instanceName}: {ex.Message}");
                    }
                }
                
                Log($"[Telemetry] Cached {_vramDedicatedCounters.Count} VRAM counters for PID set [{string.Join(",", _targetPidSet)}]");
            }
            catch (Exception ex)
            {
                Log($"[Telemetry] Error enumerating GPU Process Memory instances: {ex.Message}");
                _diagnostics.GpuProcessMemoryError = ex.Message;
            }
        }
        
        // Store diagnostics
        _diagnostics.GpuEngineMatchedCount = _gpuUtilizationCounters.Count;
        _diagnostics.GpuProcessMemoryMatchedCount = _vramDedicatedCounters.Count;
        _diagnostics.GpuEngineMatchedExamples = matchedEngineInstances.Take(5).ToList();
        _diagnostics.GpuProcessMemoryMatchedExamples = matchedMemoryInstances.Take(5).ToList();
        _diagnostics.ScannedInstanceExamples = scannedInstanceExamples.Take(10).ToList();
        _diagnostics.ExtractedPidsFromInstances = extractedPids.Take(20).ToList();
        
        // Record reason if matching failed
        if (_gpuUtilizationCounters.Count == 0 && _vramDedicatedCounters.Count == 0 && 
            (_diagnostics.TotalGpuEngineInstances > 0 || _diagnostics.TotalGpuProcessMemoryInstances > 0))
        {
            var targetPidStr = string.Join(",", _targetPidSet);
            var extractedPidStr = extractedPids.Count > 0 ? string.Join(",", extractedPids.Take(10)) : "(none)";
            _diagnostics.MatchingFailureReason = 
                $"No instances matched target PIDs [{targetPidStr}]. " +
                $"Extracted PIDs from instances: [{extractedPidStr}]. " +
                $"This may happen if the target process has no active GPU context.";
            Log($"[Telemetry] {_diagnostics.MatchingFailureReason}");
        }
    }

    /// <summary>
    /// Extracts the PID from a GPU performance counter instance name using regex.
    /// Handles common patterns like "pid_1234_luid_0x..." and variations.
    /// </summary>
    private static int? ExtractPidFromInstanceName(string instanceName)
    {
        var match = PidRegex.Match(instanceName);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var pid))
        {
            return pid;
        }
        return null;
    }

    private double? CollectGpuUtilizationPercent()
    {
        if (_gpuUtilizationCounters.Count == 0)
        {
            return null;
        }

        try
        {
            double totalUtilization = 0;
            var validReadings = 0;

            foreach (var counter in _gpuUtilizationCounters)
            {
                try
                {
                    var value = counter.NextValue();
                    if (value >= 0)
                    {
                        totalUtilization += value;
                        validReadings++;
                    }
                }
                catch
                {
                    // Counter may have become invalid (process closed GPU context)
                }
            }

            if (validReadings == 0)
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
        if (_vramDedicatedCounters.Count == 0)
        {
            return null;
        }

        try
        {
            double totalDedicated = 0;
            var validReadings = 0;

            foreach (var counter in _vramDedicatedCounters)
            {
                try
                {
                    var value = counter.NextValue();
                    if (value >= 0)
                    {
                        // Sum dedicated VRAM across all matching PIDs
                        totalDedicated += value;
                        validReadings++;
                    }
                }
                catch
                {
                    // Counter may have become invalid
                }
            }

            if (validReadings == 0)
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
        if (_vramSharedCounters.Count == 0)
        {
            return null;
        }

        try
        {
            double totalShared = 0;
            var validReadings = 0;

            foreach (var counter in _vramSharedCounters)
            {
                try
                {
                    var value = counter.NextValue();
                    if (value >= 0)
                    {
                        // Sum shared VRAM across all matching PIDs
                        totalShared += value;
                        validReadings++;
                    }
                }
                catch
                {
                    // Counter may have become invalid
                }
            }

            if (validReadings == 0)
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
    
    #region Telemetry Diagnostics
    
    private void WriteTelemetryDiagnostics()
    {
        if (string.IsNullOrWhiteSpace(_sessionFolder))
        {
            return;
        }
        
        try
        {
            _diagnostics.ProcessId = _processId;
            _diagnostics.SampleCount = _samples.Count;
            _diagnostics.IsGpuTelemetryAvailable = _isGpuTelemetryAvailable;
            
            var path = Path.Combine(_sessionFolder, "telemetry_diagnostics.json");
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(_diagnostics, options);
            File.WriteAllText(path, json);
            
            Log($"[Telemetry] Diagnostics written to: {path}");
        }
        catch (Exception ex)
        {
            Log($"[Telemetry] Failed to write diagnostics: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Sets the session folder for diagnostics output.
    /// </summary>
    public void SetSessionFolder(string? folder)
    {
        _sessionFolder = folder;
    }
    
    /// <summary>
    /// Gets the current telemetry diagnostics.
    /// </summary>
    public TelemetryDiagnostics GetDiagnostics()
    {
        _diagnostics.ProcessId = _processId;
        _diagnostics.SampleCount = _samples.Count;
        _diagnostics.IsGpuTelemetryAvailable = _isGpuTelemetryAvailable;
        return _diagnostics;
    }
    
    #endregion

    private void Log(string message)
    {
        _logger?.Invoke(message);
    }
}

/// <summary>
/// Diagnostics data for telemetry debugging.
/// </summary>
public sealed class TelemetryDiagnostics
{
    public int ProcessId { get; set; }
    public int SampleCount { get; set; }
    public bool IsGpuTelemetryAvailable { get; set; }
    
    // GPU Engine counters
    public bool GpuEngineAvailable { get; set; }
    public string? GpuEngineError { get; set; }
    public int GpuEngineMatchedCount { get; set; }
    public List<string>? GpuEngineMatchedExamples { get; set; }
    
    // GPU Process Memory counters
    public bool GpuProcessMemoryAvailable { get; set; }
    public string? GpuProcessMemoryError { get; set; }
    public int GpuProcessMemoryMatchedCount { get; set; }
    public List<string>? GpuProcessMemoryMatchedExamples { get; set; }
    
    // Multi-process app support
    public bool IsMultiProcessApp { get; set; }
    public List<int>? TargetPidSet { get; set; }
    public string? ProcessName { get; set; }
    
    // Instance scanning details (for debugging when matching fails)
    public int TotalGpuEngineInstances { get; set; }
    public int TotalGpuProcessMemoryInstances { get; set; }
    public List<string>? ScannedInstanceExamples { get; set; }
    public List<int>? ExtractedPidsFromInstances { get; set; }
    public string? MatchingFailureReason { get; set; }
    
    // General status
    public string? LastExceptionMessage { get; set; }
}

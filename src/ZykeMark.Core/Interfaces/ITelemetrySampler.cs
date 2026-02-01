using ZykeMark.Core.Models;

namespace ZykeMark.Core.Interfaces;

/// <summary>
/// Interface for sampling system telemetry data (CPU, GPU, RAM, etc.) for a target process.
/// Telemetry sampling runs independently of frame capture (PresentMon) and is used to enrich
/// frame samples with system performance data.
/// 
/// TELEMETRY SOURCES (Windows):
/// - CPU process %: Process.TotalProcessorTime delta / elapsed / ProcessorCount * 100
/// - Top thread CPU %: Enumerate Process.Threads, track max thread CPU using TotalProcessorTime delta
/// - RAM: Process.WorkingSet64 and Process.PrivateMemorySize64 (bytes → MB)
/// - Disk read MB/s: P/Invoke GetProcessIoCounters → IO_COUNTERS.ReadTransferCount delta → MB/s
/// - GPU Utilization %: Windows Performance Counters "GPU Engine(*)\Utilization Percentage", filter by pid_####
/// - VRAM: Windows Performance Counters "GPU Process Memory(*)\Dedicated Usage" and "Shared Usage", filter by pid_####
/// 
/// IMPORTANT: If telemetry counters are unavailable (permissions, missing counters), the sampler
/// should return null values for affected metrics. Frame capture must continue independently.
/// </summary>
public interface ITelemetrySampler : IDisposable
{
    /// <summary>
    /// Starts telemetry sampling for the specified process.
    /// Sampling runs on a background timer until Stop() is called.
    /// </summary>
    /// <param name="processId">The target process ID to monitor.</param>
    void Start(int processId);

    /// <summary>
    /// Stops telemetry sampling and releases resources.
    /// </summary>
    void Stop();

    /// <summary>
    /// Gets whether the sampler is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets whether GPU telemetry counters are available on this system.
    /// </summary>
    bool IsGpuTelemetryAvailable { get; }

    /// <summary>
    /// Attempts to get the latest telemetry sample.
    /// Returns null if no sample is available or sampling is not running.
    /// </summary>
    /// <returns>The latest telemetry sample, or null if unavailable.</returns>
    TelemetrySample? TryGetLatest();

    /// <summary>
    /// Gets all collected telemetry samples since Start() was called.
    /// Useful for computing aggregates over the sampling period.
    /// </summary>
    /// <returns>A read-only list of all collected samples.</returns>
    IReadOnlyList<TelemetrySample> GetSamples();
}

using ZykeMark.Core.Interfaces;
using ZykeMark.Core.Models;

namespace ZykeMark.Core.Tests;

/// <summary>
/// Fake implementation of ITelemetrySampler for testing.
/// Supports pre-configured samples with timestamps for testing timestamp-based matching.
/// </summary>
public sealed class FakeTelemetrySampler : ITelemetrySampler
{
    private readonly List<TelemetrySample> _samples = new();
    private TelemetrySample? _latestSample;

    public bool IsRunning { get; private set; }
    public bool IsGpuTelemetryAvailable { get; set; } = true;
    public double StartTimestampMs { get; private set; }

    /// <summary>
    /// Pre-populates the sampler with telemetry samples for testing.
    /// </summary>
    public void AddSamples(params TelemetrySample[] samples)
    {
        _samples.AddRange(samples);
        if (samples.Length > 0)
        {
            _latestSample = samples[^1];
        }
    }

    public void Start(int processId)
    {
        IsRunning = true;
        StartTimestampMs = 0;
    }

    public void Stop()
    {
        IsRunning = false;
    }

    public TelemetrySample? TryGetLatest() => _latestSample;

    public TelemetrySample? GetSampleAt(double timestampMs)
    {
        if (_samples.Count == 0)
        {
            return null;
        }

        // Find the sample closest to the requested timestamp
        TelemetrySample? closest = null;
        var minDistance = double.MaxValue;

        foreach (var sample in _samples)
        {
            if (sample.TimestampMs.HasValue)
            {
                var distance = Math.Abs(sample.TimestampMs.Value - timestampMs);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    closest = sample;
                }
            }
        }

        // Fall back to latest if no timestamped samples found
        return closest ?? _latestSample;
    }

    public IReadOnlyList<TelemetrySample> GetSamples() => _samples.ToArray();

    public void Dispose()
    {
        Stop();
    }
}

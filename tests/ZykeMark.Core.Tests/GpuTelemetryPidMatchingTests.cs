using System.Text.RegularExpressions;
using Xunit;

namespace ZykeMark.Core.Tests;

/// <summary>
/// Tests for GPU telemetry PID extraction and matching.
/// </summary>
public class GpuTelemetryPidMatchingTests
{
    // Pattern that matches the one in WindowsTelemetrySampler
    private static readonly Regex PidRegex = new(
        @"pid_(\d+)_",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Theory]
    [InlineData("pid_1234_luid_0x00000000_0x0000c3cb_phys_0_eng_0_engtype_3D", 1234)]
    [InlineData("pid_9068_luid_0x00000000_0x0000c3cb_phys_0_eng_0_engtype_VideoDecode", 9068)]
    [InlineData("pid_42_something", 42)]
    [InlineData("PID_5678_luid_test", 5678)] // Case insensitive
    public void ExtractPidFromInstanceName_ReturnsCorrectPid(string instanceName, int expectedPid)
    {
        // Act
        var match = PidRegex.Match(instanceName);
        
        // Assert
        Assert.True(match.Success);
        Assert.True(int.TryParse(match.Groups[1].Value, out var pid));
        Assert.Equal(expectedPid, pid);
    }

    [Theory]
    [InlineData("invalid_instance_name")]
    [InlineData("luid_0x00000000_phys_0")]
    [InlineData("no_pid_here")]
    [InlineData("")] // Empty string
    public void ExtractPidFromInstanceName_ReturnsNullForInvalidPatterns(string instanceName)
    {
        // Act
        var match = PidRegex.Match(instanceName);
        
        // Assert
        Assert.False(match.Success);
    }

    [Fact]
    public void ExtractPidFromInstanceName_HandlesMultipleFormats()
    {
        // These are common Windows GPU perf counter instance name formats
        var testCases = new Dictionary<string, int>
        {
            // Standard format from Windows 10+
            { "pid_1234_luid_0x00000000_0x0000c3cb_phys_0_eng_0_engtype_3D", 1234 },
            { "pid_9068_luid_0x00000000_0x0000c3cb_phys_0_eng_1_engtype_VideoDecode", 9068 },
            
            // GPU Process Memory format
            { "pid_5432_luid_0x00000000_0x0000c3cb", 5432 },
            
            // Edge cases
            { "pid_1_luid_test", 1 },
            { "pid_999999_luid_test", 999999 }
        };

        foreach (var (instanceName, expectedPid) in testCases)
        {
            var match = PidRegex.Match(instanceName);
            Assert.True(match.Success, $"Failed to match: {instanceName}");
            Assert.True(int.TryParse(match.Groups[1].Value, out var pid));
            Assert.Equal(expectedPid, pid);
        }
    }

    [Fact]
    public void PidSetMatching_WorksForMultiProcessApps()
    {
        // Simulating multi-process app like Edge with multiple PIDs
        var targetPidSet = new HashSet<int> { 1000, 1001, 1002, 1003 };
        
        var instanceNames = new[]
        {
            "pid_1000_luid_test_eng_0_engtype_3D",    // Main process
            "pid_1001_luid_test_eng_0_engtype_3D",    // GPU process
            "pid_1002_luid_test_eng_0_engtype_Copy",  // Another child
            "pid_9999_luid_test_eng_0_engtype_3D",    // Unrelated process
            "pid_8888_luid_test_eng_0_engtype_3D"     // Another unrelated
        };

        var matchedInstances = new List<string>();
        foreach (var instanceName in instanceNames)
        {
            var match = PidRegex.Match(instanceName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var pid))
            {
                if (targetPidSet.Contains(pid))
                {
                    matchedInstances.Add(instanceName);
                }
            }
        }

        // Should match 3 instances (pids 1000, 1001, 1002)
        Assert.Equal(3, matchedInstances.Count);
        Assert.All(matchedInstances, instance => 
            Assert.Contains("pid_100", instance));
    }
}

using ZykeMark.Core.Models;
using Xunit;

namespace ZykeMark.Core.Tests;

public class CaptureTargetTests
{
    [Theory]
    [InlineData("msedge")]
    [InlineData("msedge.exe")]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("firefox")]
    [InlineData("firefox.exe")]
    [InlineData("brave")]
    [InlineData("code")]
    [InlineData("discord")]
    [InlineData("teams")]
    [InlineData("slack")]
    [InlineData("spotify")]
    public void IsMultiProcessApplication_ReturnsTrue_ForKnownBrowsersAndElectronApps(string processName)
    {
        var target = new CaptureTarget(
            ProcessName: processName,
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.True(target.IsMultiProcessApplication);
    }

    [Theory]
    [InlineData("notepad.exe")]
    [InlineData("game.exe")]
    [InlineData("myapp")]
    [InlineData("vlc")]
    [InlineData("steam")]
    public void IsMultiProcessApplication_ReturnsFalse_ForOtherApplications(string processName)
    {
        var target = new CaptureTarget(
            ProcessName: processName,
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.False(target.IsMultiProcessApplication);
    }

    [Fact]
    public void IsMultiProcessApplication_ReturnsFalse_WhenProcessNameIsNull()
    {
        var target = new CaptureTarget(
            ProcessName: null,
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.False(target.IsMultiProcessApplication);
    }

    [Fact]
    public void IsMultiProcessApplication_ReturnsFalse_WhenProcessNameIsEmpty()
    {
        var target = new CaptureTarget(
            ProcessName: "",
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.False(target.IsMultiProcessApplication);
    }

    [Theory]
    [InlineData("MsEdge")]
    [InlineData("MSEDGE")]
    [InlineData("CHROME.EXE")]
    [InlineData("Firefox.Exe")]
    public void IsMultiProcessApplication_IsCaseInsensitive(string processName)
    {
        var target = new CaptureTarget(
            ProcessName: processName,
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.True(target.IsMultiProcessApplication);
    }

    [Fact]
    public void ToDisplayString_IncludesProcessName_AndPid()
    {
        var target = new CaptureTarget(
            ProcessName: "msedge.exe",
            ProcessId: 9068,
            SelectionMode: CaptureTarget.ModePid);

        var display = target.ToDisplayString();

        Assert.Contains("msedge.exe", display);
        Assert.Contains("9068", display);
        Assert.Contains("by PID", display);
    }

    [Fact]
    public void ToDisplayString_IncludesWindowTitle_WhenProvided()
    {
        var target = new CaptureTarget(
            ProcessName: "msedge.exe",
            ProcessId: 9068,
            SelectionMode: CaptureTarget.ModePid,
            WindowTitle: "YouTube - Watch Videos");

        var display = target.ToDisplayString();

        Assert.Contains("YouTube - Watch Videos", display);
    }
}

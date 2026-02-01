using ZykeMark.Core.Models;
using Xunit;

namespace ZykeMark.Core.Tests;

public class CaptureTargetTests
{
    [Theory]
    [InlineData("msedge", true)]
    [InlineData("msedge.exe", true)]
    [InlineData("chrome", true)]
    [InlineData("chrome.exe", true)]
    [InlineData("firefox", true)]
    [InlineData("firefox.exe", true)]
    [InlineData("brave", true)]
    [InlineData("code", true)]
    [InlineData("discord", true)]
    [InlineData("teams", true)]
    [InlineData("slack", true)]
    [InlineData("spotify", true)]
    public void IsMultiProcessApplication_ReturnsTrue_ForKnownBrowsersAndElectronApps(string processName, bool expected)
    {
        var target = new CaptureTarget(
            ProcessName: processName,
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.Equal(expected, target.IsMultiProcessApplication);
    }

    [Theory]
    [InlineData("notepad.exe", false)]
    [InlineData("game.exe", false)]
    [InlineData("myapp", false)]
    [InlineData("vlc", false)]
    [InlineData("steam", false)]
    public void IsMultiProcessApplication_ReturnsFalse_ForOtherApplications(string processName, bool expected)
    {
        var target = new CaptureTarget(
            ProcessName: processName,
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.Equal(expected, target.IsMultiProcessApplication);
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
    [InlineData("MsEdge", true)]
    [InlineData("MSEDGE", true)]
    [InlineData("CHROME.EXE", true)]
    [InlineData("Firefox.Exe", true)]
    public void IsMultiProcessApplication_IsCaseInsensitive(string processName, bool expected)
    {
        var target = new CaptureTarget(
            ProcessName: processName,
            ProcessId: 1234,
            SelectionMode: CaptureTarget.ModePid);

        Assert.Equal(expected, target.IsMultiProcessApplication);
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

using System.Text.Json;
using ZykeMark.Infrastructure.PresentMon;
using Xunit;

namespace ZykeMark.Core.Tests;

public class CaptureDiagnosticsTests
{
    [Fact]
    public void CaptureDiagnostics_CanBeSerializedToJson()
    {
        var diagnostics = new CaptureDiagnostics
        {
            PresentMonExePath = @"C:\Tools\PresentMon.exe",
            WorkingDirectory = @"C:\Sessions\abc123",
            ArgumentString = "--output_file presentmon.csv --process_name msedge.exe --timed 10",
            StartUtc = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            EndUtc = new DateTime(2024, 1, 1, 12, 0, 15, DateTimeKind.Utc),
            ExitCode = 0,
            StdOut = "Recording started",
            StdErr = "",
            OutputCsvPath = @"C:\Sessions\abc123\presentmon.csv",
            OutputCsvExists = true,
            FileSizeBytes = 12345,
            LastWriteTimeUtc = new DateTime(2024, 1, 1, 12, 0, 14, DateTimeKind.Utc),
            CsvPreviewLines = new List<string>
            {
                "Application,ProcessID,CPUStartTime,MsBetweenPresents",
                "msedge.exe,1234,1000.0,16.67",
                "msedge.exe,1234,1016.67,16.67"
            },
            ParsedHeaderColumns = new List<string> { "Application", "ProcessID", "CPUStartTime", "MsBetweenPresents" },
            ParsedRowCount = 100,
            Success = true
        };

        var json = JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true });
        
        Assert.Contains("PresentMonExePath", json);
        Assert.Contains("PresentMon.exe", json);
        Assert.Contains("msedge.exe", json);
        Assert.Contains("\"Success\": true", json);
    }

    [Fact]
    public void CaptureDiagnostics_GeneratesDetailedErrorMessage_WhenPresentMonDidNotStart()
    {
        var diagnostics = new CaptureDiagnostics
        {
            ExitCode = null, // Did not start
            FailureReason = "PresentMon process failed to start: file not found"
        };

        var message = diagnostics.GenerateDetailedErrorMessage();
        
        Assert.Contains("PresentMon failed to start", message);
    }

    [Fact]
    public void CaptureDiagnostics_GeneratesDetailedErrorMessage_WhenExitCodeNonZero()
    {
        var diagnostics = new CaptureDiagnostics
        {
            ExitCode = 1,
            StdErr = "Error: access denied"
        };

        var message = diagnostics.GenerateDetailedErrorMessage();
        
        Assert.Contains("exited with code 1", message);
        Assert.Contains("access denied", message);
    }

    [Fact]
    public void CaptureDiagnostics_GeneratesDetailedErrorMessage_WhenCsvNotFound()
    {
        var diagnostics = new CaptureDiagnostics
        {
            ExitCode = 0,
            OutputCsvExists = false,
            OutputCsvPath = @"C:\Sessions\abc123\presentmon.csv"
        };

        var message = diagnostics.GenerateDetailedErrorMessage();
        
        Assert.Contains("not found", message.ToLower());
    }

    [Fact]
    public void CaptureDiagnostics_GeneratesDetailedErrorMessage_WhenCsvIsEmpty()
    {
        var diagnostics = new CaptureDiagnostics
        {
            ExitCode = 0,
            OutputCsvExists = true,
            FileSizeBytes = 0
        };

        var message = diagnostics.GenerateDetailedErrorMessage();
        
        Assert.Contains("empty", message.ToLower());
    }

    [Fact]
    public void CaptureDiagnostics_GeneratesDetailedErrorMessage_WhenNoDataRows()
    {
        var diagnostics = new CaptureDiagnostics
        {
            ExitCode = 0,
            OutputCsvExists = true,
            FileSizeBytes = 100,
            ParsedHeaderColumns = new List<string> { "Application", "ProcessID" },
            ParsedRowCount = 0
        };

        var message = diagnostics.GenerateDetailedErrorMessage();
        
        Assert.Contains("no data rows", message.ToLower());
    }

    [Fact]
    public void CaptureDiagnostics_WriteToFile_CreatesValidJsonFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_diag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var diagnostics = new CaptureDiagnostics
            {
                ExitCode = 0,
                Success = true,
                ParsedRowCount = 50
            };

            var filePath = Path.Combine(tempDir, "capture_diagnostics.json");
            diagnostics.WriteToFile(filePath);

            Assert.True(File.Exists(filePath));
            
            var content = File.ReadAllText(filePath);
            var parsed = JsonSerializer.Deserialize<CaptureDiagnostics>(content);
            
            Assert.NotNull(parsed);
            Assert.Equal(0, parsed.ExitCode);
            Assert.True(parsed.Success);
            Assert.Equal(50, parsed.ParsedRowCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void CaptureDiagnostics_WriteToSessionFolder_CreatesFileInFolder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_session_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var diagnostics = new CaptureDiagnostics
            {
                ExitCode = 0,
                ArgumentString = "--process_name test.exe"
            };

            diagnostics.WriteToSessionFolder(tempDir);

            var expectedPath = Path.Combine(tempDir, "capture_diagnostics.json");
            Assert.True(File.Exists(expectedPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

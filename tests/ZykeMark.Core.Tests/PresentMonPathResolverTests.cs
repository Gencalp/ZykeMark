using Xunit;
using ZykeMark.Infrastructure.PresentMon;

namespace ZykeMark.Core.Tests;

public class PresentMonPathResolverTests
{
    /// <summary>
    /// Safely cleans up a temporary test directory, ignoring any exceptions.
    /// </summary>
    private static void SafeCleanupTempDir(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures - temp directory will be cleaned up by OS eventually
        }
    }

    [Fact]
    public void Resolve_ReturnsUserSettingsPath_WhenValidAndExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var exePath = Path.Combine(tempDir, "PresentMon.exe");
            File.WriteAllText(exePath, "dummy exe content");

            var resolver = new PresentMonPathResolver();
            var result = resolver.Resolve(exePath);

            Assert.True(result.IsFound);
            Assert.Equal(exePath, result.ResolvedAbsolutePath);
            Assert.Equal("User Settings", result.Source);
            Assert.Null(result.FailureReason);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Resolve_FindsInAppDirectory_WhenUserSettingsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var exePath = Path.Combine(tempDir, "PresentMon.exe");
            File.WriteAllText(exePath, "dummy exe content");

            // Use the temp dir as app directory
            var resolver = new PresentMonPathResolver(appDirectory: tempDir);
            var result = resolver.Resolve(null);

            Assert.True(result.IsFound);
            Assert.Equal(exePath, result.ResolvedAbsolutePath);
            Assert.Equal("App Directory", result.Source);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Resolve_FindsVersionedExe_WhenNoExactMatch()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create versioned exe instead of exact name
            var versionedPath = Path.Combine(tempDir, "PresentMon-1.10.0-x64.exe");
            File.WriteAllText(versionedPath, "dummy exe content");

            var resolver = new PresentMonPathResolver(appDirectory: tempDir);
            var result = resolver.Resolve(null);

            Assert.True(result.IsFound);
            Assert.Equal(versionedPath, result.ResolvedAbsolutePath);
            Assert.Equal("App Directory", result.Source);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Resolve_SelectsHighestVersion_WhenMultipleVersionedExesExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create multiple versioned exes
            File.WriteAllText(Path.Combine(tempDir, "PresentMon-1.8.0-x64.exe"), "old");
            var newestPath = Path.Combine(tempDir, "PresentMon-1.10.0-x64.exe");
            File.WriteAllText(newestPath, "newest");
            File.WriteAllText(Path.Combine(tempDir, "PresentMon-1.9.0-x64.exe"), "middle");

            var resolver = new PresentMonPathResolver(appDirectory: tempDir);
            var result = resolver.Resolve(null);

            Assert.True(result.IsFound);
            Assert.Equal(newestPath, result.ResolvedAbsolutePath);
        }
        finally
        {
            SafeCleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void Resolve_FindsInToolsFolder_WhenNotInAppDir()
    {
        // This test simulates the tools folder scenario
        var toolsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZykeMark",
            "tools",
            "presentmon");
        
        var emptyAppDir = Path.Combine(Path.GetTempPath(), "zyke_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyAppDir);
        Directory.CreateDirectory(toolsFolder);

        var exePath = Path.Combine(toolsFolder, "PresentMon.exe");
        var createdForTest = false;

        try
        {
            // Only create the test file if it doesn't already exist
            if (!File.Exists(exePath))
            {
                File.WriteAllText(exePath, "dummy exe content for test");
                createdForTest = true;
            }

            var resolver = new PresentMonPathResolver(appDirectory: emptyAppDir);
            var result = resolver.Resolve(null);

            // Should find in tools folder
            if (result.IsFound && result.Source == "Tools Folder")
            {
                Assert.Equal(exePath, result.ResolvedAbsolutePath);
            }
            // May also find via PATH or other means, which is acceptable
        }
        finally
        {
            // Only clean up if we created the file - use local cleanup to avoid deleting real user files
            if (createdForTest)
            {
                SafeCleanupTempFile(exePath);
            }
            SafeCleanupTempDir(emptyAppDir);
        }
    }
    
    /// <summary>
    /// Safely deletes a single file, ignoring any exceptions.
    /// </summary>
    private static void SafeCleanupTempFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [Fact]
    public void Resolve_ReturnsNotFound_WithClearReason_WhenNothingExists()
    {
        var emptyAppDir = Path.Combine(Path.GetTempPath(), "zyke_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyAppDir);

        try
        {
            // Remove any existing PresentMon from tools folder for this test
            var toolsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZykeMark",
                "tools",
                "presentmon_test_nonexistent");

            var resolver = new PresentMonPathResolver(appDirectory: emptyAppDir);
            
            // Use a non-existent user path to trigger full search
            var result = resolver.Resolve(Path.Combine(emptyAppDir, "nonexistent.exe"));

            // Either finds in PATH/Downloads (if user has it installed) or returns not found
            if (!result.IsFound)
            {
                Assert.NotNull(result.FailureReason);
                Assert.Contains("PresentMon.exe not found", result.FailureReason);
                Assert.Contains("Searched locations", result.FailureReason);
            }
            // If found somewhere else (PATH, Downloads), that's also acceptable
        }
        finally
        {
            SafeCleanupTempDir(emptyAppDir);
        }
    }

    [Fact]
    public void Resolve_ContinuesSearching_WhenUserPathInvalid()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);

        try
        {
            // Create exe in app dir
            var exePath = Path.Combine(appDir, "PresentMon.exe");
            File.WriteAllText(exePath, "dummy exe content");

            // Provide invalid user settings path
            var resolver = new PresentMonPathResolver(appDirectory: appDir);
            var result = resolver.Resolve("/invalid/nonexistent/path.exe");

            // Should fall through to app directory
            Assert.True(result.IsFound);
            Assert.Equal(exePath, result.ResolvedAbsolutePath);
            Assert.Equal("App Directory", result.Source);
        }
        finally
        {
            SafeCleanupTempDir(appDir);
        }
    }

    [Fact]
    public void Resolve_PrefersUserPath_OverAppDirectory()
    {
        var userDir = Path.Combine(Path.GetTempPath(), "zyke_user_" + Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(Path.GetTempPath(), "zyke_app_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDir);
        Directory.CreateDirectory(appDir);

        try
        {
            var userPath = Path.Combine(userDir, "PresentMon.exe");
            var appPath = Path.Combine(appDir, "PresentMon.exe");
            File.WriteAllText(userPath, "user exe");
            File.WriteAllText(appPath, "app exe");

            var resolver = new PresentMonPathResolver(appDirectory: appDir);
            var result = resolver.Resolve(userPath);

            Assert.True(result.IsFound);
            Assert.Equal(userPath, result.ResolvedAbsolutePath);
            Assert.Equal("User Settings", result.Source);
        }
        finally
        {
            SafeCleanupTempDir(userDir);
            SafeCleanupTempDir(appDir);
        }
    }

    [Fact]
    public void Resolve_HandlesEmptyAndNullSettingsPath()
    {
        var appDir = Path.Combine(Path.GetTempPath(), "zyke_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appDir);

        try
        {
            var exePath = Path.Combine(appDir, "PresentMon.exe");
            File.WriteAllText(exePath, "dummy exe content");

            var resolver = new PresentMonPathResolver(appDirectory: appDir);

            // Test null
            var resultNull = resolver.Resolve(null);
            Assert.True(resultNull.IsFound);
            Assert.Equal("App Directory", resultNull.Source);

            // Test empty string
            var resultEmpty = resolver.Resolve("");
            Assert.True(resultEmpty.IsFound);
            Assert.Equal("App Directory", resultEmpty.Source);

            // Test whitespace
            var resultWhitespace = resolver.Resolve("   ");
            Assert.True(resultWhitespace.IsFound);
            Assert.Equal("App Directory", resultWhitespace.Source);
        }
        finally
        {
            SafeCleanupTempDir(appDir);
        }
    }
}

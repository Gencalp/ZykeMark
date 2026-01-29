using System.IO;

namespace ZykeMark.App.Desktop.Services;

public sealed class DesktopLogger : IDisposable
{
    private readonly string _logPath;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private bool _disposed;

    public DesktopLogger()
    {
        var logsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZykeMark",
            "logs");
        Directory.CreateDirectory(logsFolder);
        _logPath = Path.Combine(logsFolder, "desktop.log");
    }

    public void Log(string message)
    {
        if (_disposed) return;

        var timestamped = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] {message}";

        lock (_lock)
        {
            try
            {
                EnsureWriter();
                _writer?.WriteLine(timestamped);
                _writer?.Flush();
            }
            catch
            {
                // Logging should never throw
            }
        }
    }

    public void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex is null
            ? $"ERROR: {message}"
            : $"ERROR: {message}\n{ex}";
        Log(fullMessage);
    }

    private void EnsureWriter()
    {
        _writer ??= new StreamWriter(_logPath, append: true) { AutoFlush = true };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

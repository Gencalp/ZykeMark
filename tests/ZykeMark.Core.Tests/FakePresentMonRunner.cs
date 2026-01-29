using System.Runtime.CompilerServices;
using ZykeMark.Infrastructure.PresentMon;

namespace ZykeMark.Core.Tests;

public sealed class FakePresentMonRunner : IPresentMonRunner
{
    private readonly IReadOnlyList<string> _lines;
    private readonly string? _csvPath;

    public FakePresentMonRunner(IEnumerable<string> lines)
    {
        _lines = lines.ToList();
        _csvPath = null;
    }

    /// <summary>
    /// Creates a fake runner that writes the lines to a CSV file and returns the path.
    /// Used for testing file-based collection.
    /// </summary>
    public FakePresentMonRunner(IEnumerable<string> lines, string csvPath)
    {
        _lines = lines.ToList();
        _csvPath = csvPath;
    }

    public async Task<PresentMonRunResult> RunToFileAsync(
        PresentMonRunOptions options,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (_csvPath is not null)
        {
            // Write lines to the CSV file
            var directory = Path.GetDirectoryName(_csvPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllLinesAsync(_csvPath, _lines, cancellationToken);
            return new PresentMonRunResult(_csvPath, 0, "", "");
        }

        // If no CSV path is configured, return null (stdout mode fallback)
        return new PresentMonRunResult(null, 0, string.Join(Environment.NewLine, _lines), "");
    }

    public async IAsyncEnumerable<string> RunAsync(
        PresentMonRunOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var line in _lines)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            await Task.Yield();
            yield return line;
        }
    }
}

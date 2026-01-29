namespace ZykeMark.Infrastructure.PresentMon;

public interface IPresentMonRunner
{
    /// <summary>
    /// Runs PresentMon and returns the result including the CSV output file path.
    /// When file output mode is used, the CSV data should be read from result.CsvPath,
    /// not from the returned async enumerable (which only contains stdout/diagnostic messages).
    /// </summary>
    Task<PresentMonRunResult> RunToFileAsync(PresentMonRunOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// [Deprecated for file output mode] Runs PresentMon and streams stdout lines.
    /// When using file output mode (SessionFolder is set), use RunToFileAsync instead
    /// and read CSV data directly from the file.
    /// </summary>
    IAsyncEnumerable<string> RunAsync(PresentMonRunOptions options, CancellationToken cancellationToken);
}

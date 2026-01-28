namespace ZykeMark.Infrastructure.PresentMon;

public interface IPresentMonRunner
{
    IAsyncEnumerable<string> RunAsync(PresentMonRunOptions options, CancellationToken cancellationToken);
}

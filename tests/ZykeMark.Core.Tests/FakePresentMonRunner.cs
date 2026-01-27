using System.Runtime.CompilerServices;
using ZykeMark.Infrastructure.PresentMon;

namespace ZykeMark.Core.Tests;

public sealed class FakePresentMonRunner : IPresentMonRunner
{
    private readonly IReadOnlyList<string> _lines;

    public FakePresentMonRunner(IEnumerable<string> lines)
    {
        _lines = lines.ToList();
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

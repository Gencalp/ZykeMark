using ZykeMark.Core.Models;

namespace ZykeMark.Core.Interfaces;

public interface ICollector
{
    IReadOnlyList<FrameSample> Collect(TimeSpan duration);
}

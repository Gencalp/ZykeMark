using ZykeMark.Core.Models;

namespace ZykeMark.Core.Interfaces;

public interface IAggregator
{
    SessionAggregates Aggregate(IReadOnlyList<FrameSample> samples);
}

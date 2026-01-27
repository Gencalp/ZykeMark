namespace ZykeMark.Core.Models;

public sealed record RawSampleChunk(
    int ChunkIndex,
    DateTime StartUtc,
    DateTime EndUtc,
    FrameSample[] Samples);

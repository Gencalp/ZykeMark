using ZykeMark.Core.Models;

namespace ZykeMark.Core.Interfaces;

public interface ILocalStore
{
    string CreateSession(SessionMetadata metadata);
    void WriteMetadata(SessionMetadata metadata);
    SessionMetadata ReadMetadata(string sessionId);
    void AppendChunk(string sessionId, RawSampleChunk chunk);
    IReadOnlyList<RawSampleChunk> ReadChunks(string sessionId);
    string WriteSummary(string sessionId, object summaryPayload);
}

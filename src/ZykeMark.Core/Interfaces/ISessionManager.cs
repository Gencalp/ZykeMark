using ZykeMark.Core.Models;

namespace ZykeMark.Core.Interfaces;

public interface ISessionManager
{
    SessionMetadata StartSession(string? gameName, string? buildVersion, RunConfig? runConfig = null);
    string StopSession(string? sessionId = null, DataQuality? dataQuality = null);
    SessionMetadata? GetCurrentSession();
}

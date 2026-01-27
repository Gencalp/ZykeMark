namespace ZykeMark.Core.Models;

public sealed record RunConfig(
    string? Api = null,
    string? Resolution = null,
    string? Preset = null);

public sealed record SessionMetadata(
    string SessionId,
    DateTime StartedAtUtc,
    DateTime? EndedAtUtc,
    long? DurationMs,
    string? GameName,
    string? BuildVersion,
    RunConfig? RunConfig);

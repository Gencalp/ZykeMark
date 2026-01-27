namespace ZykeMark.Infrastructure.PresentMon;

public sealed record PresentMonRunOptions(
    string? PresentMonPath,
    string? ProcessName,
    int? ProcessId,
    int DurationSeconds);

namespace ModService.Core.Processes;

public sealed class ProcessSnapshot
{
    public required int ProcessId { get; init; }

    public required string ProcessName { get; init; }

    public required string ExePath { get; init; }

    public required ulong ProcessCreateTimeUtc100ns { get; init; }

    public required IReadOnlyDictionary<string, string> Environment { get; init; }
}

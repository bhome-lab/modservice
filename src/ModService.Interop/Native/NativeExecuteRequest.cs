namespace ModService.Interop.Native;

public sealed class NativeExecuteRequest
{
    public required uint ProcessId { get; init; }

    public required ulong ProcessCreateTimeUtc100ns { get; init; }

    public required string ExecutablePath { get; init; }

    public required IReadOnlyList<string> ModulePaths { get; init; }

    public required IReadOnlyList<NativeEnvironmentVariable> EnvironmentVariables { get; init; }

    public uint TimeoutMs { get; init; } = 1_000;
}

public sealed class NativeEnvironmentVariable
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

public sealed class NativeExecuteResult(NativeExecuteStatus status, string? errorText)
{
    public NativeExecuteStatus Status { get; } = status;

    public string? ErrorText { get; } = errorText;

    public bool IsSuccess => Status == NativeExecuteStatus.Ok;
}

public enum NativeExecuteStatus
{
    Ok = 0,
    InvalidArgument = 1,
    TargetNotFound = 2,
    TargetChanged = 3,
    Timeout = 4,
    ExecutionFailed = 5
}

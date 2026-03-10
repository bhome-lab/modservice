namespace ModService.Core.Execution;

public sealed class ResolvedExecutionPlan
{
    public required string RuleName { get; init; }

    public required IReadOnlyList<string> ModulePaths { get; init; }

    public required IReadOnlyList<ResolvedEnvironmentVariable> EnvironmentVariables { get; init; }

    public required IReadOnlyList<ResolvedExecutorOption> ExecutorOptions { get; init; }
}

public sealed class ResolvedEnvironmentVariable
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

public sealed class ResolvedExecutorOption
{
    public required string Name { get; init; }

    public required string Value { get; init; }
}

public sealed class SourceAsset
{
    public required string SourceId { get; init; }

    public required string AssetName { get; init; }

    public required string FullPath { get; init; }
}

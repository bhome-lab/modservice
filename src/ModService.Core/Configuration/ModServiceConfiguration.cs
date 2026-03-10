namespace ModService.Core.Configuration;

public sealed class ModServiceConfiguration
{
    public PollingConfiguration Polling { get; set; } = new();

    public ProcessMonitoringConfiguration ProcessMonitoring { get; set; } = new();

    public ExecutorConfiguration Executor { get; set; } = new();

    public List<SourceConfiguration> Sources { get; set; } = [];

    public List<MatchCriteria> Excludes { get; set; } = [];

    public List<RuleConfiguration> Rules { get; set; } = [];
}

public sealed class PollingConfiguration
{
    public int IntervalSeconds { get; set; } = 300;

    public int JitterSeconds { get; set; } = 30;
}

public sealed class ProcessMonitoringConfiguration
{
    public bool Enabled { get; set; } = true;

    public int ScanIntervalSeconds { get; set; } = 2;
}

public sealed class ExecutorConfiguration
{
    public string Source { get; set; } = string.Empty;

    public string Asset { get; set; } = string.Empty;

    public List<ExecutorOptionConfiguration> Options { get; set; } = [];
}

public sealed class SourceConfiguration
{
    public string Id { get; set; } = string.Empty;

    public string Repo { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];

    public List<ArchiveConfiguration> Archives { get; set; } = [];
}

public sealed class ArchiveConfiguration
{
    public string Asset { get; set; } = string.Empty;

    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];
}

public class MatchCriteria
{
    public string? Process { get; set; }

    public string? Path { get; set; }

    public List<EnvMatchCondition> Env { get; set; } = [];
}

public sealed class RuleConfiguration : MatchCriteria
{
    public string? Name { get; set; }

    public List<string> PassEnvironment { get; set; } = [];

    public List<ExecutorOptionConfiguration> ExecutorOptions { get; set; } = [];

    public List<BindingConfiguration> Bindings { get; set; } = [];
}

public sealed class BindingConfiguration
{
    public string Source { get; set; } = string.Empty;

    public List<string> Include { get; set; } = [];

    public List<string> Exclude { get; set; } = [];
}

public sealed class EnvMatchCondition
{
    public string Name { get; set; } = string.Empty;

    public string Op { get; set; } = "exists";

    public string? Value { get; set; }
}

public sealed class ExecutorOptionConfiguration
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

using ModService.Core.Configuration;
using ModService.Core.Execution;
using ModService.Core.Processes;

namespace ModService.Core.Matching;

public sealed class RuleResolver
{
    public ResolvedExecutionPlan? Resolve(
        ModServiceConfiguration configuration,
        ProcessSnapshot snapshot,
        IReadOnlyCollection<SourceAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(assets);

        if (configuration.Excludes.Any(exclude => Matches(exclude, snapshot)))
        {
            return null;
        }

        foreach (var rule in configuration.Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (!Matches(rule, snapshot))
            {
                continue;
            }

            var resolvedModules = ResolveModules(rule, assets);
            var resolvedEnvironment = ResolveEnvironment(rule, snapshot);
            var resolvedExecutorOptions = ResolveExecutorOptions(configuration.Executor, rule);

            return new ResolvedExecutionPlan
            {
                RuleName = string.IsNullOrWhiteSpace(rule.Name) ? snapshot.ProcessName : rule.Name,
                ModulePaths = resolvedModules,
                EnvironmentVariables = resolvedEnvironment,
                ExecutorOptions = resolvedExecutorOptions
            };
        }

        return null;
    }

    public bool Matches(MatchCriteria criteria, ProcessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!string.IsNullOrWhiteSpace(criteria.Process) && !GlobPattern.IsMatch(criteria.Process, snapshot.ProcessName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(criteria.Path) &&
            snapshot.ExePath.IndexOf(criteria.Path, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        foreach (var env in criteria.Env)
        {
            if (!snapshot.Environment.TryGetValue(env.Name, out var value))
            {
                return false;
            }

            if (string.Equals(env.Op, "equals", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(value, env.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<string> ResolveModules(RuleConfiguration rule, IReadOnlyCollection<SourceAsset> assets)
    {
        var result = new List<string>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var binding in rule.Bindings)
        {
            var matchingAssets = assets
                .Where(asset => string.Equals(asset.SourceId, binding.Source, StringComparison.OrdinalIgnoreCase))
                .Where(asset => asset.AssetName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(asset => MatchesAny(binding.Include, asset.AssetName, defaultIfEmpty: true))
                .Where(asset => !MatchesAny(binding.Exclude, asset.AssetName, defaultIfEmpty: false))
                .OrderBy(asset => asset.AssetName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.FullPath, StringComparer.OrdinalIgnoreCase);

            foreach (var asset in matchingAssets)
            {
                var key = string.Create(
                    asset.SourceId.Length + asset.AssetName.Length + asset.FullPath.Length + 2,
                    asset,
                    static (span, value) =>
                    {
                        value.SourceId.AsSpan().CopyTo(span);
                        span[value.SourceId.Length] = '|';
                        value.AssetName.AsSpan().CopyTo(span[(value.SourceId.Length + 1)..]);
                        var secondSeparator = value.SourceId.Length + value.AssetName.Length + 1;
                        span[secondSeparator] = '|';
                        value.FullPath.AsSpan().CopyTo(span[(secondSeparator + 1)..]);
                    });

                if (dedupe.Add(key))
                {
                    result.Add(asset.FullPath);
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<ResolvedEnvironmentVariable> ResolveEnvironment(RuleConfiguration rule, ProcessSnapshot snapshot)
    {
        return rule.ApplyEnvironment
            .OrderBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase)
            .Select(variable => new ResolvedEnvironmentVariable
            {
                Name = variable.Key,
                Value = variable.Value
            })
            .ToArray();
    }

    private static IReadOnlyList<ResolvedExecutorOption> ResolveExecutorOptions(
        ExecutorConfiguration executor,
        RuleConfiguration rule)
    {
        var options = new List<ResolvedExecutorOption>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        ApplyOptions(executor.Options, options, indexes);
        ApplyOptions(rule.ExecutorOptions, options, indexes);

        return options;
    }

    private static void ApplyOptions(
        IEnumerable<ExecutorOptionConfiguration> configuredOptions,
        List<ResolvedExecutorOption> options,
        Dictionary<string, int> indexes)
    {
        foreach (var configuredOption in configuredOptions)
        {
            var resolved = new ResolvedExecutorOption
            {
                Name = configuredOption.Name,
                Value = configuredOption.Value
            };

            if (indexes.TryGetValue(configuredOption.Name, out var existingIndex))
            {
                options[existingIndex] = resolved;
                continue;
            }

            indexes[configuredOption.Name] = options.Count;
            options.Add(resolved);
        }
    }

    private static bool MatchesAny(IEnumerable<string> patterns, string value, bool defaultIfEmpty)
    {
        var any = false;
        foreach (var pattern in patterns)
        {
            any = true;
            if (GlobPattern.IsMatch(pattern, value))
            {
                return true;
            }
        }

        return !any && defaultIfEmpty;
    }
}

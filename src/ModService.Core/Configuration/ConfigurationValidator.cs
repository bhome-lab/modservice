using System.Collections.ObjectModel;

namespace ModService.Core.Configuration;

public static class ConfigurationValidator
{
    public static IReadOnlyList<string> Validate(ModServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (configuration.Polling.IntervalSeconds <= 0)
        {
            errors.Add("Polling intervalSeconds must be greater than zero.");
        }

        if (configuration.Polling.JitterSeconds < 0)
        {
            errors.Add("Polling jitterSeconds cannot be negative.");
        }

        if (configuration.ProcessMonitoring.Enabled && configuration.ProcessMonitoring.ScanIntervalSeconds <= 0)
        {
            errors.Add("Process monitoring scanIntervalSeconds must be greater than zero when process monitoring is enabled.");
        }

        foreach (var source in configuration.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                errors.Add("Source id is required.");
                continue;
            }

            if (!sources.Add(source.Id))
            {
                errors.Add($"Duplicate source id '{source.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(source.Repo))
            {
                errors.Add($"Source '{source.Id}' is missing repo.");
            }

            if (string.IsNullOrWhiteSpace(source.Tag))
            {
                errors.Add($"Source '{source.Id}' is missing tag.");
            }

            ValidatePatterns($"Source '{source.Id}' include", source.Include, errors);
            ValidatePatterns($"Source '{source.Id}' exclude", source.Exclude, errors);

            var archiveAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var archive in source.Archives)
            {
                if (string.IsNullOrWhiteSpace(archive.Asset))
                {
                    errors.Add($"Source '{source.Id}' contains an archive without an asset name.");
                    continue;
                }

                if (!archiveAssets.Add(archive.Asset))
                {
                    errors.Add($"Source '{source.Id}' contains duplicate archive asset '{archive.Asset}'.");
                }

                ValidatePatterns($"Archive '{archive.Asset}' include", archive.Include, errors);
                ValidatePatterns($"Archive '{archive.Asset}' exclude", archive.Exclude, errors);
            }
        }

        if (string.IsNullOrWhiteSpace(configuration.Executor.Source))
        {
            errors.Add("Executor source is required.");
        }
        else if (!sources.Contains(configuration.Executor.Source))
        {
            errors.Add($"Executor source '{configuration.Executor.Source}' was not declared.");
        }

        if (string.IsNullOrWhiteSpace(configuration.Executor.Asset))
        {
            errors.Add("Executor asset is required.");
        }

        ValidateExecutorOptions("Executor options", configuration.Executor.Options, errors);

        for (var ruleIndex = 0; ruleIndex < configuration.Rules.Count; ruleIndex++)
        {
            var rule = configuration.Rules[ruleIndex];
            if (rule.Bindings.Count == 0)
            {
                errors.Add($"Rule {DescribeRule(rule, ruleIndex)} must declare at least one binding.");
            }

            foreach (var binding in rule.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Source))
                {
                    errors.Add($"Rule {DescribeRule(rule, ruleIndex)} contains a binding without a source.");
                    continue;
                }

                if (!sources.Contains(binding.Source))
                {
                    errors.Add($"Rule {DescribeRule(rule, ruleIndex)} references unknown source '{binding.Source}'.");
                }
            }

            ValidateEnvironment(rule.Env, $"Rule {DescribeRule(rule, ruleIndex)}", errors);
            ValidateApplyEnvironment(rule.ApplyEnvironment, $"Rule {DescribeRule(rule, ruleIndex)}", errors);
            ValidateExecutorOptions($"Rule {DescribeRule(rule, ruleIndex)} executorOptions", rule.ExecutorOptions, errors);
        }

        for (var excludeIndex = 0; excludeIndex < configuration.Excludes.Count; excludeIndex++)
        {
            ValidateEnvironment(configuration.Excludes[excludeIndex].Env, $"Exclude #{excludeIndex + 1}", errors);
        }

        return new ReadOnlyCollection<string>(errors);
    }

    private static void ValidateEnvironment(IEnumerable<EnvMatchCondition> conditions, string owner, List<string> errors)
    {
        foreach (var condition in conditions)
        {
            if (string.IsNullOrWhiteSpace(condition.Name))
            {
                errors.Add($"{owner} contains an environment matcher without a name.");
            }

            if (!string.Equals(condition.Op, "exists", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(condition.Op, "equals", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{owner} contains unsupported environment matcher operator '{condition.Op}'.");
            }

            if (string.Equals(condition.Op, "equals", StringComparison.OrdinalIgnoreCase) && condition.Value is null)
            {
                errors.Add($"{owner} uses 'equals' for environment variable '{condition.Name}' without a value.");
            }
        }
    }

    private static void ValidatePatterns(string owner, IEnumerable<string> patterns, List<string> errors)
    {
        var index = 0;
        foreach (var pattern in patterns)
        {
            index++;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                errors.Add($"{owner} contains an empty pattern at index {index}.");
            }
        }
    }

    private static void ValidateApplyEnvironment(
        IReadOnlyDictionary<string, string> variables,
        string owner,
        List<string> errors)
    {
        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Key))
            {
                errors.Add($"{owner} applyEnvironment contains an empty variable name.");
            }

            if (variable.Value is null)
            {
                errors.Add($"{owner} applyEnvironment contains variable '{variable.Key}' with a null value.");
            }
        }
    }

    private static void ValidateExecutorOptions(string owner, IEnumerable<ExecutorOptionConfiguration> options, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var option in options)
        {
            index++;
            if (string.IsNullOrWhiteSpace(option.Name))
            {
                errors.Add($"{owner} contains an option without a name at index {index}.");
                continue;
            }

            if (!seen.Add(option.Name))
            {
                errors.Add($"{owner} contains duplicate option '{option.Name}'.");
            }

            if (option.Value is null)
            {
                errors.Add($"{owner} contains option '{option.Name}' with a null value.");
            }
        }
    }

    private static string DescribeRule(RuleConfiguration rule, int ruleIndex)
        => !string.IsNullOrWhiteSpace(rule.Name) ? $"'{rule.Name}'" : $"#{ruleIndex + 1}";
}

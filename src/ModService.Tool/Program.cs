using System.Diagnostics;
using System.Text.Json;
using ModService.Core.Configuration;
using ModService.Core.Matching;
using ModService.Core.Processes;
using ModService.Core.Updates;
using ModService.GitHub.Gh;
using ModService.Interop.Native;

return await new ToolApplication().RunAsync(args);

internal sealed class ToolApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var command = args[0].ToLowerInvariant();
        var options = CommandOptions.Parse(args.Skip(1).ToArray());

        try
        {
            return command switch
            {
                "validate" => Validate(options),
                "sync" => await SyncAsync(options, CancellationToken.None),
                "executor-path" => ShowExecutorPath(options),
                "resolve" => Resolve(options),
                "smoke-test" => await SmokeTestAsync(options, CancellationToken.None),
                "cleanup" => Cleanup(options),
                _ => Unknown(command)
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private int Validate(CommandOptions options)
    {
        var configuration = LoadConfiguration(options.ConfigPath);
        var errors = ConfigurationValidator.Validate(configuration);
        if (errors.Count == 0)
        {
            Console.WriteLine("Configuration is valid.");
            return 0;
        }

        foreach (var error in errors)
        {
            Console.WriteLine($"- {error}");
        }

        return 1;
    }

    private async Task<int> SyncAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        var configuration = LoadConfiguration(options.ConfigPath);
        var syncService = CreateSyncService(options.CacheRoot);
        var results = await syncService.SyncAsync(configuration, cancellationToken);
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Manifest.SourceId}: {result.Manifest.CurrentAssets.Count} current assets, downloaded [{string.Join(", ", result.DownloadedAssets)}]");
        }

        Console.WriteLine($"Executor: {syncService.ResolveExecutorPath(configuration)}");
        return 0;
    }

    private int ShowExecutorPath(CommandOptions options)
    {
        var configuration = LoadConfiguration(options.ConfigPath);
        var syncService = CreateSyncService(options.CacheRoot);
        Console.WriteLine(syncService.ResolveExecutorPath(configuration));
        return 0;
    }

    private int Resolve(CommandOptions options)
    {
        var configuration = LoadConfiguration(options.ConfigPath);
        var syncService = CreateSyncService(options.CacheRoot);
        var snapshot = BuildSnapshot(options);
        var resolver = new RuleResolver();
        var plan = resolver.Resolve(configuration, snapshot, syncService.LoadCurrentAssets());

        if (plan is null)
        {
            Console.WriteLine("No matching rule.");
            return 2;
        }

        Console.WriteLine($"Rule: {plan.RuleName}");
        Console.WriteLine("Modules:");
        foreach (var module in plan.ModulePaths)
        {
            Console.WriteLine($"- {module}");
        }

        Console.WriteLine("Environment:");
        foreach (var variable in plan.EnvironmentVariables)
        {
            Console.WriteLine($"- {variable.Name}={variable.Value}");
        }

        return 0;
    }

    private async Task<int> SmokeTestAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        var configuration = LoadConfiguration(options.ConfigPath);
        var syncService = CreateSyncService(options.CacheRoot);
        await syncService.SyncAsync(configuration, cancellationToken);

        var snapshot = BuildSnapshot(options);
        var resolver = new RuleResolver();
        var plan = resolver.Resolve(configuration, snapshot, syncService.LoadCurrentAssets());
        if (plan is null)
        {
            Console.WriteLine("No matching rule for smoke test.");
            return 2;
        }

        var executorPath = syncService.ResolveExecutorPath(configuration);
        using var currentProcess = Process.GetCurrentProcess();
        using var client = new NativeExecutorClient(executorPath);
        var result = client.Execute(new NativeExecuteRequest
        {
            ProcessId = (uint)currentProcess.Id,
            ProcessCreateTimeUtc100ns = (ulong)currentProcess.StartTime.ToUniversalTime().ToFileTimeUtc(),
            ExecutablePath = currentProcess.MainModule?.FileName ?? Environment.ProcessPath ?? "ModService.Tool",
            ModulePaths = plan.ModulePaths,
            EnvironmentVariables = plan.EnvironmentVariables
                .Select(item => new NativeEnvironmentVariable { Name = item.Name, Value = item.Value })
                .ToArray(),
            TimeoutMs = 1_000
        });

        Console.WriteLine($"Status: {result.Status}");
        if (!string.IsNullOrWhiteSpace(result.ErrorText))
        {
            Console.WriteLine(result.ErrorText);
        }

        return result.IsSuccess ? 0 : 1;
    }

    private int Cleanup(CommandOptions options)
    {
        var result = CreateSyncService(options.CacheRoot).CleanupStaleFiles();
        Console.WriteLine($"Stale files: {result.StaleFileCount}");
        Console.WriteLine($"Deleted: {result.Deleted}");
        foreach (var lockedFile in result.LockedFiles)
        {
            Console.WriteLine($"Locked: {lockedFile}");
        }

        return result.LockedFiles.Count == 0 ? 0 : 3;
    }

    private static ProcessSnapshot BuildSnapshot(CommandOptions options)
    {
        using var currentProcess = Process.GetCurrentProcess();
        var actualExecutablePath = currentProcess.MainModule?.FileName ?? Environment.ProcessPath ?? "ModService.Tool.exe";
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string?)entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        foreach (var value in options.EnvironmentOverrides)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Invalid --env value '{value}'. Expected NAME=VALUE.");
            }

            environment[value[..separator]] = value[(separator + 1)..];
        }

        return new ProcessSnapshot
        {
            ProcessId = currentProcess.Id,
            ProcessName = options.GetValue("--process-name") ?? Path.GetFileName(actualExecutablePath),
            ExePath = options.GetValue("--exe-path") ?? actualExecutablePath,
            ProcessCreateTimeUtc100ns = (ulong)currentProcess.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Environment = environment
        };
    }

    private static ModServiceConfiguration LoadConfiguration(string path)
    {
        var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement.TryGetProperty("ModService", out var section)
            ? section
            : document.RootElement;

        return root.Deserialize<ModServiceConfiguration>(JsonOptions) ?? new ModServiceConfiguration();
    }

    private static SourceSyncService CreateSyncService(string cacheRoot)
        => new(new StorageLayout(cacheRoot), new GhReleaseClient());

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  validate [--config path]");
        Console.WriteLine("  sync [--config path] [--cache path]");
        Console.WriteLine("  executor-path [--config path] [--cache path]");
        Console.WriteLine("  resolve [--config path] [--cache path] [--process-name name] [--exe-path path] [--env NAME=VALUE]");
        Console.WriteLine("  smoke-test [--config path] [--cache path] [--process-name name] [--exe-path path] [--env NAME=VALUE]");
        Console.WriteLine("  cleanup [--cache path]");
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

        public string ConfigPath => GetValue("--config") ?? Path.Combine(Environment.CurrentDirectory, "src", "ModService.Host", "modservice.json");

        public string CacheRoot => GetValue("--cache") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "cache");

        public IReadOnlyList<string> EnvironmentOverrides => GetValues("--env");

        public static CommandOptions Parse(string[] args)
        {
            var options = new CommandOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var key = args[index];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    throw new InvalidOperationException($"Missing value for {key}.");
                }

                options.Add(key, args[++index]);
            }

            return options;
        }

        public string? GetValue(string key)
            => _values.TryGetValue(key, out var values) ? values[^1] : null;

        public IReadOnlyList<string> GetValues(string key)
            => _values.TryGetValue(key, out var values) ? values : [];

        private void Add(string key, string value)
        {
            if (!_values.TryGetValue(key, out var values))
            {
                values = [];
                _values[key] = values;
            }

            values.Add(value);
        }
    }
}

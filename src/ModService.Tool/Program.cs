using System.Diagnostics;
using System.Text.Json;
using ModService.Core.Configuration;
using ModService.Core.Matching;
using ModService.Core.Processes;
using ModService.Core.Updates;
using ModService.GitHub.Auth;
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
                "status" => Status(options),
                "token-status" => TokenStatus(options),
                "set-token" => await SetTokenAsync(options, CancellationToken.None),
                "clear-token" => ClearToken(options),
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
        var syncService = CreateSyncService(options);
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
        var syncService = CreateSyncService(options);
        Console.WriteLine(syncService.ResolveExecutorPath(configuration));
        return 0;
    }

    private int Resolve(CommandOptions options)
    {
        var configuration = LoadConfiguration(options.ConfigPath);
        var syncService = CreateSyncService(options);
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

        Console.WriteLine("Applied environment:");
        foreach (var variable in plan.EnvironmentVariables)
        {
            Console.WriteLine($"- {variable.Name}={variable.Value}");
        }

        Console.WriteLine("Executor options:");
        foreach (var option in plan.ExecutorOptions)
        {
            Console.WriteLine($"- {option.Name}={option.Value}");
        }

        return 0;
    }

    private async Task<int> SmokeTestAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        var configuration = LoadConfiguration(options.ConfigPath);
        var syncService = CreateSyncService(options);
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
            ExecutorOptions = plan.ExecutorOptions
                .Select(item => new NativeExecutorOption { Name = item.Name, Value = item.Value })
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
        var result = CreateSyncService(options).CleanupStaleFiles();
        PrintCleanupResult(result);
        return result.LockedFiles.Count == 0 ? 0 : 3;
    }

    private int Status(CommandOptions options)
    {
        var tokenStore = CreateTokenStore(options);
        var syncService = CreateSyncService(options, tokenStore);
        var configuration = LoadConfiguration(options.ConfigPath);
        var cleanup = syncService.GetCleanupStatus();

        Console.WriteLine($"Token store: {tokenStore.FilePath}");
        Console.WriteLine($"Token present: {tokenStore.HasToken()}");
        Console.WriteLine($"Polling: {configuration.Polling.IntervalSeconds}s + up to {configuration.Polling.JitterSeconds}s jitter");
        Console.WriteLine($"Process monitoring: enabled={configuration.ProcessMonitoring.Enabled}, retry={configuration.ProcessMonitoring.ScanIntervalSeconds}s");

        foreach (var source in configuration.Sources)
        {
            var manifest = syncService.LoadManifest(source.Id);
            if (manifest is null)
            {
                Console.WriteLine($"Source {source.Id}: no manifest");
                continue;
            }

            Console.WriteLine($"Source {source.Id}: synced {manifest.SyncedAtUtc:O}, assets={manifest.CurrentAssets.Count}");
        }

        var executorManifest = syncService.LoadManifest(configuration.Executor.Source);
        var executorAsset = executorManifest?.CurrentAssets.FirstOrDefault(asset =>
            string.Equals(asset.AssetName, configuration.Executor.Asset, StringComparison.OrdinalIgnoreCase));
        if (executorAsset is not null)
        {
            Console.WriteLine($"Executor path: {executorAsset.FullPath}");
            Console.WriteLine($"Executor sha256: {executorAsset.Sha256}");
        }

        PrintCleanupResult(cleanup);
        return 0;
    }

    private int TokenStatus(CommandOptions options)
    {
        var tokenStore = CreateTokenStore(options);
        Console.WriteLine($"Token store: {tokenStore.FilePath}");
        Console.WriteLine($"Token present: {tokenStore.HasToken()}");
        return tokenStore.HasToken() ? 0 : 2;
    }

    private async Task<int> SetTokenAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        var token = await GetRequestedTokenAsync(options, cancellationToken);
        var tokenStore = CreateTokenStore(options);
        await tokenStore.SaveAsync(token, cancellationToken);
        Console.WriteLine($"Saved GitHub token to {tokenStore.FilePath}");
        return 0;
    }

    private int ClearToken(CommandOptions options)
    {
        var tokenStore = CreateTokenStore(options);
        tokenStore.Clear();
        Console.WriteLine($"Cleared GitHub token from {tokenStore.FilePath}");
        return 0;
    }

    private static void PrintCleanupResult(CleanupResult result)
    {
        Console.WriteLine($"Stale files: {result.StaleFileCount}");
        Console.WriteLine($"Deleted: {result.Deleted}");
        Console.WriteLine($"Locked stale files: {result.LockedFiles.Count}");
        foreach (var lockedFile in result.LockedFiles)
        {
            Console.WriteLine($"Locked: {lockedFile}");
        }
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

    private static SourceSyncService CreateSyncService(CommandOptions options)
        => CreateSyncService(options, CreateTokenStore(options));

    private static SourceSyncService CreateSyncService(CommandOptions options, GitHubTokenStore tokenStore)
        => new(new StorageLayout(options.CacheRoot), new GhReleaseClient(tokenStore));

    private static GitHubTokenStore CreateTokenStore(CommandOptions options)
        => new(options.TokenStorePath);

    private static async Task<string> GetRequestedTokenAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        var directValue = options.GetValue("--value");
        if (!string.IsNullOrWhiteSpace(directValue))
        {
            return directValue.Trim();
        }

        if (options.HasFlag("--from-gh"))
        {
            return (await RunGhAsync("auth token", cancellationToken)).Trim();
        }

        if (Console.IsInputRedirected)
        {
            return (await Console.In.ReadToEndAsync(cancellationToken)).Trim();
        }

        throw new InvalidOperationException("Provide --value TOKEN, --from-gh, or pipe the token through stdin.");
    }

    private static async Task<string> RunGhAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start gh.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"gh {arguments} failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr}");
        }

        return stdout;
    }

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
        Console.WriteLine("  sync [--config path] [--cache path] [--token-store path]");
        Console.WriteLine("  status [--config path] [--cache path] [--token-store path]");
        Console.WriteLine("  token-status [--token-store path]");
        Console.WriteLine("  set-token [--token-store path] [--value token | --from-gh]");
        Console.WriteLine("  clear-token [--token-store path]");
        Console.WriteLine("  executor-path [--config path] [--cache path] [--token-store path]");
        Console.WriteLine("  resolve [--config path] [--cache path] [--process-name name] [--exe-path path] [--env NAME=VALUE]");
        Console.WriteLine("  smoke-test [--config path] [--cache path] [--token-store path] [--process-name name] [--exe-path path] [--env NAME=VALUE]");
        Console.WriteLine("  cleanup [--cache path]");
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

        public string ConfigPath => GetValue("--config") ?? Path.Combine(Environment.CurrentDirectory, "src", "ModService.Host", "modservice.json");

        public string CacheRoot => GetValue("--cache") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "cache");

        public string TokenStorePath => GetValue("--token-store")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ModService", "secrets", "github-token.bin");

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

                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options.Add(key, args[++index]);
                }
                else
                {
                    options.Add(key, bool.TrueString);
                }
            }

            return options;
        }

        public bool HasFlag(string key)
            => _values.ContainsKey(key);

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

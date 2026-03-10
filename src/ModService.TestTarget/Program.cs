using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModService.Core.Configuration;
using ModService.Core.Execution;
using ModService.Core.Matching;
using ModService.Core.Processes;
using ModService.Core.Updates;
using ModService.GitHub.Auth;
using ModService.GitHub.Gh;
using ModService.Interop.Native;

return await new TestTargetApplication().RunAsync(args);

internal sealed class TestTargetApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<int> RunAsync(string[] args)
    {
        var options = CommandOptions.Parse(args);
        if (options.HasFlag("--help") || options.HasFlag("-h"))
        {
            PrintUsage();
            return 0;
        }

        try
        {
            var configuration = LoadConfiguration(options.ConfigPath);
            var errors = ConfigurationValidator.Validate(configuration);
            if (errors.Count > 0)
            {
                Console.Error.WriteLine("Configuration is invalid:");
                foreach (var error in errors)
                {
                    Console.Error.WriteLine($"- {error}");
                }

                return 1;
            }

            var tokenStore = new GitHubTokenStore(options.TokenStorePath);
            var syncService = new SourceSyncService(
                new StorageLayout(options.CacheRoot),
                new GhReleaseClient(tokenStore));

            if (options.SyncFirst)
            {
                var syncResults = await syncService.SyncAsync(configuration, CancellationToken.None);
                foreach (var syncResult in syncResults)
                {
                    Console.WriteLine($"{syncResult.Manifest.SourceId}: synced {syncResult.Manifest.CurrentAssets.Count} assets, downloaded [{string.Join(", ", syncResult.DownloadedAssets)}]");
                }
            }

            using var process = Process.GetCurrentProcess();
            var marker = options.Marker ?? $"marker-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
            var outputPath = PrepareOutputPath(options.OutputPath);

            Console.WriteLine($"PID: {process.Id}");
            Console.WriteLine($"Process: {Path.GetFileName(process.MainModule?.FileName ?? Environment.ProcessPath ?? "ModService.TestTarget.exe")}");
            Console.WriteLine($"Output: {outputPath}");
            Console.WriteLine($"Marker: {marker}");

            if (options.ObservationDelaySeconds > 0)
            {
                Console.WriteLine($"Waiting {options.ObservationDelaySeconds}s before self-load so the service can observe this process.");
                await Task.Delay(TimeSpan.FromSeconds(options.ObservationDelaySeconds), CancellationToken.None);
            }

            var snapshot = BuildSnapshot(process, options);
            var resolver = new RuleResolver();
            var plan = resolver.Resolve(configuration, snapshot, syncService.LoadCurrentAssets());
            if (plan is null)
            {
                Console.Error.WriteLine("No matching rule for this process.");
                return 2;
            }

            var executorPath = syncService.ResolveExecutorPath(configuration);
            var environmentVariables = BuildEnvironmentVariables(plan, outputPath, marker, options);

            Console.WriteLine($"Rule: {plan.RuleName}");
            Console.WriteLine($"Executor: {executorPath}");
            Console.WriteLine("Modules:");
            foreach (var modulePath in plan.ModulePaths)
            {
                Console.WriteLine($"- {modulePath}");
            }

            Console.WriteLine("Effective environment:");
            foreach (var environmentVariable in environmentVariables)
            {
                Console.WriteLine($"- {environmentVariable.Name}={environmentVariable.Value}");
            }

            using var client = new NativeExecutorClient(executorPath);
            var result = client.Execute(new NativeExecuteRequest
            {
                ProcessId = (uint)process.Id,
                ProcessCreateTimeUtc100ns = (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(),
                ExecutablePath = process.MainModule?.FileName ?? Environment.ProcessPath ?? "ModService.TestTarget.exe",
                ModulePaths = plan.ModulePaths,
                EnvironmentVariables = environmentVariables
                    .Select(item => new NativeEnvironmentVariable { Name = item.Name, Value = item.Value })
                    .ToArray(),
                TimeoutMs = (uint)options.TimeoutMs
            });

            Console.WriteLine($"Executor status: {result.Status}");
            if (!string.IsNullOrWhiteSpace(result.ErrorText))
            {
                Console.WriteLine(result.ErrorText);
            }

            if (!result.IsSuccess)
            {
                return 3;
            }

            var verified = await WaitForMarkerAsync(outputPath, marker, options.VerifyTimeoutSeconds);
            Console.WriteLine($"Marker verified: {verified}");
            if (!verified)
            {
                return 4;
            }

            if (options.LingerSeconds > 0)
            {
                Console.WriteLine($"Lingering for {options.LingerSeconds}s before exit.");
                await Task.Delay(TimeSpan.FromSeconds(options.LingerSeconds), CancellationToken.None);
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static ModServiceConfiguration LoadConfiguration(string path)
    {
        var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement.TryGetProperty("ModService", out var section)
            ? section
            : document.RootElement;

        return root.Deserialize<ModServiceConfiguration>(JsonOptions) ?? new ModServiceConfiguration();
    }

    private static string PrepareOutputPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Output path must have a parent directory."));
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        return fullPath;
    }

    private static ProcessSnapshot BuildSnapshot(Process process, CommandOptions options)
    {
        var executablePath = process.MainModule?.FileName ?? Environment.ProcessPath ?? "ModService.TestTarget.exe";
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(
                entry => (string)entry.Key,
                entry => (string?)entry.Value ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);

        foreach (var overrideValue in options.EnvironmentOverrides)
        {
            var separator = overrideValue.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Invalid --env value '{overrideValue}'. Expected NAME=VALUE.");
            }

            environment[overrideValue[..separator]] = overrideValue[(separator + 1)..];
        }

        return new ProcessSnapshot
        {
            ProcessId = process.Id,
            ProcessName = Path.GetFileName(executablePath),
            ExePath = executablePath,
            ProcessCreateTimeUtc100ns = (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Environment = environment
        };
    }

    private static IReadOnlyList<ResolvedEnvironmentVariable> BuildEnvironmentVariables(
        ResolvedExecutionPlan plan,
        string outputPath,
        string marker,
        CommandOptions options)
    {
        var effective = new List<ResolvedEnvironmentVariable>(plan.EnvironmentVariables);
        Upsert(effective, options.SampleOutputVariableName, outputPath);
        Upsert(effective, options.SampleMarkerVariableName, marker);

        foreach (var overrideValue in options.ForwardEnvironmentOverrides)
        {
            var separator = overrideValue.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException($"Invalid --forward-env value '{overrideValue}'. Expected NAME=VALUE.");
            }

            Upsert(effective, overrideValue[..separator], overrideValue[(separator + 1)..]);
        }

        return effective;
    }

    private static void Upsert(List<ResolvedEnvironmentVariable> environment, string name, string value)
    {
        for (var index = 0; index < environment.Count; index++)
        {
            if (!string.Equals(environment[index].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            environment[index] = new ResolvedEnvironmentVariable
            {
                Name = name,
                Value = value
            };
            return;
        }

        environment.Add(new ResolvedEnvironmentVariable
        {
            Name = name,
            Value = value
        });
    }

    private static async Task<bool> WaitForMarkerAsync(string outputPath, string marker, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
        while (DateTime.UtcNow <= deadline)
        {
            if (File.Exists(outputPath))
            {
                var content = Encoding.Unicode.GetString(await File.ReadAllBytesAsync(outputPath));
                var lines = content
                    .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Any(line => string.Equals(line, marker, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            await Task.Delay(200);
        }

        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  ModService.TestTarget.exe [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --config PATH                 Config file to load.");
        Console.WriteLine("  --cache PATH                  Cache root that contains manifests and immutable DLL storage.");
        Console.WriteLine("  --token-store PATH            DPAPI token store path used only when --sync is enabled.");
        Console.WriteLine("  --sync                        Sync sources before resolving and loading.");
        Console.WriteLine("  --env NAME=VALUE              Add or override a value for rule matching and passEnvironment resolution.");
        Console.WriteLine("  --forward-env NAME=VALUE      Append a variable to the executor request after rule resolution.");
        Console.WriteLine("  --output PATH                 Marker output file written by SampleModule.dll.");
        Console.WriteLine("  --marker VALUE                Marker text to verify in the output file.");
        Console.WriteLine("  --sample-output-env NAME      Variable name used for the output file. Default MODSERVICE_SAMPLE_OUTPUT.");
        Console.WriteLine("  --sample-marker-env NAME      Variable name used for the marker text. Default MODSERVICE_SAMPLE_MARKER.");
        Console.WriteLine("  --observation-delay-seconds N Wait before calling the executor so the service can observe this process.");
        Console.WriteLine("  --verify-timeout-seconds N    Wait for the sample module marker after execution.");
        Console.WriteLine("  --linger-seconds N            Keep the process alive after verification.");
        Console.WriteLine("  --timeout-ms N                Native executor timeout value.");
    }

    private sealed class CommandOptions
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

        public string ConfigPath => GetValue("--config") ?? Path.Combine(Environment.CurrentDirectory, "src", "ModService.Host", "modservice.json");

        public string CacheRoot => GetValue("--cache") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "cache");

        public string TokenStorePath => GetValue("--token-store")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ModService", "secrets", "github-token.bin");

        public bool SyncFirst => HasFlag("--sync");

        public IReadOnlyList<string> EnvironmentOverrides => GetValues("--env");

        public IReadOnlyList<string> ForwardEnvironmentOverrides => GetValues("--forward-env");

        public string OutputPath => GetValue("--output")
            ?? Path.Combine(Path.GetTempPath(), "ModService.TestTarget", $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.txt");

        public string? Marker => GetValue("--marker");

        public string SampleOutputVariableName => GetValue("--sample-output-env") ?? "MODSERVICE_SAMPLE_OUTPUT";

        public string SampleMarkerVariableName => GetValue("--sample-marker-env") ?? "MODSERVICE_SAMPLE_MARKER";

        public int ObservationDelaySeconds => ParseInt("--observation-delay-seconds", 5);

        public int VerifyTimeoutSeconds => ParseInt("--verify-timeout-seconds", 5);

        public int LingerSeconds => ParseInt("--linger-seconds", 10);

        public int TimeoutMs => ParseInt("--timeout-ms", 1_000);

        public static CommandOptions Parse(string[] args)
        {
            var options = new CommandOptions();
            for (var index = 0; index < args.Length; index++)
            {
                var key = args[index];
                if (!key.StartsWith("-", StringComparison.Ordinal))
                {
                    continue;
                }

                if (index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
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

        private string? GetValue(string key)
            => _values.TryGetValue(key, out var values) ? values[^1] : null;

        private IReadOnlyList<string> GetValues(string key)
            => _values.TryGetValue(key, out var values) ? values : [];

        private int ParseInt(string key, int fallback)
        {
            var raw = GetValue(key);
            return raw is not null && int.TryParse(raw, out var parsed) ? parsed : fallback;
        }

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

using ModService.Core.Configuration;
using ModService.Core.Execution;
using ModService.Core.Matching;
using ModService.Core.Processes;

namespace ModService.Tests;

public sealed class RuleResolverTests
{
    private readonly RuleResolver _resolver = new();

    [Fact]
    public void Resolve_UsesBindingOrder_AndAllowsSameBasenameAcrossSources()
    {
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration
            {
                Source = "core",
                Asset = "injector.dll",
                Options =
                [
                    new ExecutorOptionConfiguration { Name = "mode", Value = "safe" },
                    new ExecutorOptionConfiguration { Name = "profile", Value = "global" }
                ]
            },
            Sources =
            [
                new SourceConfiguration { Id = "core", Repo = "owner/core", Tag = "stable" },
                new SourceConfiguration { Id = "extras", Repo = "owner/extras", Tag = "stable" }
            ],
            Rules =
            [
                new RuleConfiguration
                {
                    Name = "main",
                    Process = "game*.exe",
                    Env = [new EnvMatchCondition { Name = "MOD_PROFILE", Op = "equals", Value = "main" }],
                    PassEnvironment = ["MOD_PROFILE", "MISSING", "MOD_PROFILE"],
                    ExecutorOptions =
                    [
                        new ExecutorOptionConfiguration { Name = "profile", Value = "main" },
                        new ExecutorOptionConfiguration { Name = "log", Value = "verbose" }
                    ],
                    Bindings =
                    [
                        new BindingConfiguration { Source = "core", Include = ["*.dll"] },
                        new BindingConfiguration { Source = "extras", Include = ["*.dll"] }
                    ]
                }
            ]
        };

        var snapshot = new ProcessSnapshot
        {
            ProcessId = 77,
            ProcessName = "game-test.exe",
            ExePath = @"C:\Games\game-test.exe",
            ProcessCreateTimeUtc100ns = 10,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["MOD_PROFILE"] = "main"
            }
        };

        var assets = new List<SourceAsset>
        {
            new() { SourceId = "core", AssetName = "alpha.dll", FullPath = @"C:\cache\core\alpha.dll" },
            new() { SourceId = "extras", AssetName = "alpha.dll", FullPath = @"C:\cache\extras\alpha.dll" },
            new() { SourceId = "core", AssetName = "alpha.dll", FullPath = @"C:\cache\core\alpha.dll" }
        };

        var plan = _resolver.Resolve(configuration, snapshot, assets);

        Assert.NotNull(plan);
        Assert.Equal("main", plan.RuleName);
        Assert.Equal(
            [@"C:\cache\core\alpha.dll", @"C:\cache\extras\alpha.dll"],
            plan.ModulePaths);
        Assert.Collection(
            plan.EnvironmentVariables,
            variable =>
            {
                Assert.Equal("MOD_PROFILE", variable.Name);
                Assert.Equal("main", variable.Value);
            });
        Assert.Collection(
            plan.ExecutorOptions,
            option =>
            {
                Assert.Equal("mode", option.Name);
                Assert.Equal("safe", option.Value);
            },
            option =>
            {
                Assert.Equal("profile", option.Name);
                Assert.Equal("main", option.Value);
            },
            option =>
            {
                Assert.Equal("log", option.Name);
                Assert.Equal("verbose", option.Value);
            });
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenExcluded()
    {
        var configuration = new ModServiceConfiguration
        {
            Executor = new ExecutorConfiguration { Source = "core", Asset = "injector.dll" },
            Sources = [new SourceConfiguration { Id = "core", Repo = "owner/core", Tag = "stable" }],
            Excludes = [new MatchCriteria { Process = "launcher*.exe" }],
            Rules =
            [
                new RuleConfiguration
                {
                    Process = "*.exe",
                    Bindings = [new BindingConfiguration { Source = "core" }]
                }
            ]
        };

        var snapshot = new ProcessSnapshot
        {
            ProcessId = 1,
            ProcessName = "launcher-test.exe",
            ExePath = @"C:\Games\launcher-test.exe",
            ProcessCreateTimeUtc100ns = 0,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };

        var plan = _resolver.Resolve(configuration, snapshot, []);

        Assert.Null(plan);
    }
}

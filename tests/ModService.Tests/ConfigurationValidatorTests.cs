using ModService.Core.Configuration;

namespace ModService.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void Validate_RejectsDuplicateArchiveAssets_AndEmptyPatterns()
    {
        var configuration = new ModServiceConfiguration
        {
            Http = new HttpApiConfiguration
            {
                ListenUrl = "http://127.0.0.1:5047"
            },
            Executor = new ExecutorConfiguration
            {
                Source = "mods",
                Asset = "injector.dll",
                Options =
                [
                    new ExecutorOptionConfiguration { Name = "mode", Value = "safe" },
                    new ExecutorOptionConfiguration { Name = "mode", Value = "duplicate" }
                ]
            },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "mods",
                    Repo = "owner/repo",
                    Tag = "stable",
                    Include = [""],
                    Archives =
                    [
                        new ArchiveConfiguration { Asset = "bundle.zip", Include = ["native/*.dll"] },
                        new ArchiveConfiguration { Asset = "bundle.zip", Exclude = [""] }
                    ]
                }
            ]
        };

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("empty pattern", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("duplicate archive asset 'bundle.zip'", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("duplicate option 'mode'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsInvalidHttpListenUrl()
    {
        var configuration = new ModServiceConfiguration
        {
            Http = new HttpApiConfiguration
            {
                ListenUrl = "https://localhost/api"
            },
            Executor = new ExecutorConfiguration
            {
                Source = "mods",
                Asset = "injector.dll"
            },
            Sources =
            [
                new SourceConfiguration
                {
                    Id = "mods",
                    Repo = "owner/repo",
                    Tag = "stable"
                }
            ]
        };

        var errors = ConfigurationValidator.Validate(configuration);

        Assert.Contains(errors, error => error.Contains("http scheme", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("must not include a path", StringComparison.OrdinalIgnoreCase));
    }
}

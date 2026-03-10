using ModService.Core.Configuration;

namespace ModService.Tests;

public sealed class ConfigurationValidatorTests
{
    [Fact]
    public void Validate_RejectsDuplicateArchiveAssets_AndEmptyPatterns()
    {
        var configuration = new ModServiceConfiguration
        {
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
}

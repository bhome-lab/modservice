using Microsoft.Extensions.Configuration;
using ModService.Host;
using Serilog;

namespace ModService.Tests;

public sealed class SerilogConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModServiceSerilogTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MaximumRetainedBytes_StaysWithinTenMegabytes()
    {
        Assert.True(SerilogConfiguration.MaximumRetainedBytes <= 10_000_000);
    }

    [Fact]
    public void Configure_WritesLogFile()
    {
        Directory.CreateDirectory(_root);
        var loggerConfiguration = new LoggerConfiguration();

        SerilogConfiguration.Configure(
            loggerConfiguration,
            new ConfigurationBuilder().Build(),
            new SerilogConfiguration.FileLoggingOptions(_root, 2_000_000, 4));

        using (var logger = loggerConfiguration.CreateLogger())
        {
            logger.Information("File logging test message.");
        }

        var logFile = Path.Combine(_root, "modservice.log");
        Assert.True(File.Exists(logFile));
        Assert.Contains("File logging test message.", File.ReadAllText(logFile));
    }

    [Fact]
    public void Configure_RetainsBoundedLogFileCount_WhenRolling()
    {
        Directory.CreateDirectory(_root);
        var loggerConfiguration = new LoggerConfiguration();

        SerilogConfiguration.Configure(
            loggerConfiguration,
            new ConfigurationBuilder().Build(),
            new SerilogConfiguration.FileLoggingOptions(_root, 512, 1));

        using (var logger = loggerConfiguration.CreateLogger())
        {
            var payload = new string('x', 256);

            for (var index = 0; index < 100; index++)
            {
                logger.Information("Message {Index} {Payload}", index, payload);
            }
        }

        var logFiles = Directory.EnumerateFiles(_root, "modservice*.log", SearchOption.TopDirectoryOnly).ToArray();
        Assert.InRange(logFiles.Length, 1, 2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

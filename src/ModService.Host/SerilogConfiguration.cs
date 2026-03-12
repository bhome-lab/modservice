using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;

namespace ModService.Host;

internal static class SerilogConfiguration
{
    public const long FileSizeLimitBytes = 2_000_000;
    public const int RetainedFileCountLimit = 4;
    public const long MaximumRetainedBytes = FileSizeLimitBytes * (RetainedFileCountLimit + 1L);

    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}";

    public static ILogger CreateBootstrapLogger(string logsDirectory)
    {
        var loggerConfiguration = new LoggerConfiguration();
        ConfigureDefaults(loggerConfiguration);
        ConfigureSinks(loggerConfiguration, new FileLoggingOptions(logsDirectory, FileSizeLimitBytes, RetainedFileCountLimit));
        return loggerConfiguration.CreateBootstrapLogger();
    }

    public static void Configure(LoggerConfiguration loggerConfiguration, IConfiguration configuration, string logsDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Configure(loggerConfiguration, configuration, new FileLoggingOptions(logsDirectory, FileSizeLimitBytes, RetainedFileCountLimit));
    }

    internal static void Configure(
        LoggerConfiguration loggerConfiguration,
        IConfiguration configuration,
        FileLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        ConfigureDefaults(loggerConfiguration);
        loggerConfiguration.ReadFrom.Configuration(configuration);
        ConfigureSinks(loggerConfiguration, options);
    }

    private static void ConfigureDefaults(LoggerConfiguration loggerConfiguration)
    {
        loggerConfiguration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext();
    }

    private static void ConfigureSinks(LoggerConfiguration loggerConfiguration, FileLoggingOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DirectoryPath);

        Directory.CreateDirectory(options.DirectoryPath);

        loggerConfiguration
            .WriteTo.Console(outputTemplate: OutputTemplate)
            .WriteTo.File(
                path: Path.Combine(options.DirectoryPath, "modservice.log"),
                outputTemplate: OutputTemplate,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: options.RetainedFileCountLimit,
                shared: false);
    }

    internal sealed record FileLoggingOptions(
        string DirectoryPath,
        long FileSizeLimitBytes,
        int RetainedFileCountLimit);
}

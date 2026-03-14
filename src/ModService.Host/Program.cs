using System.Threading;
using System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModService.Core.Configuration;
using ModService.Core.Matching;
using ModService.Core.Updates;
using ModService.GitHub.Auth;
using ModService.GitHub.Gh;
using Serilog;

namespace ModService.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var paths = new ApplicationPaths();
        Log.Logger = SerilogConfiguration.CreateBootstrapLogger(paths.LogsDirectory);
        StandardExceptionReporter.Install();

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\ModService.Tray", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "ModService is already running.",
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            using var host = BuildHost(args, paths);
            host.StartAsync().GetAwaiter().GetResult();

            try
            {
                using var context = ActivatorUtilities.CreateInstance<TrayApplicationContext>(host.Services);
                Application.Run(context);
            }
            finally
            {
                host.StopAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            StandardExceptionReporter.Report("Fatal startup exception", exception);
            MessageBox.Show(
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static WebApplication BuildHost(string[] args, ApplicationPaths paths)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Configuration.AddJsonFile("modservice.json", optional: true, reloadOnChange: true);
        builder.Host.UseSerilog((context, _, loggerConfiguration) =>
        {
            SerilogConfiguration.Configure(loggerConfiguration, context.Configuration, paths.LogsDirectory);
        });

        builder.Services
            .AddOptions<ModServiceConfiguration>()
            .Bind(builder.Configuration.GetSection("ModService"));

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<ApplicationPaths>();
            return new StorageLayout(Path.Combine(paths.ProgramDataRoot, "cache"));
        });
        builder.Services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<ApplicationPaths>();
            return new GitHubTokenStore(Path.Combine(paths.ProgramDataRoot, "secrets", "github-token.bin"));
        });
        builder.Services.AddSingleton<IGitHubCli, GhCli>();
        builder.Services.AddSingleton<GitHubTokenManager>();
        builder.Services.AddSingleton<IGitHubReleaseClient, GhReleaseClient>();
        builder.Services.AddSingleton<SourceSyncService>();
        builder.Services.AddSingleton<RuleResolver>();
        builder.Services.AddSingleton<EffectiveConfigurationStore>();
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<StartupTaskService>();
        builder.Services.AddSingleton<TrayPreferencesStore>();
        builder.Services.AddSingleton<NotificationRequestQueue>();
        builder.Services.AddSingleton<IProcessEventSource, WmiProcessEventSource>();
        builder.Services.AddSingleton<ModServiceWorker>();
        builder.Services.AddSingleton<IRefreshController>(sp => sp.GetRequiredService<ModServiceWorker>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ModServiceWorker>());
        builder.Services.AddHostedService<ProcessWatchWorker>();

        var app = builder.Build();
        app.Urls.Clear();
        app.Urls.Add(ResolveHttpListenUrl(app.Configuration));
        app.MapModServiceApi();
        return app;
    }

    private static string ResolveHttpListenUrl(IConfiguration configuration)
    {
        var configuredValue = configuration["ModService:Http:ListenUrl"];
        return IsValidListenUrl(configuredValue)
            ? configuredValue!
            : new HttpApiConfiguration().ListenUrl;
    }

    private static bool IsValidListenUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.AbsolutePath) && !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return false;
        }

        return uri.Port > 0;
    }
}

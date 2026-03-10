using System.Threading;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModService.Core.Configuration;
using ModService.Core.Matching;
using ModService.Core.Updates;
using ModService.GitHub.Auth;
using ModService.GitHub.Gh;

namespace ModService.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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
            using var host = BuildHost(args);
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
            MessageBox.Show(
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static IHost BuildHost(string[] args)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Configuration.AddJsonFile("modservice.json", optional: true, reloadOnChange: true);

        builder.Services
            .AddOptions<ModServiceConfiguration>()
            .Bind(builder.Configuration.GetSection("ModService"));

        builder.Services.AddSingleton<ApplicationPaths>();
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
        builder.Services.AddSingleton<IGitHubReleaseClient, GhReleaseClient>();
        builder.Services.AddSingleton<SourceSyncService>();
        builder.Services.AddSingleton<RuleResolver>();
        builder.Services.AddSingleton<EffectiveConfigurationStore>();
        builder.Services.AddSingleton<RuntimeStateStore>();
        builder.Services.AddSingleton<StartupTaskService>();
        builder.Services.AddSingleton<TrayPreferencesStore>();
        builder.Services.AddSingleton<NotificationRequestQueue>();
        builder.Services.AddSingleton<ModServiceWorker>();
        builder.Services.AddSingleton<IRefreshController>(sp => sp.GetRequiredService<ModServiceWorker>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ModServiceWorker>());
        builder.Services.AddHostedService<ProcessWatchWorker>();

        return builder.Build();
    }
}

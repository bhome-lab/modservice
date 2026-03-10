using ModService.Core.Configuration;
using ModService.Core.Matching;
using ModService.Core.Updates;
using ModService.GitHub.Auth;
using ModService.GitHub.Gh;
using ModService.Host;
using Microsoft.Extensions.Logging.EventLog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("modservice.json", optional: true, reloadOnChange: true);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Mod Service";
});

builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Information);

builder.Services
    .AddOptions<ModServiceConfiguration>()
    .Bind(builder.Configuration.GetSection("ModService"));

var programDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ModService");
builder.Services.AddSingleton(new StorageLayout(
    Path.Combine(programDataRoot, "cache")));
builder.Services.AddSingleton(new GitHubTokenStore(Path.Combine(programDataRoot, "secrets", "github-token.bin")));
builder.Services.AddSingleton<IGitHubReleaseClient, GhReleaseClient>();
builder.Services.AddSingleton<SourceSyncService>();
builder.Services.AddSingleton<RuleResolver>();
builder.Services.AddHostedService<ModServiceWorker>();
builder.Services.AddHostedService<ProcessWatchWorker>();

var host = builder.Build();
host.Run();

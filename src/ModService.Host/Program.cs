using ModService.Core.Configuration;
using ModService.Core.Updates;
using ModService.GitHub.Gh;
using ModService.Host;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("modservice.json", optional: true, reloadOnChange: true);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Mod Service";
});

builder.Services
    .AddOptions<ModServiceConfiguration>()
    .Bind(builder.Configuration.GetSection("ModService"));

builder.Services.AddSingleton(new StorageLayout(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ModService", "cache")));
builder.Services.AddSingleton<IGitHubReleaseClient, GhReleaseClient>();
builder.Services.AddSingleton<SourceSyncService>();
builder.Services.AddHostedService<ModServiceWorker>();

var host = builder.Build();
host.Run();

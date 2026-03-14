using System.Management;
using Microsoft.Extensions.Logging;

namespace ModService.Host;

public sealed class WmiProcessEventSource(ILogger<WmiProcessEventSource> logger) : IProcessEventSource
{
    public IDisposable Start(Action<ProcessEventNotification> onEvent)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        var startWatcher = CreateWatcher(
            query: "SELECT * FROM Win32_ProcessStartTrace",
            eventKind: ProcessEventKind.Started,
            onEvent: onEvent);
        var stopWatcher = CreateWatcher(
            query: "SELECT * FROM Win32_ProcessStopTrace",
            eventKind: ProcessEventKind.Exited,
            onEvent: onEvent);

        try
        {
            startWatcher.Start();
            stopWatcher.Start();
            return new WatcherSubscription(startWatcher, stopWatcher);
        }
        catch
        {
            startWatcher.Dispose();
            stopWatcher.Dispose();
            throw;
        }
    }

    private ManagementEventWatcher CreateWatcher(
        string query,
        ProcessEventKind eventKind,
        Action<ProcessEventNotification> onEvent)
    {
        var watcher = new ManagementEventWatcher(new WqlEventQuery(query));
        watcher.EventArrived += (_, args) =>
        {
            if (!TryGetProcessId(args, out var processId))
            {
                logger.LogDebug("Ignored WMI process event because the payload did not contain a usable ProcessID.");
                return;
            }

            onEvent(new ProcessEventNotification(
                eventKind,
                processId,
                TryGetStringProperty(args, "ProcessName")));
        };
        watcher.Stopped += (_, args) =>
        {
            if (args.Status != ManagementStatus.NoError)
            {
                logger.LogWarning(
                    "WMI process event watcher stopped unexpectedly for {EventKind}: status={Status}.",
                    eventKind,
                    args.Status);
            }
        };

        return watcher;
    }

    private static bool TryGetProcessId(EventArrivedEventArgs args, out int processId)
    {
        processId = 0;
        var value = args.NewEvent.Properties["ProcessID"]?.Value;
        if (value is null)
        {
            return false;
        }

        try
        {
            processId = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            return processId > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetStringProperty(EventArrivedEventArgs args, string propertyName)
        => args.NewEvent.Properties[propertyName]?.Value as string;

    private sealed class WatcherSubscription(params ManagementEventWatcher[] watchers) : IDisposable
    {
        private readonly ManagementEventWatcher[] _watchers = watchers;

        public void Dispose()
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.Stop();
                }
                catch
                {
                }

                watcher.Dispose();
            }
        }
    }
}

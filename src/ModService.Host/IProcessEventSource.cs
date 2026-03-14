namespace ModService.Host;

public interface IProcessEventSource
{
    IDisposable Start(Action<ProcessEventNotification> onEvent);
}

public enum ProcessEventKind
{
    Started,
    Exited
}

public readonly record struct ProcessEventNotification(
    ProcessEventKind Kind,
    int ProcessId,
    string? ProcessName);

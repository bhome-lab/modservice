using System.Collections.Concurrent;
using System.Windows.Forms;

namespace ModService.Host;

public sealed class NotificationRequestQueue
{
    private readonly ConcurrentQueue<NotificationRequest> _queue = new();

    public void Enqueue(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, int timeoutMs = 5_000)
        => _queue.Enqueue(new NotificationRequest(title, text, icon, timeoutMs));

    public bool TryDequeue(out NotificationRequest request)
        => _queue.TryDequeue(out request!);
}

public sealed record NotificationRequest(string Title, string Text, ToolTipIcon Icon, int TimeoutMs);

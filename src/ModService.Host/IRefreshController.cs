namespace ModService.Host;

public interface IRefreshController
{
    void QueueRefresh(string reason);
}

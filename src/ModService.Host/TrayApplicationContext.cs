using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace ModService.Host;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly IServiceProvider _serviceProvider;
    private readonly RuntimeStateStore _runtimeState;
    private readonly IRefreshController _refreshController;
    private readonly StartupTaskService _startupTaskService;
    private readonly TrayPreferencesStore _preferencesStore;
    private readonly NotificationRequestQueue _notificationQueue;
    private readonly ApplicationPaths _paths;
    private readonly StatusForm _statusForm;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _forceRefreshItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _notificationsItem;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private bool _updatingMenuState;

    public TrayApplicationContext(
        IServiceProvider serviceProvider,
        RuntimeStateStore runtimeState,
        IRefreshController refreshController,
        StartupTaskService startupTaskService,
        TrayPreferencesStore preferencesStore,
        NotificationRequestQueue notificationQueue,
        ApplicationPaths paths)
    {
        _serviceProvider = serviceProvider;
        _runtimeState = runtimeState;
        _refreshController = refreshController;
        _startupTaskService = startupTaskService;
        _preferencesStore = preferencesStore;
        _notificationQueue = notificationQueue;
        _paths = paths;
        _statusForm = ActivatorUtilities.CreateInstance<StatusForm>(_serviceProvider);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => _statusForm.ShowWindow());
        menu.Items.Add(new ToolStripSeparator());

        _forceRefreshItem = new ToolStripMenuItem("Force Refresh");
        _forceRefreshItem.Click += (_, _) => _refreshController.QueueRefresh("tray-menu");
        menu.Items.Add(_forceRefreshItem);

        _startupItem = new ToolStripMenuItem("Run At Login As Admin")
        {
            CheckOnClick = true
        };
        _startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(_startupItem);

        _notificationsItem = new ToolStripMenuItem("Process Notifications")
        {
            CheckOnClick = true
        };
        _notificationsItem.Click += (_, _) => ToggleNotifications();
        menu.Items.Add(_notificationsItem);

        menu.Items.Add("Open Config", null, (_, _) => OpenConfig());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Shield,
            Text = "ModService: Starting",
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => _statusForm.ShowWindow();

        _uiTimer = new System.Windows.Forms.Timer
        {
            Interval = 1_000
        };
        _uiTimer.Tick += (_, _) => UpdateUiState();
        _uiTimer.Start();

        UpdateUiState();
    }

    protected override void ExitThreadCore()
    {
        _uiTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _statusForm.Dispose();
        base.ExitThreadCore();
    }

    private void UpdateUiState()
    {
        var runtime = _runtimeState.GetSnapshot();

        _updatingMenuState = true;
        try
        {
            _forceRefreshItem.Text = runtime.QueuedRefreshCount == 0
                ? "Force Refresh"
                : $"Force Refresh ({runtime.QueuedRefreshCount} queued)";

            _startupItem.Checked = SafeIsStartupEnabled();
            _notificationsItem.Checked = _preferencesStore.AreProcessNotificationsEnabled();
        }
        finally
        {
            _updatingMenuState = false;
        }

        _notifyIcon.Text = BuildNotifyText(runtime);
        if (_notificationQueue.TryDequeue(out var request))
        {
            _notifyIcon.ShowBalloonTip(request.TimeoutMs, request.Title, request.Text, request.Icon);
        }
    }

    private void ToggleStartup()
    {
        if (_updatingMenuState)
        {
            return;
        }

        try
        {
            _startupTaskService.SetEnabled(_startupItem.Checked);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        UpdateUiState();
    }

    private void ToggleNotifications()
    {
        if (_updatingMenuState)
        {
            return;
        }

        try
        {
            _preferencesStore.SetProcessNotificationsEnabled(_notificationsItem.Checked);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        UpdateUiState();
    }

    private void OpenConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{_paths.ConfigPath}\"",
                UseShellExecute = true,
                WorkingDirectory = _paths.BaseDirectory
            });
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

    private void ExitApplication()
        => ExitThread();

    private bool SafeIsStartupEnabled()
    {
        try
        {
            return _startupTaskService.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    private static string BuildNotifyText(RuntimeSnapshot snapshot)
    {
        var text = snapshot.RefreshInProgress
            ? $"ModService: Refreshing ({snapshot.LastRefreshReason})"
            : snapshot.QueuedRefreshCount > 0
                ? $"ModService: {snapshot.QueuedRefreshCount} queued"
                : "ModService: Running";

        return text.Length <= 63 ? text : text[..63];
    }
}

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
    private readonly SelfUpdateService _selfUpdateService;
    private readonly ApplicationPaths _paths;
    private readonly StatusForm _statusForm;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _forceRefreshItem;
    private readonly ToolStripMenuItem _checkForUpdatesItem;
    private readonly ToolStripMenuItem _installUpdateItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ToolStripMenuItem _notificationsItem;
    private readonly System.Windows.Forms.Timer _uiTimer;
    private DateTimeOffset? _scheduledUpdateRestartAtUtc;
    private bool _updatingMenuState;
    private bool _updateActionInProgress;

    public TrayApplicationContext(
        IServiceProvider serviceProvider,
        RuntimeStateStore runtimeState,
        IRefreshController refreshController,
        StartupTaskService startupTaskService,
        TrayPreferencesStore preferencesStore,
        NotificationRequestQueue notificationQueue,
        SelfUpdateService selfUpdateService,
        ApplicationPaths paths)
    {
        _serviceProvider = serviceProvider;
        _runtimeState = runtimeState;
        _refreshController = refreshController;
        _startupTaskService = startupTaskService;
        _preferencesStore = preferencesStore;
        _notificationQueue = notificationQueue;
        _selfUpdateService = selfUpdateService;
        _paths = paths;
        _statusForm = ActivatorUtilities.CreateInstance<StatusForm>(_serviceProvider);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => _statusForm.ShowWindow());
        menu.Items.Add(new ToolStripSeparator());

        _forceRefreshItem = new ToolStripMenuItem("Force Refresh");
        _forceRefreshItem.Click += (_, _) => _refreshController.QueueRefresh("tray-menu");
        menu.Items.Add(_forceRefreshItem);

        _checkForUpdatesItem = new ToolStripMenuItem("Check for Updates");
        _checkForUpdatesItem.Click += async (_, _) => await TriggerManualUpdateCheckAsync();
        menu.Items.Add(_checkForUpdatesItem);

        _installUpdateItem = new ToolStripMenuItem("Restart To Update")
        {
            Enabled = false
        };
        _installUpdateItem.Click += async (_, _) => await ApplyPreparedUpdateAsync();
        menu.Items.Add(_installUpdateItem);

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
            _checkForUpdatesItem.Text = BuildCheckForUpdatesText(runtime.SelfUpdate);
            _checkForUpdatesItem.Enabled = !_updateActionInProgress &&
                runtime.SelfUpdate.State is not "checking" and not "downloading" and not "applying";

            _installUpdateItem.Text = string.IsNullOrWhiteSpace(runtime.SelfUpdate.PreparedVersion)
                ? "Restart To Update"
                : $"Restart To Update ({runtime.SelfUpdate.PreparedVersion})";
            _installUpdateItem.Enabled = !_updateActionInProgress &&
                !string.IsNullOrWhiteSpace(runtime.SelfUpdate.PreparedVersion);

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

        MaybeSchedulePreparedUpdate(runtime);
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
                WorkingDirectory = Path.GetDirectoryName(_paths.ConfigPath) ?? _paths.BaseDirectory
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

    private async Task TriggerManualUpdateCheckAsync()
    {
        if (_updateActionInProgress)
        {
            return;
        }

        try
        {
            await _selfUpdateService.CheckForUpdatesAsync("manual", CancellationToken.None);
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

    private async Task ApplyPreparedUpdateAsync()
    {
        if (_updateActionInProgress)
        {
            return;
        }

        _updateActionInProgress = true;
        _scheduledUpdateRestartAtUtc = null;
        UpdateUiState();

        try
        {
            var launched = await _selfUpdateService.ApplyPreparedUpdateAndRestartAsync(CancellationToken.None);
            if (launched)
            {
                ExitApplication();
                return;
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
        finally
        {
            _updateActionInProgress = false;
            UpdateUiState();
        }
    }

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

    private void MaybeSchedulePreparedUpdate(RuntimeSnapshot snapshot)
    {
        if (_updateActionInProgress)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.SelfUpdate.PreparedVersion))
        {
            _scheduledUpdateRestartAtUtc = null;
            return;
        }

        _scheduledUpdateRestartAtUtc ??= DateTimeOffset.UtcNow.AddSeconds(_selfUpdateService.RestartDelaySeconds);
        if (DateTimeOffset.UtcNow < _scheduledUpdateRestartAtUtc.Value)
        {
            return;
        }

        _ = ApplyPreparedUpdateAsync();
    }

    private static string BuildCheckForUpdatesText(SelfUpdateStatusSnapshot snapshot)
    {
        return snapshot.State switch
        {
            "checking" => "Checking for Updates...",
            "downloading" when snapshot.DownloadProgressPercent is { } progress => $"Downloading Update ({progress}%)",
            "applying" => "Applying Update...",
            _ => "Check for Updates"
        };
    }

    private static string BuildNotifyText(RuntimeSnapshot snapshot)
    {
        var text = !string.IsNullOrWhiteSpace(snapshot.SelfUpdate.PreparedVersion)
            ? "ModService: Update Ready"
            : snapshot.SelfUpdate.State == "downloading" && snapshot.SelfUpdate.DownloadProgressPercent is { } progress
                ? $"ModService: Updating ({progress}%)"
            : snapshot.RefreshInProgress
            ? $"ModService: Refreshing ({snapshot.LastRefreshReason})"
            : snapshot.QueuedRefreshCount > 0
                ? $"ModService: {snapshot.QueuedRefreshCount} queued"
                : "ModService: Running";

        return text.Length <= 63 ? text : text[..63];
    }
}

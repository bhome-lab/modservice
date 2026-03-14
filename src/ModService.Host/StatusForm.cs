using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ModService.Host;

public sealed class StatusForm : Form
{
    private readonly RuntimeStateStore _runtimeState;
    private readonly EffectiveConfigurationStore _configurationStore;
    private readonly IRefreshController _refreshController;
    private readonly StartupTaskService _startupTaskService;
    private readonly TrayPreferencesStore _preferencesStore;
    private readonly GitHubTokenManager _tokenManager;
    private readonly SelfUpdateService _selfUpdateService;
    private readonly ApplicationPaths _paths;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly TextBox _configPathValue;
    private readonly TextBox _configStatusValue;
    private readonly TextBox _startupValue;
    private readonly TextBox _tokenValue;
    private readonly TextBox _selfUpdateValue;
    private readonly TextBox _refreshValue;
    private readonly TextBox _executorValue;
    private readonly TextBox _processValue;
    private readonly CheckBox _startupCheckBox;
    private readonly CheckBox _notificationsCheckBox;
    private readonly Button _checkForUpdatesButton;
    private readonly Button _installUpdateButton;
    private readonly ListView _sourcesList;
    private readonly TextBox _configErrorsTextBox;
    private readonly TextBox _eventsTextBox;

    private bool _updatingToggles;
    private bool _updateActionInProgress;

    public StatusForm(
        RuntimeStateStore runtimeState,
        EffectiveConfigurationStore configurationStore,
        IRefreshController refreshController,
        StartupTaskService startupTaskService,
        TrayPreferencesStore preferencesStore,
        GitHubTokenManager tokenManager,
        SelfUpdateService selfUpdateService,
        ApplicationPaths paths)
    {
        _runtimeState = runtimeState;
        _configurationStore = configurationStore;
        _refreshController = refreshController;
        _startupTaskService = startupTaskService;
        _preferencesStore = preferencesStore;
        _tokenManager = tokenManager;
        _selfUpdateService = selfUpdateService;
        _paths = paths;

        Text = "ModService Status";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 640);
        Size = new Size(1100, 760);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(12),
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        Controls.Add(root);

        var summaryGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 8,
            AutoSize = true
        };
        summaryGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        summaryGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var rowIndex = 0; rowIndex < summaryGrid.RowCount; rowIndex++)
        {
            summaryGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        root.Controls.Add(summaryGrid, 0, 0);

        _configPathValue = AddSummaryRow(summaryGrid, 0, "Config File");
        _configStatusValue = AddSummaryRow(summaryGrid, 1, "Config Status");
        _startupValue = AddSummaryRow(summaryGrid, 2, "Startup");
        _tokenValue = AddSummaryRow(summaryGrid, 3, "GitHub Token");
        _selfUpdateValue = AddSummaryRow(summaryGrid, 4, "Self Update");
        _refreshValue = AddSummaryRow(summaryGrid, 5, "Refresh");
        _executorValue = AddSummaryRow(summaryGrid, 6, "Executor");
        _processValue = AddSummaryRow(summaryGrid, 7, "Process Watch");

        var actionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 8)
        };
        root.Controls.Add(actionsPanel, 0, 1);

        var queueRefreshButton = new Button
        {
            Text = "Queue Refresh",
            AutoSize = true
        };
        queueRefreshButton.Click += (_, _) => _refreshController.QueueRefresh("status-window");
        actionsPanel.Controls.Add(queueRefreshButton);

        _checkForUpdatesButton = new Button
        {
            Text = "Check For Updates",
            AutoSize = true
        };
        _checkForUpdatesButton.Click += async (_, _) => await TriggerManualUpdateCheckAsync();
        actionsPanel.Controls.Add(_checkForUpdatesButton);

        _installUpdateButton = new Button
        {
            Text = "Restart To Update",
            AutoSize = true,
            Enabled = false
        };
        _installUpdateButton.Click += async (_, _) => await ApplyPreparedUpdateAsync();
        actionsPanel.Controls.Add(_installUpdateButton);

        var openConfigButton = new Button
        {
            Text = "Open Config",
            AutoSize = true
        };
        openConfigButton.Click += (_, _) => OpenConfig();
        actionsPanel.Controls.Add(openConfigButton);

        var setTokenButton = new Button
        {
            Text = "Set GitHub Token",
            AutoSize = true
        };
        setTokenButton.Click += async (_, _) => await SetTokenAsync();
        actionsPanel.Controls.Add(setTokenButton);

        var importTokenButton = new Button
        {
            Text = "Import Token From gh",
            AutoSize = true
        };
        importTokenButton.Click += async (_, _) => await ImportTokenFromGhAsync();
        actionsPanel.Controls.Add(importTokenButton);

        var clearTokenButton = new Button
        {
            Text = "Clear GitHub Token",
            AutoSize = true
        };
        clearTokenButton.Click += (_, _) => ClearToken();
        actionsPanel.Controls.Add(clearTokenButton);

        _startupCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Run At Login As Admin",
            Margin = new Padding(16, 8, 0, 0)
        };
        _startupCheckBox.CheckedChanged += (_, _) => ToggleStartup();
        actionsPanel.Controls.Add(_startupCheckBox);

        _notificationsCheckBox = new CheckBox
        {
            AutoSize = true,
            Text = "Process Notifications",
            Margin = new Padding(16, 8, 0, 0)
        };
        _notificationsCheckBox.CheckedChanged += (_, _) => ToggleNotifications();
        actionsPanel.Controls.Add(_notificationsCheckBox);

        _sourcesList = new ListView
        {
            Dock = DockStyle.Fill,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            View = View.Details
        };
        _sourcesList.Columns.Add("Source", 180);
        _sourcesList.Columns.Add("Last Sync", 220);
        _sourcesList.Columns.Add("Assets", 80);
        _sourcesList.Columns.Add("Status", 400);
        root.Controls.Add(WrapInGroupBox("Sources", _sourcesList), 0, 2);

        _configErrorsTextBox = CreateReadOnlyTextBox();
        root.Controls.Add(WrapInGroupBox("Config Errors", _configErrorsTextBox), 0, 3);

        _eventsTextBox = CreateReadOnlyTextBox();
        root.Controls.Add(WrapInGroupBox("Recent Events", _eventsTextBox), 0, 4);

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1_000
        };
        _refreshTimer.Tick += (_, _) => RefreshView();
        _refreshTimer.Start();

        RefreshView();
    }

    public void ShowWindow()
    {
        if (!Visible)
        {
            Show();
        }

        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (eventArgs.CloseReason == CloseReason.UserClosing)
        {
            eventArgs.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(eventArgs);
    }

    private void RefreshView()
    {
        var runtime = _runtimeState.GetSnapshot();
        var configuration = _configurationStore.GetStatus();

        _configPathValue.Text = _paths.ConfigPath;
        _configStatusValue.Text = BuildConfigurationStatusText(configuration);
        _tokenValue.Text = BuildTokenText();
        _selfUpdateValue.Text = BuildSelfUpdateText(runtime.SelfUpdate);
        _refreshValue.Text = BuildRefreshText(runtime);
        _executorValue.Text = runtime.ExecutorPath ?? "Executor not available yet.";
        _processValue.Text = BuildProcessText(runtime);

        try
        {
            var startupEnabled = SafeIsStartupEnabled();
            _startupValue.Text = startupEnabled
                ? $"Enabled via scheduled task '{_startupTaskService.TaskName}'."
                : "Disabled.";
        }
        catch (Exception exception)
        {
            _startupValue.Text = $"Unable to query startup task: {exception.Message}";
        }

        _updatingToggles = true;
        try
        {
            _startupCheckBox.Checked = SafeIsStartupEnabled();
            _notificationsCheckBox.Checked = _preferencesStore.AreProcessNotificationsEnabled();
        }
        finally
        {
            _updatingToggles = false;
        }

        _checkForUpdatesButton.Enabled = !_updateActionInProgress &&
            runtime.SelfUpdate.State is not "checking" and not "downloading" and not "applying";
        _installUpdateButton.Enabled = !_updateActionInProgress &&
            !string.IsNullOrWhiteSpace(runtime.SelfUpdate.PreparedVersion);
        _installUpdateButton.Text = string.IsNullOrWhiteSpace(runtime.SelfUpdate.PreparedVersion)
            ? "Restart To Update"
            : $"Restart To Update ({runtime.SelfUpdate.PreparedVersion})";

        _sourcesList.BeginUpdate();
        try
        {
            _sourcesList.Items.Clear();
            foreach (var source in runtime.Sources.OrderBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ListViewItem(source.SourceId);
                item.SubItems.Add(source.SyncedAtUtc?.ToLocalTime().ToString("G") ?? "Never");
                item.SubItems.Add(source.AssetCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
                item.SubItems.Add(source.Status);
                _sourcesList.Items.Add(item);
            }
        }
        finally
        {
            _sourcesList.EndUpdate();
        }

        _configErrorsTextBox.Text = configuration.ValidationErrors.Count == 0
            ? "No validation errors."
            : string.Join(Environment.NewLine, configuration.ValidationErrors);

        _eventsTextBox.Text = runtime.RecentEvents.Count == 0
            ? "No runtime events yet."
            : string.Join(Environment.NewLine, runtime.RecentEvents);
    }

    private void ToggleStartup()
    {
        if (_updatingToggles)
        {
            return;
        }

        try
        {
            _startupTaskService.SetEnabled(_startupCheckBox.Checked);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        RefreshView();
    }

    private void ToggleNotifications()
    {
        if (_updatingToggles)
        {
            return;
        }

        try
        {
            _preferencesStore.SetProcessNotificationsEnabled(_notificationsCheckBox.Checked);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        RefreshView();
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

    private async Task SetTokenAsync()
    {
        using var dialog = new TokenEntryForm();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await RunTokenActionAsync(
            cancellationToken => _tokenManager.SaveAsync(dialog.Token, cancellationToken),
            "Saved GitHub token.");
    }

    private async Task ImportTokenFromGhAsync()
    {
        await RunTokenActionAsync(
            _tokenManager.ImportFromGhAsync,
            "Imported GitHub token from gh.");
    }

    private void ClearToken()
    {
        if (MessageBox.Show(
                this,
                "Clear the stored GitHub token?",
                "ModService",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _tokenManager.Clear();
            MessageBox.Show(
                this,
                "Cleared GitHub token.",
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        RefreshView();
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

    private string BuildTokenText()
    {
        return _tokenManager.HasToken()
            ? $"Configured. Store: {_tokenManager.FilePath}"
            : $"Not configured. Store: {_tokenManager.FilePath}";
    }

    private async Task RunTokenActionAsync(
        Func<CancellationToken, Task> action,
        string successMessage)
    {
        Enabled = false;
        UseWaitCursor = true;

        try
        {
            await action(CancellationToken.None);
            MessageBox.Show(
                this,
                successMessage,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            Enabled = true;
            RefreshView();
        }
    }

    private async Task TriggerManualUpdateCheckAsync()
    {
        if (_updateActionInProgress)
        {
            return;
        }

        _updateActionInProgress = true;
        RefreshView();

        try
        {
            await _selfUpdateService.CheckForUpdatesAsync("manual", CancellationToken.None);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _updateActionInProgress = false;
            RefreshView();
        }
    }

    private async Task ApplyPreparedUpdateAsync()
    {
        if (_updateActionInProgress)
        {
            return;
        }

        _updateActionInProgress = true;
        RefreshView();

        try
        {
            var launched = await _selfUpdateService.ApplyPreparedUpdateAndRestartAsync(CancellationToken.None);
            if (launched)
            {
                Application.Exit();
                return;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "ModService",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _updateActionInProgress = false;
            RefreshView();
        }
    }

    private static string BuildConfigurationStatusText(ConfigurationStatusSnapshot status)
    {
        if (status.HasConfiguration)
        {
            return status.UsingLastKnownGoodConfiguration
                ? $"Invalid config file detected; running last known good configuration (version {status.Version})."
                : $"Valid configuration loaded (version {status.Version}).";
        }

        return status.ValidationErrors.Count == 0
            ? "No valid configuration loaded yet."
            : "Configuration is invalid and no last known good configuration is available.";
    }

    private static string BuildRefreshText(RuntimeSnapshot snapshot)
    {
        var completed = snapshot.LastRefreshCompletedAtUtc?.ToLocalTime().ToString("G") ?? "never";
        var queueText = snapshot.QueuedRefreshCount == 0
            ? "No queued refreshes."
            : $"{snapshot.QueuedRefreshCount} refresh request(s) queued.";

        var state = snapshot.RefreshInProgress
            ? $"Running ({snapshot.LastRefreshReason})."
            : $"Idle. Last completed: {completed}.";

        var cleanup = $"Cleanup: stale={snapshot.Cleanup.StaleFileCount}, locked={snapshot.Cleanup.LockedFileCount}, deleted={snapshot.Cleanup.Deleted}.";
        return $"{state} {queueText} {snapshot.LastRefreshSummary} {BuildGitHubText(snapshot.GitHub)} {cleanup}";
    }

    private static string BuildProcessText(RuntimeSnapshot snapshot)
    {
        var activation = snapshot.LastActivationAtUtc is null
            ? snapshot.LastActivationSummary
            : $"{snapshot.LastActivationSummary} ({snapshot.LastActivationAtUtc.Value.ToLocalTime():G})";
        return $"{snapshot.LastProcessScanSummary} Last activation: {activation}";
    }

    private static string BuildGitHubText(GitHubSyncStatusSnapshot status)
    {
        return status.State switch
        {
            "rate_limited" when status.RateLimit?.BackoffUntilUtc is { } backoffUntilUtc
                => $"GitHub: rate limited until {backoffUntilUtc.ToLocalTime():G}.",
            "error" when !string.IsNullOrWhiteSpace(status.Error)
                => $"GitHub: error - {status.Error}",
            _ => "GitHub: ready."
        };
    }

    private static string BuildSelfUpdateText(SelfUpdateStatusSnapshot status)
    {
        var checkedAt = status.LastCheckedAtUtc?.ToLocalTime().ToString("G") ?? "never";
        var source = string.IsNullOrWhiteSpace(status.Source) ? "n/a" : status.Source;
        var current = string.IsNullOrWhiteSpace(status.CurrentVersion) ? "unknown" : status.CurrentVersion;
        var available = string.IsNullOrWhiteSpace(status.AvailableVersion) ? "none" : status.AvailableVersion;
        var prepared = string.IsNullOrWhiteSpace(status.PreparedVersion) ? "none" : status.PreparedVersion;
        return $"State: {status.State}. Current: {current}. Available: {available}. Prepared: {prepared}. Last check: {checkedAt}. Source: {source}. {status.Message}";
    }

    private static TextBox AddSummaryRow(TableLayoutPanel parent, int rowIndex, string title)
    {
        parent.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(Control.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 8),
            Text = title
        }, 0, rowIndex);

        var value = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            MinimumSize = new Size(0, 52),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            TabStop = false,
            WordWrap = true
        };
        value.BackColor = SystemColors.Window;
        parent.Controls.Add(value, 1, rowIndex);
        return value;
    }

    private static Control WrapInGroupBox(string title, Control child)
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = title
        };

        child.Dock = DockStyle.Fill;
        group.Controls.Add(child);
        return group;
    }

    private static TextBox CreateReadOnlyTextBox()
    {
        return new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill
        };
    }
}

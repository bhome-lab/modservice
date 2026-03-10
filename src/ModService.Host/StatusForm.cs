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
    private readonly ApplicationPaths _paths;
    private readonly System.Windows.Forms.Timer _refreshTimer;

    private readonly Label _configPathValue;
    private readonly Label _configStatusValue;
    private readonly Label _startupValue;
    private readonly Label _refreshValue;
    private readonly Label _executorValue;
    private readonly Label _processValue;
    private readonly CheckBox _startupCheckBox;
    private readonly CheckBox _notificationsCheckBox;
    private readonly ListView _sourcesList;
    private readonly TextBox _configErrorsTextBox;
    private readonly TextBox _eventsTextBox;

    private bool _updatingToggles;

    public StatusForm(
        RuntimeStateStore runtimeState,
        EffectiveConfigurationStore configurationStore,
        IRefreshController refreshController,
        StartupTaskService startupTaskService,
        TrayPreferencesStore preferencesStore,
        ApplicationPaths paths)
    {
        _runtimeState = runtimeState;
        _configurationStore = configurationStore;
        _refreshController = refreshController;
        _startupTaskService = startupTaskService;
        _preferencesStore = preferencesStore;
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
            RowCount = 6,
            AutoSize = true
        };
        summaryGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        summaryGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.Controls.Add(summaryGrid, 0, 0);

        _configPathValue = AddSummaryRow(summaryGrid, 0, "Config File");
        _configStatusValue = AddSummaryRow(summaryGrid, 1, "Config Status");
        _startupValue = AddSummaryRow(summaryGrid, 2, "Startup");
        _refreshValue = AddSummaryRow(summaryGrid, 3, "Refresh");
        _executorValue = AddSummaryRow(summaryGrid, 4, "Executor");
        _processValue = AddSummaryRow(summaryGrid, 5, "Process Watch");

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

        var openConfigButton = new Button
        {
            Text = "Open Config",
            AutoSize = true
        };
        openConfigButton.Click += (_, _) => OpenConfig();
        actionsPanel.Controls.Add(openConfigButton);

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
        return $"{state} {queueText} {snapshot.LastRefreshSummary} {cleanup}";
    }

    private static string BuildProcessText(RuntimeSnapshot snapshot)
    {
        var activation = snapshot.LastActivationAtUtc is null
            ? snapshot.LastActivationSummary
            : $"{snapshot.LastActivationSummary} ({snapshot.LastActivationAtUtc.Value.ToLocalTime():G})";
        return $"{snapshot.LastProcessScanSummary} Last activation: {activation}";
    }

    private static Label AddSummaryRow(TableLayoutPanel parent, int rowIndex, string title)
    {
        parent.Controls.Add(new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Font = new Font(Control.DefaultFont, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 8),
            Text = title
        }, 0, rowIndex);

        var value = new Label
        {
            AutoSize = false,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            Padding = new Padding(6),
            TextAlign = ContentAlignment.MiddleLeft
        };
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
            BorderStyle = BorderStyle.FixedSingle
        };
    }
}

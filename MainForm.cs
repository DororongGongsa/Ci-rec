namespace CiMeRecorderGui;

public sealed class MainForm : Form
{
    private readonly string _root = AppPaths.ExecutableDirectory;
    private readonly string _configPath;
    private readonly RecorderEngine _engine;
    private AppConfig _config;

    private readonly TextBox _outputBox = new();
    private readonly ComboBox _qualityBox = new();
    private readonly ComboBox _intervalBox = new();
    private readonly CheckBox _darkModeBox = new();
    private readonly Label _freeSpaceLabel = new();
    private readonly TextBox _urlBox = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _logBox = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly System.Windows.Forms.Timer _durationTimer = new();
    private readonly Dictionary<string, DateTime> _recordingStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _recordingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressConfigSave;

    private readonly Dictionary<string, int> _intervals = new()
    {
        ["5\uCD08"] = 5,
        ["10\uCD08"] = 10,
        ["30\uCD08"] = 30,
        ["1\uBD84"] = 60,
        ["5\uBD84"] = 300
    };

    public MainForm()
    {
        _configPath = AppPaths.ConfigPath;
        _config = ConfigStore.Load(_configPath);
        var legacyConfigPath = Path.Combine(_root, "recorder-config.json");
        if (!File.Exists(_configPath) && File.Exists(legacyConfigPath))
        {
            _config = ConfigStore.Load(legacyConfigPath);
            ConfigStore.Save(_configPath, _config);
        }
        _config.FfmpegPath = FfmpegBundle.EnsureExtracted();

        _engine = new RecorderEngine(_root);
        _engine.StatusChanged += EngineOnStatusChanged;
        _engine.LogWritten += (_, line) => BeginInvoke(() => AppendLog(line, false));
        _durationTimer.Interval = 1000;
        _durationTimer.Tick += (_, _) => UpdateRecordingDurations();

        Text = "\uC528\uBBF8\uB179\uD654";
        var appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        if (appIcon != null) Icon = appIcon;
        MinimumSize = new Size(900, 580);
        Size = new Size(1020, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);

        BuildUi();
        _suppressConfigSave = true;
        LoadConfigIntoUi();
        RefreshGrid();
        _suppressConfigSave = false;
        ApplyTheme();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        Controls.Add(root);

        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 11, RowCount = 1 };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        root.Controls.Add(settings, 0, 0);

        settings.Controls.Add(new Label { Text = "\uC800\uC7A5\uD3F4\uB354", TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _outputBox.Dock = DockStyle.Fill;
        settings.Controls.Add(_outputBox, 1, 0);
        var browseOutput = new Button { Text = "\uCC3E\uAE30", Dock = DockStyle.Fill };
        browseOutput.Click += (_, _) => BrowseFolder();
        settings.Controls.Add(browseOutput, 2, 0);
        settings.Controls.Add(new Label { Text = "\uD654\uC9C8", TextAlign = ContentAlignment.MiddleLeft }, 3, 0);
        SetupQualityBox(_qualityBox);
        settings.Controls.Add(_qualityBox, 4, 0);
        settings.Controls.Add(new Label { Text = "\uC8FC\uAE30", TextAlign = ContentAlignment.MiddleLeft }, 5, 0);
        SetupIntervalBox();
        settings.Controls.Add(_intervalBox, 6, 0);
        _darkModeBox.Text = "\uB2E4\uD06C\uBAA8\uB4DC";
        _darkModeBox.TextAlign = ContentAlignment.MiddleLeft;
        _darkModeBox.Dock = DockStyle.Fill;
        _darkModeBox.CheckedChanged += (_, _) =>
        {
            if (_suppressConfigSave) return;
            _config.DarkMode = _darkModeBox.Checked;
            ApplyTheme();
            SaveConfig();
        };
        settings.Controls.Add(_darkModeBox, 8, 0);
        _freeSpaceLabel.TextAlign = ContentAlignment.MiddleLeft;
        _freeSpaceLabel.Dock = DockStyle.Fill;
        settings.Controls.Add(_freeSpaceLabel, 10, 0);

        var channelBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1 };
        channelBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        channelBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
        channelBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        channelBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 8));
        channelBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        root.Controls.Add(channelBar, 0, 1);

        _urlBox.Dock = DockStyle.Fill;
        _urlBox.PlaceholderText = "https://ci.me/@name/live";
        channelBar.Controls.Add(_urlBox, 0, 0);

        var addButton = new Button { Text = "\uCD94\uAC00", Dock = DockStyle.Fill };
        addButton.Click += (_, _) => AddChannel();
        channelBar.Controls.Add(addButton, 2, 0);
        var removeButton = new Button { Text = "\uC0AD\uC81C", Dock = DockStyle.Fill };
        removeButton.Click += (_, _) => RemoveSelected();
        channelBar.Controls.Add(removeButton, 4, 0);

        var actionBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _startButton.Text = "\uAC10\uC2DC \uC2DC\uC791";
        _startButton.Dock = DockStyle.Fill;
        _startButton.Margin = new Padding(0, 6, 8, 6);
        _startButton.Click += (_, _) => StartMonitoring();
        _stopButton.Text = "\uC911\uC9C0";
        _stopButton.Dock = DockStyle.Fill;
        _stopButton.Margin = new Padding(0, 6, 8, 6);
        _stopButton.Enabled = false;
        _stopButton.Click += async (_, _) => await StopMonitoringAsync();
        actionBar.Controls.Add(_startButton, 0, 0);
        actionBar.Controls.Add(_stopButton, 1, 0);
        actionBar.Controls.Add(new Label { Text = "\uC0C1\uD0DC\uC640 \uC6A9\uB7C9\uC740 \uB179\uD654 \uC911 1\uCD08\uB9C8\uB2E4 \uAC31\uC2E0\uB429\uB2C8\uB2E4.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 9F) }, 2, 0);
        root.Controls.Add(actionBar, 0, 2);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "\uC0AC\uC6A9", FillWeight = 38 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "\uC774\uB984", FillWeight = 74 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Url", HeaderText = "URL", FillWeight = 190 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RecordingState", HeaderText = "\uB179\uD654\uC0C1\uD0DC", FillWeight = 68, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Duration", HeaderText = "\uB179\uD654\uC2DC\uAC04", FillWeight = 64, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "\uC6A9\uB7C9", FillWeight = 58, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FinalizeProgress", HeaderText = "\uBCC0\uD658", FillWeight = 48, ReadOnly = true });
        _grid.CellEndEdit += (_, _) => SaveGridToConfig();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, _) => SaveGridToConfig();
        root.Controls.Add(_grid, 0, 3);

        _logBox.Dock = DockStyle.Fill;
        _logBox.Multiline = true;
        _logBox.ScrollBars = ScrollBars.Vertical;
        _logBox.ReadOnly = true;
        root.Controls.Add(_logBox, 0, 4);
    }

    private void SetupQualityBox(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.Items.AddRange(new object[] { "2160p", "1440p", "1080p", "720p", "480p", "360p", "best", "worst" });
        combo.SelectedItem = "1080p";
    }

    private void SetupIntervalBox()
    {
        _intervalBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _intervalBox.Dock = DockStyle.Fill;
        _intervalBox.Items.AddRange(_intervals.Keys.Cast<object>().ToArray());
        _intervalBox.SelectedItem = "30\uCD08";
    }

    private void LoadConfigIntoUi()
    {
        _outputBox.Text = _config.OutputDirectory;
        _qualityBox.SelectedItem = string.IsNullOrWhiteSpace(_config.Quality) ? "1080p" : _config.Quality;
        _intervalBox.SelectedItem = _intervals.FirstOrDefault(pair => pair.Value == _config.CheckIntervalSeconds).Key ?? "30\uCD08";
        _darkModeBox.Checked = _config.DarkMode;
        UpdateFreeSpaceLabel();
    }

    private void SaveUiToConfig()
    {
        _config.OutputDirectory = _outputBox.Text.Trim();
        _config.FfmpegPath = FfmpegBundle.EnsureExtracted();
        _config.CheckIntervalSeconds = _intervals.TryGetValue(_intervalBox.SelectedItem?.ToString() ?? "30\uCD08", out var seconds) ? seconds : 30;
        _config.Quality = _qualityBox.SelectedItem?.ToString() ?? "1080p";
        _config.DarkMode = _darkModeBox.Checked;
        SaveGridToConfig();
    }

    private void RefreshGrid()
    {
        var wasSuppressing = _suppressConfigSave;
        _suppressConfigSave = true;
        _grid.Rows.Clear();
        foreach (var channel in _config.Channels)
        {
            _grid.Rows.Add(channel.Enabled, channel.EffectiveName, channel.Url, "\uB179\uD654 \uC624\uD504\uB77C\uC778", "00:00:00", "-", "-");
        }
        _suppressConfigSave = wasSuppressing;
    }

    private void SaveGridToConfig()
    {
        if (_suppressConfigSave) return;
        var channels = new List<ChannelConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var url = row.Cells["Url"].Value?.ToString()?.Trim() ?? "";
            if (url.Length == 0) continue;
            channels.Add(new ChannelConfig
            {
                Enabled = row.Cells["Enabled"].Value as bool? ?? Convert.ToBoolean(row.Cells["Enabled"].Value ?? true),
                Name = row.Cells["Name"].Value?.ToString()?.Trim() ?? "",
                Url = url,
                Quality = "default"
            });
        }
        _config.Channels = channels;
    }

    private void AddChannel()
    {
        var url = _urlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show("\uCC44\uB110 URL\uC744 \uD655\uC778\uD574\uC8FC\uC138\uC694.", "\uC528\uBBF8\uB179\uD654", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveGridToConfig();
        if (_config.Channels.Any(c => string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("\uC774\uBBF8 \uCD94\uAC00\uB41C \uCC44\uB110\uC785\uB2C8\uB2E4.", "\uC528\uBBF8\uB179\uD654", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _config.Channels.Add(new ChannelConfig
        {
            Url = url,
            Name = ChannelName.FromUrl(url),
            Quality = "default",
            Enabled = true
        });
        _urlBox.Clear();
        RefreshGrid();
        SaveConfig();
    }

    private void RemoveSelected()
    {
        if (_grid.SelectedRows.Count == 0) return;
        _grid.Rows.Remove(_grid.SelectedRows[0]);
        SaveGridToConfig();
        SaveConfig();
    }

    private void StartMonitoring()
    {
        SaveConfig();
        _engine.Start(_config);
        _durationTimer.Start();
        UpdateFreeSpaceLabel();
        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        AppendLog("\uAC10\uC2DC \uC2DC\uC791", false);
    }

    private async Task StopMonitoringAsync()
    {
        _stopButton.Enabled = false;
        MarkRecordingRowsAsFinalizing();
        AppendLog("\uB9C8\uBB34\uB9AC\uC911", false);
        await _engine.StopAsync();
        _durationTimer.Stop();
        _recordingStarts.Clear();
        _recordingPaths.Clear();
        _startButton.Enabled = true;
        ResetGridStates();
        AppendLog("\uC911\uC9C0", false);
    }

    private void MarkRecordingRowsAsFinalizing()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if ((row.Cells["RecordingState"].Value?.ToString() ?? "") == "\uB179\uD654\uC911")
            {
                row.Cells["RecordingState"].Value = "\uB9C8\uBB34\uB9AC\uC911";
            }
        }
    }

    private void ResetGridStates()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var enabled = row.Cells["Enabled"].Value as bool? ?? Convert.ToBoolean(row.Cells["Enabled"].Value ?? true);
            row.Cells["RecordingState"].Value = enabled ? "\uB179\uD654 \uC624\uD504\uB77C\uC778" : "\uAEBC\uC9D0";
            row.Cells["Duration"].Value = "00:00:00";
            row.Cells["Size"].Value = "-";
            row.Cells["FinalizeProgress"].Value = "-";
        }
    }

    private void SaveConfig()
    {
        SaveUiToConfig();
        ConfigStore.Save(_configPath, _config);
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputBox.Text = dialog.SelectedPath;
        UpdateFreeSpaceLabel();
    }

    private void EngineOnStatusChanged(object? sender, ChannelStatusEventArgs e)
    {
        BeginInvoke(() =>
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!string.Equals(row.Cells["Url"].Value?.ToString(), e.Channel.Url, StringComparison.OrdinalIgnoreCase)) continue;
                row.Cells["RecordingState"].Value = RecordingStateText(e.State);
                if (e.State == ChannelState.Failed)
                {
                    AppLogger.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Channel.Url} :: {e.Message}");
                }
                if (e.State == ChannelState.Recording && e.RecordingStartedAt.HasValue)
                {
                    _recordingStarts[e.Channel.Url] = e.RecordingStartedAt.Value;
                    if (!string.IsNullOrWhiteSpace(e.RecordingPath))
                    {
                        _recordingPaths[e.Channel.Url] = e.RecordingPath;
                    }
                    row.Cells["Duration"].Value = FormatDuration(DateTime.Now - e.RecordingStartedAt.Value);
                    row.Cells["Size"].Value = FormatFileSize(e.RecordingPath);
                    row.Cells["FinalizeProgress"].Value = "-";
                }
                else if (e.State == ChannelState.Finalizing)
                {
                    if (e.RecordingStartedAt.HasValue)
                    {
                        row.Cells["Duration"].Value = FormatDuration(DateTime.Now - e.RecordingStartedAt.Value);
                    }
                    row.Cells["Size"].Value = FormatFileSize(e.RecordingPath);
                    row.Cells["FinalizeProgress"].Value = e.FinalizeProgressPercent.HasValue ? $"{e.FinalizeProgressPercent.Value}%" : "0%";
                }
                else if (e.State != ChannelState.Recording)
                {
                    _recordingStarts.Remove(e.Channel.Url);
                    _recordingPaths.Remove(e.Channel.Url);
                    row.Cells["Duration"].Value = "00:00:00";
                    row.Cells["Size"].Value = "-";
                    row.Cells["FinalizeProgress"].Value = "-";
                }
                break;
            }
        });
    }

    private string RecordingStateText(ChannelState state)
    {
        return state switch
        {
            ChannelState.Checking => "\uD655\uC778\uC911",
            ChannelState.Recording => "\uB179\uD654\uC911",
            ChannelState.Finalizing => "\uB9C8\uBB34\uB9AC\uC911",
            ChannelState.Failed => "\uC2E4\uD328",
            ChannelState.Disabled => "\uAEBC\uC9D0",
            _ => "\uB179\uD654 \uC624\uD504\uB77C\uC778"
        };
    }

    private void UpdateRecordingDurations()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var url = row.Cells["Url"].Value?.ToString();
            if (string.IsNullOrWhiteSpace(url) || !_recordingStarts.TryGetValue(url, out var startedAt)) continue;
            row.Cells["Duration"].Value = FormatDuration(DateTime.Now - startedAt);
            if (_recordingPaths.TryGetValue(url, out var path))
            {
                row.Cells["Size"].Value = FormatFileSize(path);
            }
        }
        UpdateFreeSpaceLabel();
    }

    private string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private string FormatFileSize(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "-";
            return FormatBytes(new FileInfo(path).Length);
        }
        catch
        {
            return "-";
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private void UpdateFreeSpaceLabel()
    {
        try
        {
            var path = _outputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) path = "recordings";
            if (!Path.IsPathRooted(path)) path = Path.Combine(_root, path);
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                _freeSpaceLabel.Text = "\uC5EC\uC720 -";
                return;
            }
            var drive = new DriveInfo(root);
            _freeSpaceLabel.Text = "\uC5EC\uC720 " + FormatBytes(drive.AvailableFreeSpace);
        }
        catch
        {
            _freeSpaceLabel.Text = "\uC5EC\uC720 -";
        }
    }

    private void AppendLog(string line, bool saveToFile)
    {
        if (saveToFile) AppLogger.Write(line);
        _logBox.AppendText(line + Environment.NewLine);
    }

    private void ApplyTheme()
    {
        var dark = _darkModeBox.Checked;
        var back = dark ? Color.FromArgb(28, 28, 30) : SystemColors.Control;
        var panel = dark ? Color.FromArgb(38, 38, 42) : SystemColors.Window;
        var fore = dark ? Color.FromArgb(240, 240, 240) : SystemColors.ControlText;
        ApplyThemeToControl(this, back, panel, fore);

        _grid.BackgroundColor = panel;
        _grid.GridColor = dark ? Color.FromArgb(70, 70, 74) : SystemColors.ControlDark;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = dark ? Color.FromArgb(48, 48, 52) : SystemColors.Control;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = fore;
        _grid.DefaultCellStyle.BackColor = panel;
        _grid.DefaultCellStyle.ForeColor = fore;
        _grid.DefaultCellStyle.SelectionBackColor = dark ? Color.FromArgb(68, 88, 120) : SystemColors.Highlight;
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
    }

    private void ApplyThemeToControl(Control control, Color back, Color panel, Color fore)
    {
        if (control is Button button)
        {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Standard;
            button.BackColor = _darkModeBox.Checked ? Color.FromArgb(58, 58, 64) : SystemColors.Control;
            button.ForeColor = _darkModeBox.Checked ? Color.White : SystemColors.ControlText;
        }
        else
        {
            control.BackColor = control is TextBox or DataGridView or ComboBox ? panel : back;
            control.ForeColor = fore;
        }

        foreach (Control child in control.Controls)
        {
            ApplyThemeToControl(child, back, panel, fore);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveConfig();
        _engine.StopAsync().GetAwaiter().GetResult();
        _durationTimer.Stop();
        base.OnFormClosing(e);
    }
}

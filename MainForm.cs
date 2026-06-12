using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuickSwap;

// ===================== Models =====================

public class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Process { get; set; } = "";
}

public class Config
{
    public List<AppEntry> Apps { get; set; } = new();
}

// ===================== Main Form =====================

public partial class MainForm : Form
{
    private const string CONFIG_FILE = "modes_config.json";

    private Config _config = new();
    private string _configPath;

    private Button _launchBtn = null!, _closeBtn = null!;
    private DataGridView _grid = null!;
    private Button _addBtn = null!, _deleteBtn = null!;
    private ProgressBar _progress = null!;
    private Label _statusLabel = null!;
    private System.Windows.Forms.Timer _statusTimer = null!;

    private Dictionary<string, string> _discovered = new();

    public MainForm()
    {
        _configPath = Path.Combine(Application.StartupPath, CONFIG_FILE);
        LoadConfig();
        DiscoverApps();

        Text = "QuickSwap";
        Size = new Size(720, 500);
        MinimumSize = new Size(500, 340);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUI();
        RefreshGrid();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _statusTimer.Tick += (_, _) => RefreshRunningStatus();
        _statusTimer.Start();
    }

    // ===================== Config I/O =====================

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
            else { _config = new Config(); SaveConfig(); }
        }
        catch { _config = new Config(); }
    }

    private void SaveConfig()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, opts));
    }

    // ===================== Start Menu Discovery =====================

    private void DiscoverApps()
    {
        try
        {
            var tempFile = Path.GetTempFileName();
            var psScript = @"
$wsh = New-Object -ComObject WScript.Shell
$results = @{}
$paths = @(
    [Environment]::GetFolderPath('CommonStartMenu'),
    [Environment]::GetFolderPath('StartMenu')
)
foreach ($p in $paths) {
    if (-not (Test-Path $p)) { continue }
    Get-ChildItem -Path $p -Filter *.lnk -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $target = $wsh.CreateShortcut($_.FullName).TargetPath
            if ($target -and (Test-Path $target)) {
                $results[$_.BaseName] = $target
            }
        } catch {}
    }
}
$results | ConvertTo-Json -Compress | Out-File -Encoding UTF8 '" + tempFile.Replace("\\", "\\\\") + @"'
";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(10000);

            if (File.Exists(tempFile))
            {
                var json = File.ReadAllText(tempFile);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kv in dict)
                            if (!_discovered.ContainsKey(kv.Key) && !kv.Key.Contains("卸载"))
                                _discovered[kv.Key] = kv.Value;
                    }
                }
                try { File.Delete(tempFile); } catch { }
            }
        }
        catch { }
    }

    // ===================== UI =====================

    private void BuildUI()
    {
        var pad = 12;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(pad),
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // -- row 0: action buttons --
        var topPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 2),
        };

        _launchBtn = new Button
        {
            Text = "▶ 一键启动",
            Height = 32,
            Width = 110,
            BackColor = Color.ForestGreen,
            ForeColor = Color.White,
        };
        _launchBtn.Click += async (_, _) => await LaunchSelected();
        topPanel.Controls.Add(_launchBtn);

        _closeBtn = new Button
        {
            Text = "■ 一键关闭",
            Height = 32,
            Width = 110,
            BackColor = Color.OrangeRed,
            ForeColor = Color.White,
        };
        _closeBtn.Click += async (_, _) => await CloseSelected();
        topPanel.Controls.Add(_closeBtn);

        var tip = new Label
        {
            Text = "  勾选应用 → 点按钮批量操作",
            AutoSize = true,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        topPanel.Controls.Add(tip);

        table.Controls.Add(topPanel, 0, 0);

        // -- row 1: grid --
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
        };

        var colCheck = new DataGridViewCheckBoxColumn
        {
            Name = "Check",
            HeaderText = "✓",
            Width = 40,
            FillWeight = 8,
        };
        var colName = new DataGridViewTextBoxColumn
        {
            Name = "Name",
            HeaderText = "应用",
            FillWeight = 35,
            ReadOnly = true,
        };
        var colProc = new DataGridViewTextBoxColumn
        {
            Name = "Process",
            HeaderText = "进程",
            FillWeight = 30,
            ReadOnly = true,
        };
        var colStatus = new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "状态",
            FillWeight = 15,
            ReadOnly = true,
        };

        _grid.Columns.AddRange(colCheck, colName, colProc, colStatus);

        // single-click checkbox toggle
        _grid.CellContentClick += (_, e) =>
        {
            if (e.ColumnIndex == 0 && e.RowIndex >= 0)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell.ColumnIndex == 0)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        // highlight checked rows
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.ColumnIndex == 0 && e.RowIndex >= 0)
            {
                var row = _grid.Rows[e.RowIndex];
                var isChecked = (bool)(row.Cells["Check"].Value ?? false);
                row.DefaultCellStyle.BackColor = isChecked ? Color.FromArgb(230, 247, 230) : Color.White;
            }
        };
        _grid.CellDoubleClick += (_, _) => EditSelectedApp();
        _grid.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Space && _grid.SelectedRows.Count > 0)
            {
                e.Handled = true;
                foreach (DataGridViewRow row in _grid.SelectedRows)
                {
                    var cell = (DataGridViewCheckBoxCell)row.Cells["Check"];
                    cell.Value = !(bool)(cell.Value ?? false);
                }
            }
        };

        table.Controls.Add(_grid, 0, 1);

        // -- row 2: progress bar --
        _progress = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Height = 10,
            Style = ProgressBarStyle.Continuous,
            Maximum = 100,
            Visible = false,
        };
        table.Controls.Add(_progress, 0, 2);

        // -- row 3: bottom buttons + status --
        var botPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 0),
        };

        var btnPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        _addBtn = new Button { Text = "添加应用", Width = 88, Height = 28 };
        _addBtn.Click += (_, _) => AddApp();
        _deleteBtn = new Button { Text = "删除", Width = 64, Height = 28 };
        _deleteBtn.Click += (_, _) => DeleteSelectedApp();
        btnPanel.Controls.AddRange([_addBtn, _deleteBtn]);

        var selectAll = new Button { Text = "全选", Width = 64, Height = 28 };
        selectAll.Click += (_, _) => SetAllChecked(true);
        var selectNone = new Button { Text = "全不选", Width = 76, Height = 28 };
        selectNone.Click += (_, _) => SetAllChecked(false);
        btnPanel.Controls.AddRange([selectAll, selectNone]);

        botPanel.Controls.Add(btnPanel);

        _statusLabel = new Label { AutoSize = true, ForeColor = Color.Gray };
        botPanel.Controls.Add(_statusLabel);

        table.Controls.Add(botPanel, 0, 3);

        Controls.Add(table);
    }

    private void SetAllChecked(bool check)
    {
        var color = check ? Color.FromArgb(230, 247, 230) : Color.White;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Cells["Check"].Value = check;
            row.DefaultCellStyle.BackColor = color;
        }
    }

    // ===================== Grid =====================

    private void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var app in _config.Apps)
        {
            var row = new DataGridViewRow();
            row.Cells.Add(new DataGridViewCheckBoxCell { Value = false });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = app.Name });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = string.IsNullOrEmpty(app.Process) ? "—" : app.Process });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = "..." });
            row.Tag = app;
            _grid.Rows.Add(row);
        }
        RefreshRunningStatus();
    }

    private void RefreshRunningStatus()
    {
        var procs = Process.GetProcesses().ToLookup(p => p.ProcessName, StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not AppEntry a) continue;
            var name = Path.GetFileNameWithoutExtension(a.Process) ?? a.Process;
            var running = !string.IsNullOrEmpty(name) && procs.Contains(name);
            row.Cells["Status"].Value = running ? "● 运行中" : "○ 未运行";
            row.Cells["Status"].Style.ForeColor = running ? Color.ForestGreen : Color.Gray;
        }
    }

    // ===================== Add / Edit / Delete =====================

    private void AddApp()
    {
        var existing = _config.Apps.Select(a => a.Name).ToHashSet();
        using var dlg = new AppEditForm(_discovered, null, existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.Apps.Add(dlg.Result);
        SaveConfig();
        RefreshGrid();
    }

    private void EditSelectedApp()
    {
        var app = SelectedApp();
        if (app == null) return;
        var idx = _config.Apps.IndexOf(app);
        var existing = _config.Apps.Where(a => a != app).Select(a => a.Name).ToHashSet();
        using var dlg = new AppEditForm(_discovered, app, existing);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.Apps[idx] = dlg.Result;
        SaveConfig();
        RefreshGrid();
    }

    private void DeleteSelectedApp()
    {
        var selected = CheckedApps();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先勾选要删除的应用。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var names = string.Join("、", selected.Select(a => a.Name));
        var ok = MessageBox.Show(this, $"确认从列表删除 {names}？", "确认",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return;
        foreach (var app in selected) _config.Apps.Remove(app);
        SaveConfig();
        RefreshGrid();
    }

    private AppEntry? SelectedApp()
    {
        if (_grid.SelectedRows.Count == 0) return null;
        return _grid.SelectedRows[0].Tag as AppEntry;
    }

    private List<AppEntry> SelectedApps()
    {
        return _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.Tag as AppEntry)
            .Where(a => a != null)
            .ToList()!;
    }

    // ===================== Actions =====================

    private async Task LaunchSelected()
    {
        var apps = CheckedApps();
        if (apps.Count == 0)
        {
            MessageBox.Show(this, "请先勾选要启动的应用。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await RunWithProgress("启动", apps, LaunchApp);
    }

    private async Task CloseSelected()
    {
        var apps = CheckedApps();
        if (apps.Count == 0)
        {
            MessageBox.Show(this, "请先勾选要关闭的应用。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await RunWithProgress("关闭", apps, CloseApp);
    }

    private List<AppEntry> CheckedApps()
    {
        return _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => (bool)(r.Cells["Check"].Value ?? false))
            .Select(r => r.Tag as AppEntry)
            .Where(a => a != null)
            .ToList()!;
    }

    private async Task RunWithProgress(string action, List<AppEntry> apps, Func<AppEntry, bool> act)
    {
        _launchBtn.Enabled = false;
        _closeBtn.Enabled = false;
        _progress.Value = 0;
        _progress.Visible = true;

        var failed = new List<string>();
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            SetStatus($"正在{action} {app.Name}...  ({i + 1}/{apps.Count})");
            var ok = await Task.Run(() => act(app));
            if (!ok) failed.Add(app.Name);
            _progress.Value = (i + 1) * 100 / apps.Count;
            _progress.Refresh(); // force repaint
            await Task.Delay(300);
        }

        _progress.Visible = false;
        var msg = $"{action}完成 — 共处理 {apps.Count} 个应用";
        if (failed.Count > 0)
            msg += $"（{string.Join("、", failed)} 未能{action}）";
        SetStatus(msg);
        RefreshRunningStatus();
        _launchBtn.Enabled = true;
        _closeBtn.Enabled = true;
    }

    private static bool LaunchApp(AppEntry app)
    {
        try
        {
            if (string.IsNullOrEmpty(app.Path)) return false;
            var psi = new ProcessStartInfo(app.Path) { UseShellExecute = true };
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    private static bool CloseApp(AppEntry app)
    {
        if (string.IsNullOrEmpty(app.Process)) return false;

        var imageName = app.Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? app.Process : app.Process + ".exe";
        var procName = Path.GetFileNameWithoutExtension(imageName);

        bool StillAlive()
        {
            try
            {
                var procs = Process.GetProcessesByName(procName);
                return procs.Length > 0 && procs.Any(p => { try { return !p.HasExited; } catch { return true; } });
            }
            catch { return true; } // can't check → assume alive
        }

        if (!StillAlive()) return true;

        // step 1: CloseMainWindow (gentle WM_CLOSE)
        foreach (var p in Process.GetProcessesByName(procName))
        {
            try { p.CloseMainWindow(); } catch { }
        }
        for (int i = 0; i < 15; i++) { if (!StillAlive()) return true; Thread.Sleep(200); }

        // step 2: Process.Kill() (TerminateProcess API, may work when taskkill doesn't)
        foreach (var p in Process.GetProcessesByName(procName))
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
        for (int i = 0; i < 10; i++) { if (!StillAlive()) return true; Thread.Sleep(200); }

        // step 3: taskkill /F /T
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /IM \"{imageName}\" /T",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
        for (int i = 0; i < 10; i++) { if (!StillAlive()) return true; Thread.Sleep(200); }

        // step 4: taskkill by PID
        foreach (var pid in GetPids())
        {
            if (!StillAlive()) return true;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /PID {pid} /T",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        }

        // step 5: PowerShell Stop-Process -Force (last resort)
        foreach (var pid in GetPids())
        {
            if (!StillAlive()) return true;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"Stop-Process -Id {pid} -Force -ErrorAction SilentlyContinue\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
            }
            catch { }
        }

        return !StillAlive();

        int[] GetPids()
        {
            try { return Process.GetProcessesByName(procName).Select(p => p.Id).ToArray(); }
            catch { return []; }
        }
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _statusLabel.Text = text;
    }
}

// ===================== Add/Edit Dialog =====================

public partial class AppEditForm : Form
{
    private Dictionary<string, string> _discovered;
    private HashSet<string> _existingNames;
    private AppEntry _source;

    private TextBox _txtSearch = null!;
    private ListBox _discoveredList = null!;
    private TextBox _txtName = null!, _txtPath = null!, _txtProcess = null!;
    private Button _btnBrowse = null!;
    private SplitContainer _split = null!;

    public AppEntry Result { get; private set; } = new();

    public AppEditForm(Dictionary<string, string> discovered, AppEntry? source, HashSet<string> existingNames)
    {
        _discovered = discovered;
        _existingNames = existingNames;
        _source = source ?? new AppEntry();
        Result = new AppEntry { Name = _source.Name, Path = _source.Path, Process = _source.Process };

        Text = source == null ? "添加应用" : "编辑应用";
        Size = new Size(600, 420);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildUI();
        if (source != null) FillFrom(Result);
    }

    private void BuildUI()
    {
        var pad = 12;

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = true,
        };

        // left panel: discovered apps
        var leftTop = _split.Panel1;
        leftTop.Padding = new Padding(pad);
        var lblLeft = new Label { Text = "从开始菜单选择（双击添加）：", AutoSize = true, Location = new Point(pad, pad) };
        _txtSearch = new TextBox
        {
            Location = new Point(pad, lblLeft.Bottom + 4),
            Width = leftTop.ClientSize.Width - pad * 2,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            PlaceholderText = "输入关键词过滤...",
        };
        _txtSearch.TextChanged += (_, _) => FilterDiscovered();
        _discoveredList = new ListBox
        {
            Location = new Point(pad, _txtSearch.Bottom + 4),
            IntegralHeight = false,
            DisplayMember = "Key",
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        _discoveredList.MouseDoubleClick += (_, _) => PickDiscovered();
        leftTop.Controls.Add(lblLeft);
        leftTop.Controls.Add(_txtSearch);
        leftTop.Controls.Add(_discoveredList);
        FillDiscovered("");
        leftTop.Layout += (_, _) =>
        {
            _discoveredList.Width = leftTop.ClientSize.Width - pad * 2;
            _discoveredList.Height = leftTop.ClientSize.Height - _discoveredList.Top - pad;
            _txtSearch.Width = leftTop.ClientSize.Width - pad * 2;
        };

        // right panel: manual fields
        var right = _split.Panel2;
        right.Padding = new Padding(pad);
        var y = pad;
        (_txtName, _) = AddFieldY(right, "名称：", ref y);
        (_txtPath, _) = AddFieldY(right, "路径：", ref y);
        _btnBrowse = new Button { Text = "...", Size = new Size(30, 23), Location = new Point(right.ClientSize.Width - pad - 34, _txtPath.Location.Y) };
        _btnBrowse.Click += (_, _) => BrowsePath();
        right.Controls.Add(_btnBrowse);
        (_txtProcess, _) = AddFieldY(right, "进程名：", ref y, "如 Steam.exe");
        _txtPath.TextChanged += (_, _) =>
        {
            if (string.IsNullOrEmpty(_txtProcess.Text) && !string.IsNullOrEmpty(_txtPath.Text))
                _txtProcess.Text = Path.GetFileName(_txtPath.Text);
        };
        right.Layout += (_, _) =>
        {
            _btnBrowse.Location = new Point(right.ClientSize.Width - pad - 34, _txtPath.Location.Y);
        };

        // bottom: buttons
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var btnOK = new Button
        {
            Text = "确定", Size = new Size(88, 28),
            BackColor = Color.SteelBlue, ForeColor = Color.White,
        };
        btnOK.Click += (_, _) => Commit();
        bottom.Controls.Add(btnOK);
        var btnCancel = new Button { Text = "取消", Size = new Size(88, 28) };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottom.Controls.Add(btnCancel);
        bottom.Layout += (_, _) =>
        {
            btnOK.Location = new Point(bottom.ClientSize.Width - pad - 88, 10);
            btnCancel.Location = new Point(bottom.ClientSize.Width - pad * 2 - 88 * 2 - 8, 10);
        };

        Controls.Add(_split);
        Controls.Add(bottom);

        this.Load += (_, _) =>
        {
            if (_split.Width > 400)
                _split.SplitterDistance = _split.Width * 3 / 5;
        };
    }

    private static (TextBox, Label) AddFieldY(Control parent, string label, ref int y, string? placeholder = null)
    {
        var pad = 12;
        var lbl = new Label { Text = label, AutoSize = true, Location = new Point(pad, y) };
        parent.Controls.Add(lbl);
        y = lbl.Bottom + 2;
        var tb = new TextBox
        {
            Location = new Point(pad, y),
            Width = parent.ClientSize.Width - pad * 2 - 40,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            PlaceholderText = placeholder,
        };
        parent.Controls.Add(tb);
        y = tb.Bottom + 10;
        return (tb, lbl);
    }

    private void FillFrom(AppEntry a)
    {
        _txtName.Text = a.Name;
        _txtPath.Text = a.Path;
        _txtProcess.Text = a.Process;
    }

    private void FillDiscovered(string filter)
    {
        _discoveredList.Items.Clear();
        foreach (var kv in _discovered.OrderBy(k => k.Key))
        {
            if (kv.Key.Contains("卸载")) continue;
            if (string.IsNullOrEmpty(filter) || kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _discoveredList.Items.Add(kv);
        }
    }

    private void FilterDiscovered() => FillDiscovered(_txtSearch.Text.Trim());

    private void PickDiscovered()
    {
        if (_discoveredList.SelectedItem is not KeyValuePair<string, string> kv) return;
        _txtName.Text = kv.Key;
        _txtPath.Text = kv.Value;
        _txtProcess.Text = Path.GetFileName(kv.Value);
    }

    private void BrowsePath()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _txtPath.Text = dlg.FileName;
            if (string.IsNullOrEmpty(_txtName.Text))
                _txtName.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            if (string.IsNullOrEmpty(_txtProcess.Text))
                _txtProcess.Text = Path.GetFileName(dlg.FileName);
        }
    }

    private void Commit()
    {
        var name = _txtName.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "请输入应用名称。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_existingNames.Contains(name) && name != _source.Name)
        {
            MessageBox.Show(this, $"应用「{name}」已存在。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Result.Name = name;
        Result.Path = _txtPath.Text.Trim();
        Result.Process = _txtProcess.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}

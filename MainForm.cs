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

namespace ModeSwitcher;

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

        Text = "ModeSwitcher";
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
                            if (!_discovered.ContainsKey(kv.Key))
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

        // -- top: action buttons --
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(pad, pad, pad, 6),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
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

        Controls.Add(topPanel);

        // -- grid --
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
        // commit immediately
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

        Controls.Add(_grid);

        // -- progress bar (between grid and bottom buttons) --
        _progress = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 10,
            Style = ProgressBarStyle.Continuous,
            Maximum = 100,
            Visible = false,
        };
        Controls.Add(_progress);

        // -- bottom --
        var botPanel = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(pad, pad, pad, 8) };

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
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

        _statusLabel = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 2, 0, 0) };
        botPanel.Controls.Add(_statusLabel);

        Controls.Add(botPanel);
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
        using var dlg = new AppEditForm(_discovered, null);
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
        using var dlg = new AppEditForm(_discovered, app);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.Apps[idx] = dlg.Result;
        SaveConfig();
        RefreshGrid();
    }

    private void DeleteSelectedApp()
    {
        var selected = SelectedApps();
        if (selected.Count == 0) return;
        var names = string.Join("、", selected.Select(a => a.Name));
        var ok = MessageBox.Show(this, $"确认删除「{names}」？", "确认",
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
        var procName = Path.GetFileNameWithoutExtension(app.Process);

        bool StillAlive()
        {
            var procs = Process.GetProcessesByName(procName);
            return procs.Any(p => !p.HasExited);
        }

        if (!StillAlive()) return true; // already dead

        // step 1: CloseMainWindow (gentle WM_CLOSE to main window)
        foreach (var p in Process.GetProcessesByName(procName))
        {
            try { p.CloseMainWindow(); } catch { }
        }
        for (int i = 0; i < 30; i++) { if (!StillAlive()) return true; Thread.Sleep(200); }

        // step 2: taskkill /IM (WM_CLOSE to all windows, still gentle)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/IM \"{app.Process}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { }
        for (int i = 0; i < 15; i++) { if (!StillAlive()) return true; Thread.Sleep(200); }

        // step 3: taskkill /F (force — last resort)
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/F /IM \"{app.Process}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }

        return !StillAlive();
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
    private AppEntry _source;

    private TextBox _txtSearch = null!;
    private ListBox _discoveredList = null!;
    private TextBox _txtName = null!, _txtPath = null!, _txtProcess = null!;
    private Button _btnBrowse = null!;

    public AppEntry Result { get; private set; } = new();

    public AppEditForm(Dictionary<string, string> discovered, AppEntry? source)
    {
        _discovered = discovered;
        _source = source ?? new AppEntry();
        Result = new AppEntry { Name = _source.Name, Path = _source.Path, Process = _source.Process };

        Text = source == null ? "添加应用" : "编辑应用";
        Size = new Size(560, 380);
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

        // left: discovered apps
        var left = new Panel { Location = new Point(pad, pad), Size = new Size(260, 290) };
        left.Controls.Add(new Label { Text = "从开始菜单选择（双击添加）：", AutoSize = true, Location = new Point(0, 0) });

        _txtSearch = new TextBox { Location = new Point(0, 22), Width = 258, PlaceholderText = "输入关键词过滤..." };
        _txtSearch.TextChanged += (_, _) => FilterDiscovered();
        left.Controls.Add(_txtSearch);

        _discoveredList = new ListBox
        {
            Location = new Point(0, 50), Width = 258, Height = 238, IntegralHeight = false,
            DisplayMember = "Key",
        };
        _discoveredList.MouseDoubleClick += (_, _) => PickDiscovered();
        left.Controls.Add(_discoveredList);
        FillDiscovered("");

        Controls.Add(left);

        // right: manual fields
        var right = new Panel { Location = new Point(284, pad), Size = new Size(258, 260) };

        _txtName = AddField(right, "名称：", 0, 242);
        _txtPath = AddField(right, "路径：", 56, 164);
        _btnBrowse = new Button { Text = "...", Location = new Point(226, 77), Width = 30, Height = 23 };
        _btnBrowse.Click += (_, _) => BrowsePath();
        right.Controls.Add(_btnBrowse);

        _txtProcess = AddField(right, "进程名：", 112, 242, "如 Steam.exe");
        _txtPath.TextChanged += (_, _) =>
        {
            if (string.IsNullOrEmpty(_txtProcess.Text) && !string.IsNullOrEmpty(_txtPath.Text))
                _txtProcess.Text = Path.GetFileName(_txtPath.Text);
        };

        Controls.Add(right);

        // OK / Cancel
        var btnOK = new Button
        {
            Text = "确定", Location = new Point(356, 316), Size = new Size(80, 28),
            BackColor = Color.SteelBlue, ForeColor = Color.White,
        };
        btnOK.Click += (_, _) => Commit();
        Controls.Add(btnOK);

        var btnCancel = new Button { Text = "取消", Location = new Point(446, 316), Size = new Size(80, 28) };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
    }

    private static TextBox AddField(Control parent, string label, int y, int width, string? placeholder = null)
    {
        parent.Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(0, y) });
        var tb = new TextBox { Location = new Point(0, y + 20), Width = width, PlaceholderText = placeholder };
        parent.Controls.Add(tb);
        return tb;
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
        Result.Name = name;
        Result.Path = _txtPath.Text.Trim();
        Result.Process = _txtProcess.Text.Trim();
        DialogResult = DialogResult.OK;
        Close();
    }
}

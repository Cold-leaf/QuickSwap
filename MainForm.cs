using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ModeSwitcher;

// ===================== Models =====================

public class AppEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Process { get; set; } = "";
    public List<string> StartIn { get; set; } = new();
    public List<string> StopIn { get; set; } = new();

    public string ActionFor(string mode)
    {
        if (StopIn.Contains(mode)) return "关闭";
        if (StartIn.Contains(mode)) return "启动";
        return "—";
    }

    public bool Affects(string mode) => StartIn.Contains(mode) || StopIn.Contains(mode);
}

public class Config
{
    public List<string> Modes { get; set; } = new() { "game", "work" };
    public List<AppEntry> Apps { get; set; } = new();
}

// ===================== Main Form =====================

public partial class MainForm : Form
{
    private const string CONFIG_FILE = "modes_config.json";

    private Config _config = new();
    private string _configPath;
    private string _currentMode;

    // controls
    private ComboBox _modeCombo = null!;
    private Button _switchBtn = null!;
    private DataGridView _grid = null!;
    private Button _addBtn = null!, _editBtn = null!, _deleteBtn = null!;
    private ProgressBar _progress = null!;
    private Label _statusLabel = null!;
    private System.Windows.Forms.Timer _statusTimer = null!;

    // cache: display name -> exe path (from Start Menu scan)
    private Dictionary<string, string> _discovered = new();

    public MainForm()
    {
        _configPath = Path.Combine(Application.StartupPath, CONFIG_FILE);
        LoadConfig();
        DiscoverApps();
        _currentMode = _config.Modes.FirstOrDefault() ?? "game";

        Text = "ModeSwitcher";
        Size = new Size(760, 530);
        MinimumSize = new Size(600, 380);
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
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            };

            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var lnk in Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    var target = ResolveShortcut(lnk);
                    if (target != null && !_discovered.ContainsKey(name))
                        _discovered[name] = target;
                }
            }
        }
        catch { /* non-critical: discovery failure shouldn't crash the app */ }
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
            if (t == null) return null;
            var shell = Activator.CreateInstance(t);
            if (shell == null) return null;
            var sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc == null) return null;
            var target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
            return !string.IsNullOrEmpty(target) && File.Exists(target) ? target : null;
        }
        catch { return null; }
    }

    // ===================== UI Construction =====================

    private void BuildUI()
    {
        var pad = 12;

        // -- top bar --
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Padding = new Padding(pad, pad, pad, 6),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
        };

        topPanel.Controls.Add(new Label { Text = "模式：", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft });

        _modeCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
        };
        foreach (var m in _config.Modes) _modeCombo.Items.Add(m);
        _modeCombo.SelectedItem = _currentMode;
        _modeCombo.SelectedIndexChanged += (_, _) =>
        {
            _currentMode = (string)_modeCombo.SelectedItem!;
            RefreshGrid();
        };
        topPanel.Controls.Add(_modeCombo);

        _switchBtn = new Button { Text = "切换模式", Height = 28, BackColor = Color.SteelBlue, ForeColor = Color.White };
        _switchBtn.Click += async (_, _) => await SwitchMode();
        topPanel.Controls.Add(_switchBtn);

        Controls.Add(topPanel);

        // -- app grid --
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ReadOnly = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
        };

        // columns: Enabled | Name | Action | Process | Running
        var colCb = new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "", Width = 30, FillWeight = 5, ReadOnly = true };
        var colName = new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "应用", FillWeight = 30 };
        var colAction = new DataGridViewTextBoxColumn { Name = "Action", HeaderText = "动作", FillWeight = 10 };
        var colProc = new DataGridViewTextBoxColumn { Name = "Process", HeaderText = "进程", FillWeight = 30 };
        var colStatus = new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "状态", FillWeight = 15 };

        _grid.Columns.AddRange(colCb, colName, colAction, colProc, colStatus);
        _grid.CellFormatting += Grid_CellFormatting;
        _grid.CellDoubleClick += (_, _) => EditSelectedApp();

        Controls.Add(_grid);

        // -- bottom bar --
        var botPanel = new Panel { Dock = DockStyle.Bottom, Height = 70, Padding = new Padding(pad) };

        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        _addBtn = new Button { Text = "添加应用", Width = 80 };
        _addBtn.Click += (_, _) => AddApp();
        _editBtn = new Button { Text = "编辑", Width = 60 };
        _editBtn.Click += (_, _) => EditSelectedApp();
        _deleteBtn = new Button { Text = "删除", Width = 60 };
        _deleteBtn.Click += (_, _) => DeleteSelectedApp();
        btnPanel.Controls.AddRange([_addBtn, _editBtn, _deleteBtn]);
        botPanel.Controls.Add(btnPanel);

        _statusLabel = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.Gray, Padding = new Padding(0, 4, 0, 0) };
        botPanel.Controls.Add(_statusLabel);

        _progress = new ProgressBar { Dock = DockStyle.Bottom, Height = 6, Style = ProgressBarStyle.Continuous, Maximum = 100 };
        botPanel.Controls.Add(_progress);

        Controls.Add(botPanel);
    }

    // ===================== Grid =====================

    private void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var app in _config.Apps)
        {
            var row = new DataGridViewRow();
            row.Cells.Add(new DataGridViewCheckBoxCell { Value = app.Affects(_currentMode) });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = app.Name });
            row.Cells.Add(new DataGridViewTextBoxCell { Value = app.ActionFor(_currentMode) });
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
            if (running)
                row.Cells["Status"].Style.ForeColor = Color.ForestGreen;
            else
                row.Cells["Status"].Style.ForeColor = Color.Gray;
        }
    }

    private void Grid_CellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_grid.Columns[e.ColumnIndex].Name == "Action" && e.Value is string action)
        {
            e.CellStyle!.ForeColor = action switch
            {
                "关闭" => Color.OrangeRed,
                "启动" => Color.SteelBlue,
                _ => Color.Gray,
            };
        }
    }

    // ===================== Add / Edit / Delete =====================

    private void AddApp()
    {
        using var dlg = new AppEditForm(_config.Modes, _discovered, null);
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
        using var dlg = new AppEditForm(_config.Modes, _discovered, app);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.Apps[idx] = dlg.Result;
        SaveConfig();
        RefreshGrid();
    }

    private void DeleteSelectedApp()
    {
        var app = SelectedApp();
        if (app == null) return;
        var ok = MessageBox.Show(this, $"确认删除「{app.Name}」？", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return;
        _config.Apps.Remove(app);
        SaveConfig();
        RefreshGrid();
    }

    private AppEntry? SelectedApp()
    {
        if (_grid.SelectedRows.Count == 0) return null;
        return _grid.SelectedRows[0].Tag as AppEntry;
    }

    // ===================== Mode Switch =====================

    private async Task SwitchMode()
    {
        _switchBtn.Enabled = false;
        var apps = _config.Apps.Where(a => a.Affects(_currentMode)).ToList();
        var total = apps.Count;
        _progress.Value = 0;

        for (int i = 0; i < total; i++)
        {
            var app = apps[i];
            if (app.StartIn.Contains(_currentMode))
            {
                SetStatus($"正在启动 {app.Name}...");
                await Task.Run(() => LaunchApp(app));
            }
            else if (app.StopIn.Contains(_currentMode))
            {
                SetStatus($"正在关闭 {app.Name}...");
                await Task.Run(() => CloseApp(app));
            }
            _progress.Value = (int)((i + 1) / (double)total * 100);
            this.Update(); // force UI refresh
        }

        _progress.Value = 100;
        SetStatus($"「{_currentMode}」模式切换完成");
        RefreshRunningStatus();
        _switchBtn.Enabled = true;
    }

    private static void LaunchApp(AppEntry app)
    {
        try
        {
            if (string.IsNullOrEmpty(app.Path)) return;
            var psi = new ProcessStartInfo(app.Path) { UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch {app.Name}: {ex.Message}");
        }
    }

    private static void CloseApp(AppEntry app)
    {
        if (string.IsNullOrEmpty(app.Process)) return;
        var name = Path.GetFileNameWithoutExtension(app.Process);
        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0) return;

        // gentle close: send WM_CLOSE, wait 5s, then skip (no force kill)
        foreach (var p in procs)
        {
            try
            {
                if (p.HasExited) continue;
                p.CloseMainWindow();
                p.WaitForExit(5000);
            }
            catch { /* may not have a main window */ }
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
    private List<string> _modes;
    private Dictionary<string, string> _discovered;
    private AppEntry _source;

    private TextBox _txtSearch = null!;
    private ListBox _discoveredList = null!;
    private TextBox _txtName = null!, _txtPath = null!, _txtProcess = null!;
    private Button _btnBrowse = null!;
    private FlowLayoutPanel _startPanel = null!;
    private FlowLayoutPanel _stopPanel = null!;
    private Button _btnOK = null!;
    private List<CheckBox> _startChecks = new();
    private List<CheckBox> _stopChecks = new();

    public AppEntry Result { get; private set; } = new();

    public AppEditForm(List<string> modes, Dictionary<string, string> discovered, AppEntry? source)
    {
        _modes = modes;
        _discovered = discovered;
        _source = source ?? new AppEntry();
        Result = new AppEntry
        {
            Name = _source.Name,
            Path = _source.Path,
            Process = _source.Process,
            StartIn = new List<string>(_source.StartIn),
            StopIn = new List<string>(_source.StopIn),
        };

        Text = source == null ? "添加应用" : "编辑应用";
        Size = new Size(560, 480);
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

        // -- left panel: discovered apps --
        var left = new Panel { Location = new Point(pad, pad), Size = new Size(260, 390) };
        left.Controls.Add(new Label { Text = "从开始菜单选择（双击添加）：", AutoSize = true, Location = new Point(0, 0) });

        _txtSearch = new TextBox { Location = new Point(0, 22), Width = 258, PlaceholderText = "输入关键词过滤..." };
        _txtSearch.TextChanged += (_, _) => FilterDiscovered();
        left.Controls.Add(_txtSearch);

        _discoveredList = new ListBox
        {
            Location = new Point(0, 50), Width = 258, Height = 338, IntegralHeight = false,
            DisplayMember = "Key",
        };
        _discoveredList.MouseDoubleClick += (_, _) => PickDiscovered();
        left.Controls.Add(_discoveredList);
        FillDiscovered("");

        Controls.Add(left);

        // -- right panel: manual entry --
        var right = new Panel { Location = new Point(284, pad), Size = new Size(258, 390) };

        _txtName = AddLabeledField(right, "名称：", 0, 242);
        _txtPath = AddLabeledField(right, "路径：", 56, 164);
        _btnBrowse = new Button { Text = "...", Location = new Point(226, 77), Width = 30, Height = 23 };
        _btnBrowse.Click += (_, _) => BrowsePath();
        right.Controls.Add(_btnBrowse);

        _txtProcess = AddLabeledField(right, "进程名：", 112, 242, "如 Steam.exe");
        _txtPath.TextChanged += (_, _) =>
        {
            if (string.IsNullOrEmpty(_txtProcess.Text) && !string.IsNullOrEmpty(_txtPath.Text))
                _txtProcess.Text = Path.GetFileName(_txtPath.Text);
        };

        var cy = 168;
        right.Controls.Add(new Label { Text = "启动于此模式：", AutoSize = true, Location = new Point(0, cy) });
        _startPanel = new FlowLayoutPanel
        {
            Location = new Point(0, cy + 20), Width = 258, Height = 54,
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true,
        };
        foreach (var m in _modes)
        {
            var cb = new CheckBox { Text = m, AutoSize = true, Tag = m };
            cb.Checked = _source.StartIn.Contains(m);
            _startChecks.Add(cb);
            _startPanel.Controls.Add(cb);
        }
        right.Controls.Add(_startPanel);

        cy += 78;
        right.Controls.Add(new Label { Text = "关闭于此模式：", AutoSize = true, Location = new Point(0, cy) });
        _stopPanel = new FlowLayoutPanel
        {
            Location = new Point(0, cy + 20), Width = 258, Height = 54,
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true,
        };
        foreach (var m in _modes)
        {
            var cb = new CheckBox { Text = m, AutoSize = true, Tag = m };
            cb.Checked = _source.StopIn.Contains(m);
            _stopChecks.Add(cb);
            _stopPanel.Controls.Add(cb);
        }
        right.Controls.Add(_stopPanel);

        Controls.Add(right);

        // -- OK/Cancel --
        _btnOK = new Button
        {
            Text = "确定", Location = new Point(356, 416), Size = new Size(80, 28),
            BackColor = Color.SteelBlue, ForeColor = Color.White,
        };
        _btnOK.Click += (_, _) => Commit();
        Controls.Add(_btnOK);

        var btnCancel = new Button { Text = "取消", Location = new Point(446, 416), Size = new Size(80, 28) };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);
    }

    private static TextBox AddLabeledField(Control parent, string label, int y, int width, string? placeholder = null)
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
        foreach (var cb in _startChecks) cb.Checked = a.StartIn.Contains((string)cb.Tag!);
        foreach (var cb in _stopChecks) cb.Checked = a.StopIn.Contains((string)cb.Tag!);
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
        Result.StartIn = _startChecks.Where(c => c.Checked).Select(c => (string)c.Tag!).ToList();
        Result.StopIn = _stopChecks.Where(c => c.Checked).Select(c => (string)c.Tag!).ToList();
        DialogResult = DialogResult.OK;
        Close();
    }
}

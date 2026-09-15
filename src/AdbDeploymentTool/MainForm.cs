using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdbDeploymentTool;

internal sealed partial class MainForm : Form
{
    private sealed record Device(string Serial, string Description) { public override string ToString() => Description; }
    private sealed record SourceFile(string Path, string Label) { public override string ToString() => Label; }
    private readonly string _root = AppContext.BaseDirectory;
    private readonly ComboBox _devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly RadioButton _qc = new() { Text = "QC（全彩）", AutoSize = true, Checked = true };
    private readonly RadioButton _dl = new() { Text = "DL（单绿）", AutoSize = true };
    private readonly Dictionary<string, (RadioButton Overwrite, RadioButton Skip, Label Device)> _choices = [];
    private readonly Dictionary<string, ComboBox> _sources = [];
    private readonly Dictionary<string, string> _customPaths = [];
    private readonly Dictionary<string, string> _localPreviews = [];
    private IReadOnlyDictionary<string, DeviceComponentState> _deviceStates = new Dictionary<string, DeviceComponentState>();
    private readonly ToolTip _tips = new() { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 30000, ShowAlways = true };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(22, 27, 34), ForeColor = Color.FromArgb(220, 230, 240), Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None };
    private readonly Label _status = Label("准备就绪");
    private readonly Label _updateStatus = Label("客户端更新：尚未检查");
    private readonly Label _adbStatus = Label("ADB：尚未检测");
    private readonly Button _installUpdate = Button("下载并更新客户端");
    private readonly Button _checkUpdate = Button("检查更新");
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Height = 9 };
    private readonly List<Control> _locked = [];
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _inspection;
    private long _inspectionRevision;
    private bool _busy, _checking, _handoff, _loadingSources, _refreshingDevices;
    private string? _adb;
    private UpdateSettings _settings = new();
    private VerifiedRelease? _release;
    private UnityVariant Variant => _qc.Checked ? UnityVariant.QC : UnityVariant.DL;
    private Dictionary<string, string?> CurrentFiles => _sources.ToDictionary(p => p.Key, p => (p.Value.SelectedItem as SourceFile)?.Path);
    private string SelectionKey(string id) => id == "Unity" ? "Unity." + Variant : id;

    public MainForm(bool preview = false)
    {
        Text = ApplicationIdentity.Name + " v" + UpdateService.AppVersion;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        BackColor = Color.FromArgb(245, 247, 250);
        ClientSize = new Size(1240, 850);
        MinimumSize = new Size(1120, 760);
        BuildLayout();
        try { _settings = UpdateSettings.Load(_root); } catch (Exception ex) { Log("更新配置读取失败，使用默认 GitHub 地址：" + ex.Message); }
        LoadSelection(); ReloadSources();
        _qc.CheckedChanged += (_, _) => { if (_qc.Checked) { ReloadSources(); _ = RefreshDeviceStatusAsync(); } };
        _dl.CheckedChanged += (_, _) => { if (_dl.Checked) { ReloadSources(); _ = RefreshDeviceStatusAsync(); } };
        _devices.SelectedIndexChanged += async (_, _) => { if (!_refreshingDevices) await RefreshDeviceStatusAsync(); };
        _checkUpdate.Click += async (_, _) => await CheckUpdateAsync(true);
        _installUpdate.Click += async (_, _) => await InstallUpdateAsync();
        _timer.Tick += async (_, _) => { if (_settings.AutoCheck) await CheckUpdateAsync(false); };
        if (!preview) ResetTimer();
        Shown += async (_, _) =>
        {
            if (preview) return;
            // 每次启动均检查更新；网络请求与 ADB 检测同时运行，不等待设备，也不阻塞 UI。
            await Task.WhenAll(CheckUpdateAsync(false), RefreshAsync());
            var result = Path.Combine(_root, ".updates", "last-result.txt");
            try { if (File.Exists(result)) Log(File.ReadAllText(result)); } catch (IOException ex) { Log(ex.Message); }
        };
        FormClosing += (_, e) =>
        {
            if (_busy && !_handoff) { e.Cancel = true; _status.Text = "任务正在执行，请完成后退出。"; return; }
            _timer.Stop(); CancelInspection(); _lifetime.Cancel(); SaveSelection();
        };
        FormClosed += (_, _) => { _timer.Dispose(); _tips.Dispose(); };
    }

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 6, 4, 6) };
    private static Button Button(string text) => new() { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(4) };
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        p.Controls.AddRange(controls); return p;
    }
    private Button ActionButton(string text, Action action)
    {
        var button = Button(text); button.Click += (_, _) => action(); _locked.Add(button); return button;
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Padding = new Padding(20), AutoScroll = true };
        for (var i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = Label(ApplicationIdentity.Name); title.Font = new Font(Font.FontFamily, 22, FontStyle.Bold);
        layout.Controls.Add(Flow(title, Label("v" + UpdateService.AppVersion)), 0, 0);
        var refresh = ActionButton("刷新设备与文件", async () => await RefreshAsync());
        _locked.Add(_devices);
        layout.Controls.Add(Flow(Label("目标设备"), _devices, refresh, _adbStatus), 0, 1);
        var variant = Flow(Label("Unity 类型"), _qc, _dl, Label("悬停配置可预览内容；双击设备状态可打开内容窗口。"));
        layout.Controls.Add(variant, 0, 2); _locked.Add(variant);

        var choices = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 8, BackColor = Color.White, Padding = new Padding(8) };
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        for (int i = 0; i < 8; i++) choices.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? 34 : 44));
        foreach (var (caption, column) in new[] { ("部署部分", 0), ("设备状态", 1), ("是否覆盖 / 安装", 2), ("本地文件", 3), ("文件来源", 4) }) choices.Controls.Add(Label(caption), column, 0);
        int row = 1;
        foreach (var component in Component.All)
        {
            var overwrite = new RadioButton { Text = "覆盖", Checked = true, AutoSize = true, Margin = new Padding(4, 5, 8, 2) };
            var skip = new RadioButton { Text = "跳过", AutoSize = true, Margin = new Padding(4, 5, 8, 2) };
            var state = Label("未检测"); state.ForeColor = Color.Gray;
            var source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(4, 7, 4, 5) };
            var pair = Flow(overwrite, skip); pair.WrapContents = false;
            _choices.Add(component.Id, (overwrite, skip, state)); _sources.Add(component.Id, source);
            var name = Label(component.Label);
            choices.Controls.Add(name, 0, row); choices.Controls.Add(state, 1, row); choices.Controls.Add(pair, 2, row); choices.Controls.Add(source, 3, row);
            var browse = Button("选择…"); browse.Padding = new Padding(2); browse.Click += (_, _) => BrowseSource(component);
            var reset = Button("默认"); reset.Padding = new Padding(2); reset.Click += (_, _) => { _customPaths.Remove(SelectionKey(component.Id)); ReloadSources(); _ = RefreshDeviceStatusAsync(); };
            var buttons = Flow(browse, reset); buttons.WrapContents = false;
            choices.Controls.Add(buttons, 4, row++);
            source.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSources) return;
                if (source.SelectedItem is SourceFile file) _customPaths[SelectionKey(component.Id)] = file.Path;
                UpdateLocalPreview(component); _ = RefreshDeviceStatusAsync();
            };
            name.MouseEnter += (_, _) => UpdateLocalPreview(component, name);
            name.DoubleClick += (_, _) => ShowLocalPreview(component);
            source.MouseEnter += (_, _) => UpdateLocalPreview(component);
            state.DoubleClick += (_, _) => { if (_deviceStates.TryGetValue(component.Id, out var status)) ShowText(component.Label + " · 设备内容", status.Detail + "\n\n" + (status.Preview ?? "没有可用内容预览。")); };
        }
        _locked.Add(choices); layout.Controls.Add(choices, 0, 3);
        layout.Controls.Add(Flow(ActionButton("全部覆盖", () => SetPreset(_ => true)),
            ActionButton("仅 SO 和应用（跳过所有配置）", () => SetPreset(c => c.Id is "Unity" or "Launcher" || c.Id.EndsWith(".so"))),
            ActionButton("全部跳过", () => SetPreset(_ => false))), 0, 4);
        var deploy = ActionButton("执行选中项目", async () => await DeployAsync());
        deploy.BackColor = Color.FromArgb(37, 99, 235); deploy.ForeColor = Color.White; deploy.FlatStyle = FlatStyle.Flat;
        layout.Controls.Add(Flow(deploy, ActionButton("导出 Unity files…", async () => await ExportAsync()),
            Label("覆盖 Launcher 会备份系统桌面并重启设备。")), 0, 5);
        var settings = ActionButton("更新设置", EditUpdateSettings);
        _installUpdate.Enabled = false;
        layout.Controls.Add(Flow(_checkUpdate, _installUpdate, settings, _updateStatus), 0, 6);
        layout.Controls.Add(Flow(Label("执行日志"), _status), 0, 7);
        var logs = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, MinimumSize = new Size(0, 100) };
        logs.RowStyles.Add(new RowStyle(SizeType.Absolute, 9)); logs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logs.Controls.Add(_progress, 0, 0); logs.Controls.Add(_log, 0, 1); layout.Controls.Add(logs, 0, 8);
        Controls.Add(layout);
    }

    private void SetPreset(Func<Component, bool> included)
    {
        foreach (var c in Component.All) { _choices[c.Id].Overwrite.Checked = included(c); _choices[c.Id].Skip.Checked = !included(c); }
    }

    private void ReloadSources()
    {
        _loadingSources = true;
        try
        {
            var catalog = new ResourceCatalog(_root);
            foreach (var component in Component.All)
            {
                var paths = component.Id switch { "Unity" => catalog.Apks(Variant).ToList(), "Launcher" => catalog.Apks(null).ToList(),
                    _ => catalog.Find(component.Id) is { } found ? new List<string> { found } : [] };
                _customPaths.TryGetValue(SelectionKey(component.Id), out var preferred);
                if (preferred != null && !paths.Contains(preferred, StringComparer.OrdinalIgnoreCase)) paths.Insert(0, preferred);
                var box = _sources[component.Id]; box.Items.Clear();
                foreach (var path in paths)
                {
                    var local = path.StartsWith(Path.Combine(_root, "tools") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    box.Items.Add(new SourceFile(path, (local ? "tools/" + Path.GetFileName(path) : "[自定义] " + path) + (File.Exists(path) ? "" : "（文件缺失）")));
                }
                var item = box.Items.Cast<SourceFile>().FirstOrDefault(x => string.Equals(x.Path, preferred, StringComparison.OrdinalIgnoreCase));
                if (item != null) box.SelectedItem = item; else box.SelectedIndex = paths.Count == 1 ? 0 : -1;
                UpdateLocalPreview(component);
            }
        }
        catch (Exception ex) { Log("资源扫描失败：" + ex.Message); }
        finally { _loadingSources = false; }
    }

    private void BrowseSource(Component component)
    {
        using var dialog = new OpenFileDialog { Title = "选择 " + component.Label, CheckFileExists = true, Multiselect = false,
            Filter = component.Target == null ? "Android 应用 (*.apk)|*.apk" : $"{component.Label} (*{Path.GetExtension(component.Id)})|*{Path.GetExtension(component.Id)}" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (component.Id == "Unity" && !ResourceCatalog.IsUnity(dialog.FileName, Variant))
        { MessageBox.Show(this, $"请选择名称含大写 {Variant} 且包含 Unity 或 COMO_UN 的 APK。", "型号不匹配"); return; }
        _customPaths[SelectionKey(component.Id)] = dialog.FileName; ReloadSources(); SaveSelection(); _ = RefreshDeviceStatusAsync();
    }

    private void UpdateLocalPreview(Component component, Control? extra = null)
    {
        var source = _sources[component.Id]; var path = (source.SelectedItem as SourceFile)?.Path;
        string text = path ?? (component.Id == "Unity" ? $"未选择 {Variant} APK；默认从 tools 目录查找。" : "未选择文件；默认从 tools 目录查找。");
        if (path != null && ConfigurationPreview.IsConfig(component))
        {
            try { _localPreviews[component.Id] = ConfigurationPreview.ReadLocal(path); text += "\n\n" + _localPreviews[component.Id]; }
            catch (Exception ex) { _localPreviews.Remove(component.Id); text += "\n读取失败：" + ex.Message; }
        }
        _tips.SetToolTip(source, ConfigurationPreview.Tooltip(text));
        if (extra != null) _tips.SetToolTip(extra, ConfigurationPreview.Tooltip(text));
    }

    private void ShowLocalPreview(Component component)
    {
        UpdateLocalPreview(component);
        if (_localPreviews.TryGetValue(component.Id, out var text)) ShowText(component.Label + " · 本地内容", text);
    }
    private void ShowText(string title, string text)
    {
        using var dialog = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(800, 600), MinimizeBox = false };
        dialog.Controls.Add(new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Text = text, Font = new Font("Consolas", 10), WordWrap = false });
        dialog.ShowDialog(this);
    }

    private void CancelInspection() { _inspectionRevision++; _inspection?.Cancel(); }
    private void ClearDeviceStates(string text)
    {
        _deviceStates = new Dictionary<string, DeviceComponentState>();
        foreach (var choice in _choices.Values) { choice.Device.Text = text; choice.Device.ForeColor = Color.Gray; _tips.SetToolTip(choice.Device, text); }
    }

    private async Task RefreshDeviceStatusAsync()
    {
        CancelInspection();
        if (_busy || IsDisposed || Disposing) return;
        if (_adb == null || _devices.SelectedItem is not Device device) { ClearDeviceStates("未连接"); return; }
        var revision = _inspectionRevision; var files = CurrentFiles;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); cancellation.CancelAfter(TimeSpan.FromSeconds(60));
        _inspection = cancellation; ClearDeviceStates("检测中…");
        try
        {
            var packages = await Task.Run(() =>
            {
                var values = new Dictionary<string, string?> { ["Unity"] = DeviceInspectionService.UnityPackage, ["Launcher"] = DeviceInspectionService.LauncherPackage };
                foreach (var id in new[] { "Unity", "Launcher" })
                    if (files.GetValueOrDefault(id) is { } path)
                        try { values[id] = ApkMetadata.ReadPackageName(path); } catch { values[id] = null; }
                return values;
            }, cancellation.Token);
            var result = await new DeviceInspectionService(new AdbClient(_adb, Log)).InspectAsync(device.Serial, packages, cancellation.Token);
            if (revision != _inspectionRevision || IsDisposed || Disposing) return;
            _deviceStates = result;
            foreach (var (id, state) in result)
            {
                var label = _choices[id].Device; label.Text = state.Label;
                label.ForeColor = state.Presence switch { Presence.Present => Color.SeaGreen, Presence.Absent => Color.DarkOrange, Presence.Error => Color.Firebrick, _ => Color.Gray };
                _tips.SetToolTip(label, ConfigurationPreview.Tooltip(state.Detail + (state.Preview == null ? "" : "\n\n" + state.Preview)));
            }
        }
        catch (OperationCanceledException) { if (revision == _inspectionRevision && !IsDisposed) ClearDeviceStates("检测已取消"); }
        catch (Exception ex) { if (revision == _inspectionRevision && !IsDisposed) { ClearDeviceStates("检测失败"); Log(ex.Message); } }
        finally { if (ReferenceEquals(_inspection, cancellation)) _inspection = null; cancellation.Dispose(); }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        CancelInspection(); SetBusy(true, "正在检测设备与文件…"); _refreshingDevices = true;
        var previous = (_devices.SelectedItem as Device)?.Serial;
        try
        {
            ReloadSources(); _adb = FindAdb(_root); _devices.Items.Clear(); ClearDeviceStates("未连接");
            _adbStatus.Text = _adb == null ? "ADB：未找到" : "ADB：可用";
            if (_adb == null) { Log("未找到 adb.exe：请把完整 platform-tools 放在程序旁，或添加到 PATH。"); return; }
            var result = await new AdbClient(_adb, Log).RunAsync(null, _lifetime.Token, "devices", "-l");
            foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = Regex.Split(line.Trim(), "\\s+");
                if (fields.Length < 2 || fields[1] != "device") continue;
                var model = fields.FirstOrDefault(s => s.StartsWith("model:"))?[6..] ?? "Android";
                _devices.Items.Add(new Device(fields[0], $"{model} ({fields[0]})"));
            }
            var chosen = _devices.Items.Cast<Device>().FirstOrDefault(d => d.Serial == previous);
            if (chosen != null) _devices.SelectedItem = chosen; else if (_devices.Items.Count == 1) _devices.SelectedIndex = 0;
            Log($"检测到 {_devices.Items.Count} 台在线设备。多设备时请明确选择目标。");
        }
        catch (Exception ex) { Log("检测失败：" + ex.Message); }
        finally { _refreshingDevices = false; SetBusy(false, "准备就绪"); }
        await RefreshDeviceStatusAsync();
    }

    internal static string? FindAdb(string root)
    {
        foreach (var candidate in new[] { Path.Combine(root, "tools", "adb.exe"), Path.Combine(root, "tools", "platform-tools", "adb.exe"), Path.Combine(root, "platform-tools", "adb.exe"), Path.Combine(root, "adb.exe") })
            if (File.Exists(candidate)) return candidate;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            try { var path = Path.Combine(directory.Trim().Trim('"'), "adb.exe"); if (File.Exists(path)) return Path.GetFullPath(path); } catch { }
        return null;
    }

    private async Task DeployAsync()
    {
        if (_busy) return;
        try
        {
            if (_adb == null || _devices.SelectedItem is not Device device) throw new InvalidOperationException("请先连接并选择目标设备。");
            var files = CurrentFiles;
            var selected = _choices.Where(p => p.Value.Overwrite.Checked).Select(p => p.Key).ToHashSet();
            var plan = DeploymentPlan.Create(new ResourceCatalog(_root), Variant, selected, files["Unity"], files["Launcher"],
                files.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value!));
            SaveSelection(); CancelInspection(); ClearDeviceStates("待重新检测"); SetBusy(true, "正在部署，请保持 USB 连接…");
            await new DeploymentService(new AdbClient(_adb, Log), Log).ExecuteAsync(device.Serial, plan, _lifetime.Token);
            Log("选中项目部署成功。"); _status.Text = "部署成功";
        }
        catch (Exception ex) { Log("部署未完成：" + ex.Message); _status.Text = "部署未完成"; MessageBox.Show(this, ex.Message, "部署未完成", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { SetBusy(false, _status.Text); }
        await RefreshDeviceStatusAsync();
    }

    private async Task ExportAsync()
    {
        if (_busy) return;
        if (_adb == null || _devices.SelectedItem is not Device device) { MessageBox.Show(this, "请先选择目标设备。"); return; }
        using var dialog = new FolderBrowserDialog { Description = "选择导出文件夹（会在其中创建独立的 UnityFiles 子目录）", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        CancelInspection(); SetBusy(true, "正在导出 Unity files…");
        try
        {
            var target = await new DeviceInspectionService(new AdbClient(_adb, Log)).ExportUnityFilesAsync(device.Serial, dialog.SelectedPath, _lifetime.Token);
            Log("导出完成：" + target); _status.Text = "导出完成";
            MessageBox.Show(this, "文件已导出到：\n" + target, "导出完成");
        }
        catch (Exception ex) { Log("导出失败：" + ex.Message); _status.Text = "导出失败"; MessageBox.Show(this, ex.Message, "导出失败"); }
        finally { SetBusy(false, _status.Text); }
        await RefreshDeviceStatusAsync();
    }

    private void SetBusy(bool busy, string status)
    {
        if (IsDisposed || Disposing) return;
        _busy = busy; foreach (var control in _locked) control.Enabled = !busy;
        _checkUpdate.Enabled = !_checking; _installUpdate.Enabled = !busy && !_checking && _release != null;
        _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; _status.Text = status;
    }
    private void Log(string text)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { BeginInvoke(() => Log(text)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}"); _log.ScrollToCaret();
    }

    private sealed record SavedSelection(string Variant, string[] Overwrite, Dictionary<string, string>? CustomPaths);
    private void LoadSelection()
    {
        try
        {
            var path = Path.Combine(_root, "deployment-options.json"); if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<SavedSelection>(File.ReadAllText(path)); if (saved == null) return;
            _dl.Checked = saved.Variant == "DL"; _qc.Checked = !_dl.Checked; SetPreset(c => saved.Overwrite.Contains(c.Id));
            if (saved.CustomPaths != null) foreach (var (key, value) in saved.CustomPaths) _customPaths[key] = value;
        }
        catch (Exception ex) { Log("读取部署选项失败：" + ex.Message); }
    }
    private void SaveSelection()
    {
        try { UpdateSettings.AtomicWrite(Path.Combine(_root, "deployment-options.json"), JsonSerializer.Serialize(new SavedSelection(Variant.ToString(), _choices.Where(c => c.Value.Overwrite.Checked).Select(c => c.Key).ToArray(), _customPaths))); }
        catch (Exception ex) { Log("无法保存部署选项：" + ex.Message); }
    }

    internal void RenderPreview(string path)
    {
        ShowInTaskbar = false; Opacity = 0; Show(); Application.DoEvents(); PerformLayout();
        using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path); Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsDisposed)
        {
            _timer.Stop(); _timer.Dispose(); _tips.Dispose();
            CancelInspection(); _lifetime.Cancel();
        }
        base.Dispose(disposing);
    }
}

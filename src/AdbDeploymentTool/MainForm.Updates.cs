namespace AdbDeploymentTool;
internal sealed partial class MainForm
{
    private async Task CheckUpdateAsync(bool manual)
    {
        if (_checking || IsDisposed || Disposing) return;
        if (string.IsNullOrWhiteSpace(_settings.ManifestUrl)) { _updateStatus.Text = "待配置更新地址"; if (manual) EditUpdateSettings(); return; }
        _checking = true; _checkUpdate.Enabled = false; _installUpdate.Enabled = false;
        try
        {
            _updateStatus.Text = "正在检查客户端更新…";
            using var service = new UpdateService(_root, _settings, Log);
            _release = await service.CheckAsync(_lifetime.Token);
            if (IsDisposed || Disposing) return;
            _updateStatus.Text = _release == null ? "已是最新版本（" + service.InstalledVersion() + "）" : "发现新版 " + _release.Manifest.Version;
            if (_release != null) Log("客户端新版 " + _release.Manifest.Version + "：" + _release.Manifest.Notes);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { _release = null; if (!IsDisposed && !Disposing) _updateStatus.Text = "仓库尚无可用发布版本"; }
        catch (Exception ex) { _release = null; if (!IsDisposed && !Disposing) _updateStatus.Text = "检查失败，可稍后重试"; Log("更新检查失败：" + ex.Message); }
        finally { _checking = false; if (!IsDisposed) SetBusy(_busy, _status.Text); }
    }

    private async Task InstallUpdateAsync()
    {
        if (_busy || _release == null) return;
        SetBusy(true, "正在下载并校验客户端更新…");
        try
        {
            using var service = new UpdateService(_root, _settings, Log);
            CancelInspection();
            var stage = await service.DownloadAsync(_release, _lifetime.Token);
            Log("所有文件校验完成，正在退出并应用更新。");
            SaveSelection();
            using var runner = UpdateService.StartUpdater(_root, stage);
            _handoff = true; Close();
        }
        catch (Exception ex) { Log("更新未完成：" + ex.Message); _updateStatus.Text = "更新失败，原版本保留"; }
        finally { if (!_handoff) SetBusy(false, "准备就绪"); }
    }

    private void ResetTimer() { _timer.Interval = Math.Clamp(_settings.CheckIntervalMinutes, 5, 1440) * 60 * 1000; _timer.Start(); }

    private void EditUpdateSettings()
    {
        if (_busy || _checking || IsDisposed || Disposing) return;
        using var dialog = new Form { Text = "客户端更新设置", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(720, 480), Font = Font, MinimizeBox = false, MaximizeBox = false };
        var url = new TextBox { Dock = DockStyle.Fill, Text = _settings.ManifestUrl };
        var key = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = _settings.PublicKeyPem };
        var auto = new CheckBox { Text = "运行期间定时检查（启动时始终异步检查）", Checked = _settings.AutoCheck, AutoSize = true };
        var interval = new NumericUpDown { Minimum = 5, Maximum = 1440, Value = Math.Clamp(_settings.CheckIntervalMinutes, 5, 1440), Width = 85 };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i == 3 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 100 : 0));
        layout.Controls.Add(Label("更新地址（默认使用 YS_ADBDeploymentTools GitHub Releases）"), 0, 0);
        layout.Controls.Add(url, 0, 1); layout.Controls.Add(Label("发布者 RSA 公钥（PEM，可由管理员预配置）"), 0, 2); layout.Controls.Add(key, 0, 3);
        layout.Controls.Add(auto, 0, 4); layout.Controls.Add(Flow(Label("检查间隔（分钟）"), interval), 0, 5);
        layout.Controls.Add(Label("自动检查只提示新版本；点击“下载并更新客户端”才会替换本机文件。"), 0, 6);
        var save = Button("保存");
        save.Click += (_, _) =>
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(url.Text))
                {
                    UpdateService.RequireHttps(ApplicationIdentity.NormalizeUpdateUrl(url.Text));
                    using var rsa = System.Security.Cryptography.RSA.Create(); rsa.ImportFromPem(key.Text.Trim());
                    if (rsa.KeySize < 2048) throw new InvalidDataException("RSA 公钥至少需要 2048 位。");
                    try { rsa.ExportParameters(true); throw new InvalidDataException("客户端只能配置公钥，请勿粘贴私钥。"); }
                    catch (System.Security.Cryptography.CryptographicException) { }
                }
                var settings = new UpdateSettings { AutoCheck = auto.Checked, CheckIntervalMinutes = (int)interval.Value, ManifestUrl = ApplicationIdentity.NormalizeUpdateUrl(url.Text), PublicKeyPem = key.Text.Trim() };
                settings.Save(_root); _settings = settings; _release = null; _installUpdate.Enabled = false;
                _updateStatus.Text = "设置已保存，等待检查"; ResetTimer(); dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "设置未保存"); }
        };
        layout.Controls.Add(Flow(save), 0, 7); dialog.Controls.Add(layout); dialog.ShowDialog(this);
    }

}

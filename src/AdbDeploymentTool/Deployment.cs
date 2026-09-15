using System.Text.Json;

namespace AdbDeploymentTool;

internal enum UnityVariant { QC, DL }
internal enum DeploymentGroup { System, Unity, Launcher }
internal sealed record Component(string Id, string Label, DeploymentGroup Group, string? Target = null)
{
    public const string UnityDirectory = "/sdcard/Android/data/com.horeal.UnityAndroid/files";
    public static readonly Component[] All =
    [
        new("Unity", "Unity APK", DeploymentGroup.Unity),
        new("Launcher", "Launcher APK", DeploymentGroup.Launcher),
        new("libdsp_wrapper.so", "libdsp_wrapper.so", DeploymentGroup.System, "/vendor/lib64/libdsp_wrapper.so"),
        new("libXrSlamInterface.so", "libXrSlamInterface.so", DeploymentGroup.System, "/vendor/lib64/libXrSlamInterface.so"),
        new("YsSlamSensorSettingConfig.yaml", "YsSlamSensorSettingConfig.yaml", DeploymentGroup.System, "/mnt/vendor/persist/calibdata/slam/YsSlamSensorSettingConfig.yaml"),
        new("CameraParametersSY.json", "CameraParametersSY.json", DeploymentGroup.Unity, UnityDirectory + "/CameraParametersSY.json"),
        new("CameraEffect.json", "CameraEffect.json", DeploymentGroup.Unity, UnityDirectory + "/CameraEffect.json")
    ];
}

internal sealed class ResourceCatalog(string directory)
{
    private IEnumerable<string> Directories => new[] { Path.Combine(directory, "tools") }.Where(Directory.Exists);
    public string? Find(string name) => Directories.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    public static bool IsUnity(string path, UnityVariant variant)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase)) return false;
        if (!name.Contains("COMO_UN", StringComparison.OrdinalIgnoreCase) && !name.Contains("unity", StringComparison.OrdinalIgnoreCase)) return false;
        // 型号标识严格区分大小写；同时含 QC 和 DL 的包不作为任一型号候选。
        return name.Contains(variant.ToString(), StringComparison.Ordinal) &&
            !name.Contains(variant == UnityVariant.QC ? "DL" : "QC", StringComparison.Ordinal);
    }
    public IReadOnlyList<string> Apks(UnityVariant? variant) => Directories.SelectMany(d => Directory.EnumerateFiles(d))
        .Where(p => Path.GetExtension(p).Equals(".apk", StringComparison.OrdinalIgnoreCase))
        .Where(p => variant.HasValue ? IsUnity(p, variant.Value) :
            Path.GetFileName(p).Contains("COMO_LA", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(p).Contains("XRLaunche", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
}

internal sealed record DeploymentItem(Component Component, string LocalPath);
internal sealed record DeploymentPlan(UnityVariant Variant, IReadOnlyList<DeploymentItem> Items)
{
    public static DeploymentPlan Create(ResourceCatalog catalog, UnityVariant variant, ISet<string> selected,
        string? unityApk, string? launcherApk, IReadOnlyDictionary<string, string>? customFiles = null)
    {
        var items = new List<DeploymentItem>();
        foreach (var component in Component.All.Where(c => selected.Contains(c.Id)))
        {
            var path = component.Id switch { "Unity" => unityApk, "Launcher" => launcherApk,
                _ => customFiles != null && customFiles.TryGetValue(component.Id, out var custom) ? custom : catalog.Find(component.Id) };
            if (path == null || !File.Exists(path)) throw new FileNotFoundException(component.Id == "Unity"
                ? $"没有选定 {variant}（{(variant == UnityVariant.QC ? "全彩" : "单绿")}）Unity APK。请将对应大写型号 APK 放入 tools，并在列表中选择。"
                : $"缺少或未选择：{component.Label}。请检查 tools 目录。已跳过的项目无需提供文件。");
            if (component.Id == "Unity" && !ResourceCatalog.IsUnity(path, variant)) throw new InvalidOperationException("Unity APK 与当前 QC/DL 选择不匹配。");
            var extension = component.Target == null ? ".apk" : Path.GetExtension(component.Id);
            if (!Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(component.Label + " 的文件类型应为 " + extension);
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
            }
            if (new FileInfo(path).Length == 0) throw new InvalidDataException($"文件为空：{component.Label}");
            items.Add(new(component, path));
        }
        if (items.Count == 0) throw new InvalidOperationException("当前范围内没有选择任何覆盖项目。请至少选择一项“覆盖”。");
        return new(variant, items);
    }
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError = "")
{
    // 两路原始文本分别保存；协议字段仅从 StandardOutput 解析。
    public string Output => string.IsNullOrEmpty(StandardError) ? StandardOutput :
        string.IsNullOrEmpty(StandardOutput) ? StandardError : StandardOutput.TrimEnd('\r', '\n') + Environment.NewLine + StandardError;
}

internal sealed class AdbCommandException(string command, CommandResult result)
    : InvalidOperationException($"ADB 命令未正常结束：{command}；Process.ExitCode={result.ExitCode} (0x{unchecked((uint)result.ExitCode):X8})。" +
        "\n失败依据为进程退出码，stderr 内容本身不决定成败。\nstdout:\n" +
        (result.StandardOutput.Length == 0 ? "<空>" : result.StandardOutput) + "\nstderr:\n" +
        (result.StandardError.Length == 0 ? "<空>" : result.StandardError))
{
    public CommandResult Result { get; } = result;
}
internal interface IAdbClient
{
    Task<CommandResult> RunAsync(string? serial, CancellationToken token, params string[] arguments);
    Task EnsureRootAndRemountAsync(string serial, CancellationToken token);
    Task EnsureRootAsync(string serial, CancellationToken token);
}

internal sealed class DeploymentService(IAdbClient adb, Action<string> log)
{
    public async Task ExecuteAsync(string serial, DeploymentPlan plan, CancellationToken token)
    {
        log($"目标设备：{serial}；Unity 型号：{plan.Variant}；覆盖项目：{string.Join("、", plan.Items.Select(x => x.Component.Label))}");
        foreach (var c in Component.All.Where(c => plan.Items.All(i => i.Component.Id != c.Id))) log("跳过：" + c.Label);
        var system = plan.Items.Where(x => x.Component.Group == DeploymentGroup.System).ToArray();
        if (system.Length > 0)
        {
            await adb.EnsureRootAndRemountAsync(serial, token);
            foreach (var item in system) await PushAsync(serial, item, true, token);
        }
        foreach (var item in plan.Items.Where(x => x.Component.Group == DeploymentGroup.Unity))
        {
            if (item.Component.Id == "Unity") await InstallAsync(serial, item.LocalPath, token);
            else await PushAsync(serial, item, false, token);
        }
        var launcher = plan.Items.FirstOrDefault(x => x.Component.Id == "Launcher");
        if (launcher != null)
        {
            await InstallAsync(serial, launcher.LocalPath, token);
            await adb.EnsureRootAndRemountAsync(serial, token);
            log("备份系统 Launcher3QuickStep；已有备份保持不变。");
            await adb.RunAsync(serial, token, "shell", "cd /system_ext/priv-app/Launcher3QuickStep && " +
                "{ if [ -f Launcher3QuickStep.apk ] && [ ! -e Launcher3QuickStep.apk.bak ]; then mv Launcher3QuickStep.apk Launcher3QuickStep.apk.bak || exit 1; fi; } && " +
                "{ if [ -d oat ] && [ ! -e oat_bak ]; then mv oat oat_bak || exit 1; fi; } && sync");
            await adb.RunAsync(serial, token, "reboot");
            log("已发送设备重启命令；请等待设备启动后验收。");
        }
        else await adb.RunAsync(serial, token, "shell", "sync");
    }

    private async Task InstallAsync(string serial, string path, CancellationToken token)
    {
        log("覆盖安装（保留应用数据）：" + Path.GetFileName(path));
        var result = await adb.RunAsync(serial, token, "install", "-r", path);
        if (!result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(l => l.Trim() == "Success"))
            throw new InvalidOperationException("APK 安装没有返回 Success：" + result.Output);
    }

    private async Task PushAsync(string serial, DeploymentItem item, bool permissions, CancellationToken token)
    {
        var target = item.Component.Target!;
        var directory = target[..target.LastIndexOf('/')];
        // 先完整写入同目录临时文件，再替换，避免传输中断截断原配置或库。
        var staging = target + ".deploy-" + Guid.NewGuid().ToString("N");
        log("替换：" + item.Component.Label + " → " + target);
        await adb.RunAsync(serial, token, "shell", "mkdir -p " + directory);
        try
        {
            await adb.RunAsync(serial, token, "push", item.LocalPath, staging);
            var bytes = new FileInfo(item.LocalPath).Length;
            await adb.RunAsync(serial, token, "shell", $"test \"$(wc -c < {staging})\" -eq {bytes}" +
                (permissions ? $" && chmod 0644 {staging}" : "") + $" && mv -f {staging} {target} && sync");
        }
        catch
        {
            try { await adb.RunAsync(serial, CancellationToken.None, "shell", "rm -f " + staging); } catch { }
            throw;
        }
    }
}

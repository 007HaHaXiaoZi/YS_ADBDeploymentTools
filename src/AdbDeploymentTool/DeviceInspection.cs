using System.Text;
using System.Text.RegularExpressions;

namespace AdbDeploymentTool;

internal enum Presence { Unknown, Present, Absent, Error }
internal sealed record DeviceComponentState(Presence Presence, string Label, string Detail, string? Preview = null);

internal static class ConfigurationPreview
{
    public const int Limit = 64 * 1024;
    public static bool IsConfig(Component component) => component.Id.EndsWith(".json") || component.Id.EndsWith(".yaml");
    public static string ReadLocal(string path)
    {
        using var stream = File.OpenRead(path);
        var data = new byte[(int)Math.Min(stream.Length, Limit + 1)]; stream.ReadExactly(data);
        var text = Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(data.Length, Limit)));
        return data.Length > Limit ? text + "\n\n[预览截断：文件超过 64 KiB]" : text;
    }
    public static string Tooltip(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var clipped = lines.Length > 8;
        var preview = lines.Take(8).Select(line =>
        {
            line = line.Replace("\t", "  ");
            if (line.Length <= 50) return line;
            clipped = true;
            return line[..49] + "…";
        }).ToArray();
        return string.Join("\n", preview) + (clipped ? "\n… 双击名称或设备状态查看完整预览" : "");
    }
}

internal sealed class DeviceInspectionService(IAdbClient adb)
{
    public const string UnityPackage = "com.horeal.UnityAndroid";
    // 从本项目当前 Launcher APK 的真实清单读取；自定义 APK 会覆盖此默认包名。
    public const string LauncherPackage = "com.horeal.xrlauncher";

    public async Task<IReadOnlyDictionary<string, DeviceComponentState>> InspectAsync(string serial,
        IReadOnlyDictionary<string, string?> packages, CancellationToken token)
    {
        var result = new Dictionary<string, DeviceComponentState>();
        var rootAttempted = false;
        string? rootError = null;
        async Task<CommandResult> ReadFileAsync(string command)
        {
            try { return await adb.RunAsync(serial, token, "shell", command); }
            catch (AdbCommandException ex) when (ex.Result.Output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                if (!rootAttempted)
                {
                    rootAttempted = true;
                    try { await adb.EnsureRootAsync(serial, token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { rootError = error.Message; }
                }
                if (rootError != null) throw new UnauthorizedAccessException("权限不足；获取 root 失败：" + rootError + "\n" + ex.Message, ex);
                try { return await adb.RunAsync(serial, token, "shell", command); }
                catch (AdbCommandException retry) when (retry.Result.Output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                { throw new UnauthorizedAccessException("权限不足：已验证 root，设备仍拒绝访问。\n" + retry.Message, retry); }
            }
        }
        HashSet<string>? installed = null;
        string? packageError = null;
        try
        {
            var list = await adb.RunAsync(serial, token, "shell", "pm list packages");
            installed = list.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("package:", StringComparison.Ordinal)).Select(l => l[8..].Trim()).ToHashSet(StringComparer.Ordinal);
            // pm 查询出错时部分固件会返回 0，但输出只有错误文案，不能把所有应用误标为未安装。
            if (installed.Count == 0) packageError = "包管理器没有返回有效应用列表，无法确认安装状态。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { packageError = ex.Message; }
        foreach (var component in Component.All)
        {
            token.ThrowIfCancellationRequested();
            if (component.Target == null)
            {
                var package = packages.GetValueOrDefault(component.Id);
                result[component.Id] = string.IsNullOrEmpty(package) ? new(Presence.Unknown, "包名未知", "请选择有效 APK，以读取真实包名。") :
                    packageError != null ? new(Presence.Error, "检测失败", packageError) :
                    installed!.Contains(package) ? new(Presence.Present, "已安装", "包名：" + package) : new(Presence.Absent, "未安装", "包名：" + package);
                continue;
            }
            try
            {
                var file = await ReadFileAsync("ls -ld " + ShellQuote(component.Target));
                if (string.IsNullOrWhiteSpace(file.StandardOutput))
                    result[component.Id] = new(Presence.Error, "检测失败", "设备没有返回文件信息。");
                else if (file.StandardOutput.TrimStart().StartsWith('d'))
                    result[component.Id] = new(Presence.Error, "不是文件", component.Target + " 实际是目录。");
                else
                {
                    string? preview = null;
                    var detail = component.Target + "\n" + file.StandardOutput.TrimEnd();
                    if (ConfigurationPreview.IsConfig(component))
                    {
                        try
                        {
                            var read = await ReadFileAsync($"head -c {ConfigurationPreview.Limit + 1} < {ShellQuote(component.Target)}");
                            preview = read.StandardOutput;
                            if (Encoding.UTF8.GetByteCount(preview) > ConfigurationPreview.Limit) preview += "\n\n[预览截断：文件超过 64 KiB]";
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { detail += "\n内容读取失败：" + ex.Message; }
                    }
                    result[component.Id] = new(Presence.Present, "已存在", detail, preview);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException ex) { result[component.Id] = new(Presence.Error, "权限不足", component.Target + "\n" + ex.Message); }
            catch (AdbCommandException ex) when (ex.Result.Output.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
                && !ex.Result.Output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            { result[component.Id] = new(Presence.Absent, "不存在", component.Target); }
            catch (Exception ex) { result[component.Id] = new(Presence.Error, "检测失败", ex.Message); }
        }
        return result;
    }

    public async Task<string> ExportUnityFilesAsync(string serial, string destinationParent, CancellationToken token)
    {
        await adb.RunAsync(serial, token, "shell", "test -d " + ShellQuote(Component.UnityDirectory) + " && ls -ld " + ShellQuote(Component.UnityDirectory));
        var parent = Path.GetFullPath(destinationParent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("导出目标文件夹不存在。");
        var safeSerial = Regex.Replace(serial, @"[^A-Za-z0-9_.-]", "_");
        if (safeSerial.Length > 64) safeSerial = safeSerial[..64];
        // 每次新建导出目录，防止覆盖用户文件；设备端仅执行读取命令。
        var target = Path.Combine(parent, $"UnityFiles-{safeSerial}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        try { await adb.RunAsync(serial, token, "pull", Component.UnityDirectory + "/.", target); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new IOException("导出未完成，已下载的部分文件保留在：" + target, ex); }
        return target;
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

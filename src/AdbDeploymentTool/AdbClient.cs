using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbDeploymentTool;

internal sealed class AdbClient(string adbPath, Action<string> log, TimeSpan? commandTimeout = null) : IAdbClient
{
    internal const string BinderWarning = "Binder ioctl to enable oneway spam detection failed: Invalid argument";
    private readonly string _adbPath = Path.GetFullPath(adbPath);

    public async Task EnsureRootAndRemountAsync(string serial, CancellationToken token)
    {
        await EnsureRootAsync(serial, token);
        if (RemountNeedsReboot((await RunRemountAsync(serial, token)).Output))
        {
            log("remount 提示首次启用需要重启，正在自动重启设备...");
            await RunAsync(serial, token, "reboot");
            await WaitForRebootAsync(serial, token);
            await EnsureRootAsync(serial, token);
            if (RemountNeedsReboot((await RunRemountAsync(serial, token)).Output))
                throw new InvalidOperationException("设备重启后 remount 仍要求重启，请检查 verity/overlayfs 状态后重试。");
        }
    }

    private static bool IsRoot(CommandResult result) => result.ExitCode == 0 &&
        Regex.IsMatch(result.StandardOutput, @"(?:^|\s)uid=0(?:\(|\s|$)");

    public async Task EnsureRootAsync(string serial, CancellationToken token)
    {
        // root 不等于 remount 成功，但已有 root 时无需重复重启 adbd。
        var before = await RunRawAsync(serial, token, "shell", "id");
        if (IsRoot(before)) { log("设备已经具有 root 权限（uid=0），跳过 adb root。"); return; }
        var root = await RunRawAsync(serial, token, "root");
        if (ContainsAny(root.Output, "cannot run as root", "adbd cannot run as root"))
            throw new InvalidOperationException("设备不允许 adb root，无法获取受保护文件的访问权限。");
        if (root.ExitCode != 0)
            log($"adb root 的真实退出码为 {root.ExitCode}；等待设备上线后独立验证 uid，不把该退出码改写为 0。");
        await WaitForDeviceAsync(serial, token);
        var after = await RunRawAsync(serial, token, "shell", "id");
        if (!IsRoot(after)) throw new InvalidOperationException("root 身份验证失败：shell id 未正常返回 uid=0。\n" + after.Output);
        log("root 身份验证通过（uid=0）。");
    }

    internal async Task<CommandResult> RunRemountAsync(string serial, CancellationToken token)
    {
        var result = await RunRawAsync(serial, token, "remount");
        if (result.ExitCode == 0)
        {
            log(result.StandardOutput.Contains("remount succeeded", StringComparison.OrdinalIgnoreCase)
                ? "remount 成功：Process.ExitCode=0，并收到 remount succeeded。"
                : "remount 成功：Process.ExitCode=0；不以 stderr 非空或缺少成功文案判为失败。");
            return result;
        }
        // 保留原工具的“明确要求重启”流程：这不是 remount 成功，重启后仍会重新执行并检查。
        if (RemountNeedsReboot(result.Output))
        {
            log($"remount 返回 {result.ExitCode} 并明确要求重启，尚未判定成功，重启后重新检查。");
            return result;
        }
        if (result.ExitCode == -1)
            log("ADB Diagnostic: -1 来自 adb.exe 的实际 Process.ExitCode（0xFFFFFFFF），不是 stderr、超时或 C# 异常占位值。当前 remount 结果无法确认。");
        if (result.StandardOutput.Contains("remount succeeded", StringComparison.OrdinalIgnoreCase))
            log("ADB Diagnostic: 成功文案与非零退出码冲突，仍按真实退出码报告异常，不静默忽略。");
        throw new AdbCommandException("remount", result);
    }

    private async Task WaitForDeviceAsync(string serial, CancellationToken token)
    {
        log("等待设备上线（最长 120 秒）...");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            while (true)
            {
                var state = await RunRawAsync(serial, deadline.Token, "get-state");
                if (IsOnline(state)) { log("设备已上线。"); return; }
                await Task.Delay(1500, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待设备重新连接超时（120 秒）。"); }
    }

    private async Task WaitForRebootAsync(string serial, CancellationToken token)
    {
        log("等待设备离线并重新上线（最长 120 秒）...");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        bool offline = false;
        try
        {
            while (true)
            {
                var state = await RunRawAsync(serial, deadline.Token, "get-state");
                if (!IsOnline(state)) offline = true;
                else if (offline) { log("设备已重新上线。"); return; }
                await Task.Delay(1500, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("等待设备重启超时（120 秒）。"); }
    }

    internal static bool IsOnline(CommandResult result) => result.ExitCode == 0 && result.StandardOutput.Trim() == "device";

    public async Task<CommandResult> RunAsync(string? serial, CancellationToken token, params string[] arguments)
    {
        var result = await RunRawAsync(serial, token, arguments);
        if (result.ExitCode != 0) throw new AdbCommandException(string.Join(" ", arguments.Select(FormatArgumentForLog)), result);
        return result;
    }

    internal async Task<CommandResult> RunRawAsync(string? serial, CancellationToken token, params string[] arguments)
    {
        token.ThrowIfCancellationRequested();
        var duration = commandTimeout ?? TimeSpan.FromSeconds(arguments.FirstOrDefault() == "pull" ? 1800 : arguments.FirstOrDefault() is "install" or "push" ? 300 : 20);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(duration);
        using var drain = new CancellationTokenSource();
        var start = new ProcessStartInfo(_adbPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(serial)) { start.ArgumentList.Add("-s"); start.ArgumentList.Add(serial); }
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        log("ADB executable: " + _adbPath);
        log("> adb " + string.Join(" ", start.ArgumentList.Select(FormatArgumentForLog)));
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log("ADB HostError: 无法启动 adb.exe；没有可用的 Process.ExitCode。" + ex.Message);
            throw new InvalidOperationException("无法启动 ADB（不是设备命令退出失败）：" + _adbPath, ex);
        }

        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        // 两路同时读取，直到 EOF。进程退出和两路读取全部完成后，才构造正常结果。
        var readOut = CaptureAsync(process.StandardOutput, stdout, drain.Token);
        var readErr = CaptureAsync(process.StandardError, stderr, drain.Token);
        var readers = Task.WhenAll(readOut, readErr);
        try
        {
            // 本工具只执行非交互命令；显式 EOF 防止 WinExe 继承不存在或不合适的 stdin。
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(), readers).WaitAsync(deadline.Token);
            var result = new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
            LogStreams(result.StandardOutput, result.StandardError);
            log($"ADB Process: PID={process.Id}; Process.ExitCode={result.ExitCode} (0x{unchecked((uint)result.ExitCode):X8}); stdout={result.StandardOutput.Length} chars; stderr={result.StandardError.Length} chars");
            return result;
        }
        catch (Exception ex)
        {
            // 超时、取消、读取异常单独报告，不伪造 -1。先终止本次子进程，再尽量排空已捕获输出。
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception stop) { log("ADB HostError: 终止子进程失败：" + stop.Message); }
            drain.CancelAfter(TimeSpan.FromSeconds(2));
            try { await readers; } catch { }
            LogStreams(stdout.ToString(), stderr.ToString());
            if (ex is OperationCanceledException)
            {
                if (token.IsCancellationRequested)
                {
                    log("ADB Cancelled: 操作已取消；保留已捕获输出，不生成命令退出码。");
                    throw new OperationCanceledException("ADB 操作已取消。", ex, token);
                }
                log($"ADB Timeout: 超过 {duration.TotalSeconds:g} 秒；保留已捕获输出，不生成命令退出码。");
                throw new TimeoutException($"ADB 执行或输出读取超时（{duration.TotalSeconds:g} 秒），不能判定命令成功。", ex);
            }
            log("ADB HostError: 进程输出读取或管理异常；不生成命令退出码。" + ex.Message);
            throw new IOException("ADB 进程输出读取或管理异常。", ex);
        }
    }

    private static async Task CaptureAsync(StreamReader reader, StringBuilder output, CancellationToken token)
    {
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0) output.Append(buffer, 0, count);
    }

    private void LogStreams(string stdout, string stderr)
    {
        foreach (var (name, text) in new[] { ("stdout", stdout), ("stderr", stderr) })
        {
            if (text.Length == 0) { log("ADB " + name + ": <空>"); continue; }
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
                log(line.Contains(BinderWarning, StringComparison.Ordinal)
                    ? "ADB Warning: " + line + " [" + name + "]"
                    : "ADB " + name + ": " + line);
        }
    }

    private static bool RemountNeedsReboot(string output) => output.Contains("reboot", StringComparison.OrdinalIgnoreCase) &&
        ContainsAny(output, "required", "needed", "take effect", "first time", "reboot your device");
    private static bool ContainsAny(string value, params string[] needles) => needles.Any(n => value.Contains(n, StringComparison.OrdinalIgnoreCase));
    private static string FormatArgumentForLog(string value) => value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;
}

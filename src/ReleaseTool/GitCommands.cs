using System.Diagnostics;

namespace ReleaseTool;

internal static class GitCommands
{
    public static async Task<string> Run(string root, params string[] args) => await Execute(root, null, args);

    private static async Task<string> Execute(string root, string? input, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Git，请先安装 Git for Windows。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (input != null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new IOException("Git 操作超时，请检查网络和登录状态。"); }
        var output = await stdout;
        await stderr; // Credential-helper diagnostics may contain credentials; never print them.
        if (process.ExitCode != 0) throw new IOException("Git 操作失败（退出码 " + process.ExitCode + "），请检查仓库、HTTPS 登录和远程权限；推送失败时本地提交仍保留。");
        return output.TrimEnd('\r', '\n');
    }

    public static async Task<string> Token(string root)
    {
        var result = await Execute(root, "protocol=https\nhost=github.com\n\n", "-c", "credential.interactive=never", "credential", "fill");
        var token = result.Split('\n').FirstOrDefault(line => line.StartsWith("password="))?[9..].TrimEnd('\r');
        return string.IsNullOrWhiteSpace(token) ? throw new IOException("没有 GitHub HTTPS 凭据，请先运行登录命令。") : token;
    }

    public static async Task<string> ValidateRepository(string root, string repository)
    {
        var top = Path.GetFullPath(await Run(root, "rev-parse", "--show-toplevel"));
        if (!string.Equals(top.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("--root 必须指向 Git 仓库根目录。");
        var remote = await Run(root, "remote", "get-url", "--push", "origin");
        if (remote != "https://github.com/" + repository + ".git" && remote != "https://github.com/" + repository &&
            remote != "git@github.com:" + repository + ".git") throw new IOException("origin 推送地址与指定 GitHub 仓库不一致。");
        var branch = await Run(root, "symbolic-ref", "--short", "HEAD");
        if (string.IsNullOrWhiteSpace(branch)) throw new IOException("请先切换到要推送的分支。");
        return branch;
    }

    public static void ValidateFile(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Directory.Exists(path))
            throw new IOException("提交文件必须是仓库内的具体文件，不能是目录：" + relative);
        var parts = Path.GetRelativePath(root, path).Replace('\\', '/').Split('/');
        string[] excluded = [".git", ".build", ".release-private", ".ssh", ".updates", "tools", "bin", "obj"];
        if (parts.Any(p => excluded.Contains(p, StringComparer.OrdinalIgnoreCase)) ||
            new[] {"publisher-private.pem", "deployment-options.json", "installed-version.txt", "update-settings.json"}.Contains(parts[^1], StringComparer.OrdinalIgnoreCase) ||
            new[] {".pfx", ".p12", ".key", ".ppk"}.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new IOException("不允许提交私钥、本地资源或运行数据：" + relative);
        for (var current = path; current != null && current.Length >= prefix.Length; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("提交路径不能包含符号链接：" + relative);
        if (File.Exists(path) && new FileInfo(path).Length < 1024 * 1024 && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), @"(?m)^-----BEGIN (?:RSA |EC |ENCRYPTED )?PRIVATE KEY-----"))
            throw new IOException("所选文件包含私钥，拒绝提交：" + relative);
    }

    public static async Task Upload(string root, string repository, string message, string[] files)
    {
        if (files.Length == 0 || string.IsNullOrWhiteSpace(message)) throw new ArgumentException("请提供提交说明及具体文件列表。");
        var branch = await ValidateRepository(root, repository);
        if (!string.IsNullOrWhiteSpace(await Run(root, "diff", "--cached", "--name-only")))
            throw new IOException("暂存区已有内容，请先完成或取消已有暂存，避免混入本次提交。");
        foreach (var file in files) ValidateFile(root, file);
        await Run(root, new[] { "--literal-pathspecs", "add", "--" }.Concat(files).ToArray());
        Console.WriteLine("本次提交文件：\n" + await Run(root, "diff", "--cached", "--stat"));
        await Run(root, "commit", "-m", message);
        await Run(root, "push", "origin", "HEAD:refs/heads/" + branch);
        Console.WriteLine("已提交并推送：" + await Run(root, "rev-parse", "--short", "HEAD"));
    }
}

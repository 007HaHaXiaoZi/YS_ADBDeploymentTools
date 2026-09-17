using AdbDeploymentTool;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ReleaseTool;

internal static class ReleaseWorkflow
{
    public static string[] Menu()
    {
        Console.WriteLine("YS_ADBReleaseTool 发布工具\n1. 生成本地签名更新包\n2. 推送已有提交并上传发布 Release\n3. 选择文件、提交并推送 Git\n4. 查看命令帮助\n0. 退出");
        var choice = Ask("请选择", "0");
        if (choice == "0") return ["exit"];
        if (choice == "4") return ["--help"];
        if (choice is not ("1" or "2" or "3")) throw new ArgumentException("无效选项。");
        var root = Ask("仓库根目录", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (choice == "3")
        {
            Console.WriteLine("每次只提交所选文件。输入相对于仓库的具体文件路径，多个文件用 | 分隔。");
            var message = Ask("提交说明");
            var files = Ask("文件路径").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new[] { "git-upload", "--root", root, "--message", message }.Concat(files.SelectMany(f => new[] { "--file", f })).ToArray();
        }
        var version = Ask("本次版本（须与已构建客户端一致）");
        var notes = Ask("更新说明", "更新 YS_ADBDeploymentTools");
        var args = new List<string> { "release", version, "--root", root, "--notes", notes };
        if (choice == "2") args.AddRange(["--upload", "--push"]);
        return args.ToArray();
    }

    private static string Ask(string label, string fallback = "")
    {
        Console.Write(label + (fallback.Length > 0 ? " [" + fallback + "]" : "") + "：");
        var value = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == "exit") return 0;
        if (args.Length > 0 && args[0] is "keygen" or "publish" or "publish-github") return PackageCommands.Run(args);
        if (args.Length == 0 || args[0] is "--help" or "help" or "-h")
        {
            Console.WriteLine("双击本程序可打开交互菜单。\n" +
                "YS_ADBReleaseTool release <版本> [--root <仓库目录>] [--notes <说明>] [--output <新目录>] [--key <私钥>] [--repository <owner/repo>] [--upload] [--push]\n" +
                "  默认只打包；--upload 上传并发布；--push 在上传前推送当前分支已有提交（须同时指定 --upload）。\n" +
                "YS_ADBReleaseTool git-upload --message <提交说明> --file <具体文件> [--file <其他文件>] [--root <仓库目录>] [--repository <owner/repo>]\n" +
                "  只暂存所选文件，提交后推送当前分支；如已有暂存内容则停止。\n" +
                "保留 keygen、publish、publish-github 本地打包命令。\n" +
                "需要安装 Git for Windows，并先登录：git credential-manager github login --url https://github.com --username 007HaHaXiaoZi\n" +
                "客户端需提前构建；版本必须递增。发布功能不依赖 PowerShell 或 GitHub CLI。");
            return 0;
        }
        if (args[0] is not ("release" or "git-upload")) throw new ArgumentException("未知命令，请运行 --help。");
        bool releasing = args[0] == "release";
        if (releasing && (args.Length < 2 || !Version.TryParse(args[1], out _))) throw new ArgumentException("请提供有效版本号。");
        var values = new Dictionary<string, string>();
        var files = new List<string>();
        var flags = new HashSet<string>();
        var allowed = releasing ? new[] { "--root", "--notes", "--output", "--key", "--repository" } : new[] { "--root", "--message", "--repository", "--file" };
        for (var i = releasing ? 2 : 1; i < args.Length; i++)
        {
            var name = args[i];
            if (releasing && name is "--upload" or "--push") { if (!flags.Add(name)) throw new ArgumentException("重复选项：" + name); continue; }
            if (!allowed.Contains(name) || i + 1 >= args.Length) throw new ArgumentException("未知或缺少参数的选项：" + name);
            var value = args[++i];
            if (name == "--file") files.Add(value);
            else if (!values.TryAdd(name, value)) throw new ArgumentException("重复选项：" + name);
        }
        var root = Path.GetFullPath(values.GetValueOrDefault("--root", AppContext.BaseDirectory));
        var repository = values.GetValueOrDefault("--repository", ApplicationIdentity.Repository);
        if (!Regex.IsMatch(repository, "\\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+\\z")) throw new ArgumentException("仓库格式必须是 owner/repo。");
        if (!releasing)
        {
            await GitCommands.Upload(root, repository, values.GetValueOrDefault("--message", ""), files.ToArray());
            return 0;
        }
        var version = Version.Parse(args[1]).ToString();
        var upload = flags.Contains("--upload");
        if (flags.Contains("--push") && !upload) throw new ArgumentException("--push 必须与 --upload 一起使用。");
        var key = Path.GetFullPath(values.GetValueOrDefault("--key", Path.Combine(root, ".release-private", "publisher-private.pem")));
        var output = Path.GetFullPath(values.GetValueOrDefault("--output", Path.Combine(root, ".build", "releases", version + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6])));
        var notes = values.GetValueOrDefault("--notes", "更新 YS_ADBDeploymentTools");
        ValidateClient(root, version);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(key));
        using var trusted = RSA.Create();
        trusted.ImportFromPem(ApplicationIdentity.PublisherPublicKey);
        if (!rsa.ExportSubjectPublicKeyInfo().SequenceEqual(trusted.ExportSubjectPublicKeyInfo())) throw new IOException("发布私钥与内置客户端公钥不匹配。");
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("输出目录已存在，请指定新目录。");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("YS-ADBReleaseTool/2.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        var publisher = new GitHubPublisher(http, repository);
        string commit = "", branch = "";
        if (upload)
        {
            branch = await GitCommands.ValidateRepository(root, repository);
            if (!string.IsNullOrWhiteSpace(await GitCommands.Run(root, "status", "--porcelain", "--untracked-files=no")))
                throw new IOException("存在未提交的已跟踪文件，请先用 git-upload 提交所需内容后再发布。");
            await GitCommands.Run(root, "ls-files", "--error-unmatch", "--", UpdateService.ExeName);
            commit = await GitCommands.Run(root, "rev-parse", "HEAD");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GitCommands.Token(root));
            await publisher.Preflight(version);
        }
        // Keep the packaging input separate: the default output is inside the repository.
        var source = Path.Combine(root, ".build", "release-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.Copy(Path.Combine(root, UpdateService.ExeName), Path.Combine(source, UpdateService.ExeName));
        if (PackageCommands.Run(["publish-github", source, output, repository, version, key, notes]) != 0) return 1;
        GitHubPublisher.ValidateAssets(output, repository, version, ApplicationIdentity.PublisherPublicKey);
        if (!upload) { Console.WriteLine("本地打包完成。使用 --upload 可上传并发布。"); return 0; }
        if (flags.Contains("--push")) await GitCommands.Run(root, "push", "origin", "HEAD:refs/heads/" + branch);
        var remote = await GitCommands.Run(root, "ls-remote", "--exit-code", "https://github.com/" + repository + ".git", "refs/heads/" + branch);
        if (remote.Split('\t')[0] != commit) throw new IOException("GitHub 分支尚未同步到本地提交，请加 --push 或先推送。");
        await publisher.Upload(output, version, notes, commit, ApplicationIdentity.PublisherPublicKey);
        return 0;
    }

    internal static void ValidateClient(string root, string version)
    {
        var exe = Path.Combine(root, UpdateService.ExeName);
        if (!File.Exists(exe)) throw new FileNotFoundException("缺少客户端 EXE，请先构建。", exe);
        var fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        if (!Version.TryParse(fileVersion, out var actual)) throw new IOException("无法读取客户端版本号，请重新构建。");
        var desired = Version.Parse(version);
        if (actual.Major != desired.Major || actual.Minor != desired.Minor || Math.Max(0, actual.Build) != Math.Max(0, desired.Build) || Math.Max(0, actual.Revision) != Math.Max(0, desired.Revision))
            throw new IOException($"客户端版本 {actual} 与发布版本 {version} 不一致，请先修改版本并构建。");
    }
}

using AdbDeploymentTool;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PackageCommands
{
public static int Run(string[] args)
{
try
{
    if (args.Length == 2 && args[0] == "keygen")
    {
        var directory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(directory);
        var privatePath = Path.Combine(directory, "publisher-private.pem");
        var publicPath = Path.Combine(directory, "publisher-public.pem");
        if (File.Exists(privatePath) || File.Exists(publicPath)) throw new IOException("密钥文件已存在，拒绝覆盖。升级时必须继续使用原发布密钥。");
        using var rsa = RSA.Create(3072);
        File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(publicPath, rsa.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine("已生成密钥。私钥仅由发布者保存，禁止放到网站或客户端；公钥配置到客户端。\n" + publicPath);
        return 0;
    }
    if (args.Length is 6 or 7 && args[0] is "publish" or "publish-github")
    {
        var source = Path.GetFullPath(args[1]);
        var output = Path.GetFullPath(args[2]);
        var version = Version.Parse(args[4]).ToString();
        var github = args[0] == "publish-github";
        if (github && !System.Text.RegularExpressions.Regex.IsMatch(args[3], "\\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+\\z"))
            throw new InvalidDataException("GitHub 仓库请填写 owner/repo。");
        var baseUrl = UpdateService.RequireHttps(github ? $"https://github.com/{args[3]}/releases/download/v{version}/" : args[3].TrimEnd('/') + "/");
        var keyPath = Path.GetFullPath(args[5]);
        if (Directory.Exists(output)) throw new IOException("输出目录已存在。请为每次发布指定新的目录，避免混入旧文件。");
        if (output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("发布输出目录不能位于客户端目录内。");
        if (!File.Exists(Path.Combine(source, UpdateService.ExeName))) throw new FileNotFoundException("客户端目录中缺少 " + UpdateService.ExeName);
        using var rsa = RSA.Create(); rsa.ImportFromPem(File.ReadAllText(keyPath));
        if (rsa.KeySize < 2048) throw new InvalidDataException("RSA 密钥至少需要 2048 位。");
        var files = new List<ReleaseFile>();
        // 仓库及 Releases 都不发布 tools；资源由客户在本地维护或选择自定义路径。
        var candidates = new[] { Path.Combine(source, UpdateService.ExeName) };
        // 每个版本使用独立资源 URL；只在所有文件上传后切换 manifest.json 和签名。
        var contentPrefix = "files/" + version + "/";
        Directory.CreateDirectory(output);
        foreach (var path in candidates)
        {
            var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            if (!UpdateService.AllowedPath(relative)) { Console.WriteLine("不发布：" + relative); continue; }
            // 使用 ASCII 资产名，避免 GitHub 对中文文件名重命名；实际客户端路径仍由签名清单保存。
            var assetName = relative == UpdateService.ExeName ? UpdateService.ExeName : "asset-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative))).ToLowerInvariant()[..24] + Path.GetExtension(path);
            var uploadPath = github ? assetName : contentPrefix + relative;
            var target = Path.Combine(output, uploadPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
            using var stream = File.OpenRead(target);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var url = new Uri(baseUrl, string.Join('/', uploadPath.Split('/').Select(Uri.EscapeDataString))).AbsoluteUri;
            files.Add(new(relative, url, stream.Length, hash));
            Console.WriteLine("已打包：" + relative);
        }
        var manifest = new ReleaseManifest(UpdateService.Product, version, args.Length == 7 ? args[6] : "", files);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateSettings.Json);
        var signature = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        UpdateService.Verify(bytes, signature, rsa.ExportSubjectPublicKeyInfoPem());
        File.WriteAllBytes(Path.Combine(output, "manifest.json"), bytes);
        File.WriteAllBytes(Path.Combine(output, "manifest.json.sig"), signature);
        Console.WriteLine("发布文件已生成：" + output + (github
            ? $"\n请将目录内所有文件上传到 {args[3]} 的 v{version} Release，全部上传后再发布为 Latest。\n客户端更新地址：https://github.com/{args[3]}/releases/latest/download/manifest.json"
            : "\n上传 files 目录后，最后发布 manifest.json 及 manifest.json.sig。"));
        return 0;
    }
    Console.WriteLine("用法：\n  YS_ADBReleaseTool keygen <密钥目录>\n  YS_ADBReleaseTool publish <客户端目录> <新输出目录> <HTTPS发布根地址> <版本号> <私钥PEM路径> [更新说明]\n  YS_ADBReleaseTool publish-github <客户端目录> <新输出目录> <owner/repo> <版本号> <私钥PEM路径> [更新说明]\n版本号必须递增。此工具只生成发布文件，不会自动上传。");
    return 1;
}
catch (Exception ex) { Console.Error.WriteLine("失败：" + ex.Message); return 1; }

}
}

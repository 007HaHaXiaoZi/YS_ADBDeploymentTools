using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AdbDeploymentTool;

internal sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 60;
    public string ManifestUrl { get; set; } = ApplicationIdentity.ManifestUrl;
    public string PublicKeyPem { get; set; } = ApplicationIdentity.PublisherPublicKey;
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static UpdateSettings Load(string root)
    {
        var path = Path.Combine(root, "update-settings.json");
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("更新配置为空。") : new();
        settings.ManifestUrl = ApplicationIdentity.NormalizeUpdateUrl(settings.ManifestUrl);
        if (string.IsNullOrWhiteSpace(settings.PublicKeyPem) && settings.ManifestUrl == ApplicationIdentity.ManifestUrl)
            settings.PublicKeyPem = ApplicationIdentity.PublisherPublicKey;
        return settings;
    }
    public void Save(string root) => AtomicWrite(Path.Combine(root, "update-settings.json"), JsonSerializer.Serialize(this, Json));
    public static void AtomicWrite(string path, string text)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }
}

internal sealed record ReleaseFile(string Path, string Url, long Size, string Sha256);
internal sealed record ReleaseManifest(string Product, string Version, string Notes, List<ReleaseFile> Files);
internal sealed record VerifiedRelease(ReleaseManifest Manifest, byte[] ManifestBytes, byte[] Signature);
internal sealed record UpdateJournal(string BackupDirectory, List<BackupEntry> Entries);
internal sealed record BackupEntry(string Path, bool Existed);

internal sealed class UpdateService : IDisposable
{
    public const string Product = ApplicationIdentity.Name;
    public const string ExeName = ApplicationIdentity.Name + ".exe";
    public const string AppVersion = "2.0.3";
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly UpdateSettings _settings;
    private readonly Action<string> _log;

    public UpdateService(string root, UpdateSettings settings, Action<string> log, HttpMessageHandler? handler = null)
    {
        _root = System.IO.Path.GetFullPath(root);
        _settings = settings;
        _log = log;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ComoAdbTool/" + AppVersion);
    }

    public static Uri RequireHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("更新地址必须是无用户名和密码的 HTTPS 地址。");
        return uri;
    }

    public static bool AllowedPath(string path)
    {
        if (path == ExeName) return true;
        if (!path.StartsWith("tools/", StringComparison.Ordinal) || path.Count(c => c == '/') != 1) return false;
        var name = path[6..];
        if (name.Length == 0 || name.Contains("..") || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (Component.All.Any(c => c.Target != null && c.Id == name)) return true;
        return ResourceCatalog.IsUnity(name, UnityVariant.QC) || ResourceCatalog.IsUnity(name, UnityVariant.DL) ||
            (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) &&
             (name.Contains("COMO_LA", StringComparison.OrdinalIgnoreCase) || name.Contains("XRLaunche", StringComparison.OrdinalIgnoreCase)));
    }

    public static string SafePath(string root, string relative)
    {
        if (!AllowedPath(relative)) throw new InvalidDataException("更新包包含不允许替换的路径：" + relative);
        root = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新路径越界。");
        for (var current = full; current != null && current.Length >= root.Length; current = System.IO.Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("更新路径不能经过符号链接或目录联接：" + current);
        return full;
    }

    public static VerifiedRelease Verify(byte[] bytes, byte[] signature, string publicKey)
    {
        if (bytes.Length > 512 * 1024) throw new InvalidDataException("版本清单过大。");
        if (string.IsNullOrWhiteSpace(publicKey)) throw new InvalidDataException("请先配置发布者公钥，才能验证更新来源。");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKey);
        if (rsa.KeySize < 2048 || !rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("更新签名校验失败，已拒绝此次更新。");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(bytes, UpdateSettings.Json) ?? throw new InvalidDataException("版本清单为空。");
        if (manifest.Product != Product || !Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("更新产品或版本号无效。");
        if (manifest.Files == null || manifest.Files.Count is < 1 or > 128) throw new InvalidDataException("更新文件数量无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file == null || file.Path == null || !AllowedPath(file.Path) || !names.Add(file.Path)) throw new InvalidDataException("更新路径无效或重复。");
            RequireHttps(file.Url);
            if (file.Size <= 0 || file.Size > 2L * 1024 * 1024 * 1024 || file.Sha256 == null || !System.Text.RegularExpressions.Regex.IsMatch(file.Sha256, "\\A[0-9a-fA-F]{64}\\z"))
                throw new InvalidDataException("更新文件大小或 SHA-256 无效。");
            total += file.Size;
        }
        if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("更新包总大小超过 4 GB。");
        return new(manifest, bytes, signature);
    }

    private async Task<HttpResponseMessage> GetHttpsAsync(Uri uri, CancellationToken token)
    {
        // GitHub Releases 会跳转到资产下载域名。每一跳都校验 HTTPS，避免降级或无限重定向。
        for (int redirects = 0; ; redirects++)
        {
            RequireHttps(uri.AbsoluteUri);
            var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null || redirects >= 5) throw new InvalidDataException("更新下载跳转无效或超过 5 次。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private async Task<byte[]> GetSmallAsync(Uri uri, int limit, CancellationToken token)
    {
        using var response = await GetHttpsAsync(uri, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("更新响应超过大小限制。");
        using var output = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("更新响应超过大小限制。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public Version InstalledVersion()
    {
        var binary = Version.Parse(AppVersion);
        var path = System.IO.Path.Combine(_root, "installed-version.txt");
        if (File.Exists(path) && Version.TryParse(File.ReadAllText(path).Trim(), out var installed) && installed > binary) return installed;
        return binary;
    }

    public async Task<VerifiedRelease?> CheckAsync(CancellationToken token)
    {
        var uri = RequireHttps(_settings.ManifestUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var bytes = await GetSmallAsync(uri, 512 * 1024, timeout.Token);
        var signatureUri = new UriBuilder(uri) { Path = uri.AbsolutePath + ".sig" }.Uri;
        var signature = await GetSmallAsync(signatureUri, 1024, timeout.Token);
        var release = Verify(bytes, signature, _settings.PublicKeyPem);
        return Version.Parse(release.Manifest.Version) > InstalledVersion() ? release : null;
    }

    public async Task<string> DownloadAsync(VerifiedRelease release, CancellationToken token)
    {
        // 不信任调用者可变的 manifest 对象，再从已签名原始字节解析。
        release = Verify(release.ManifestBytes, release.Signature, _settings.PublicKeyPem);
        var updates = UpdateDirectory(_root);
        var stage = System.IO.Path.Combine(updates, "stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        foreach (var file in release.Manifest.Files)
        {
            var target = SafePath(stage, file.Path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            _log("下载更新：" + file.Path);
            using var response = await GetHttpsAsync(RequireHttps(file.Url), timeout.Token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using (var output = File.Create(target))
            {
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    written += count;
                    if (written > file.Size) throw new InvalidDataException("下载文件超出清单声明的大小：" + file.Path);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                }
            }
            ValidateFile(target, file);
        }
        await File.WriteAllBytesAsync(System.IO.Path.Combine(stage, "manifest.json"), release.ManifestBytes, token);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(stage, "manifest.json.sig"), release.Signature, token);
        return stage;
    }

    internal static void ValidateFile(string path, ReleaseFile file)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) throw new InvalidDataException("文件大小校验失败：" + file.Path);
        using var stream = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件 SHA-256 校验失败：" + file.Path);
    }

    internal static string UpdateDirectory(string root)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), ".updates");
        if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("更新目录不能是符号链接。");
        Directory.CreateDirectory(path);
        return path;
    }

    public static Process StartUpdater(string root, string stage)
    {
        var runner = System.IO.Path.Combine(UpdateDirectory(root), "runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runner);
        var exe = System.IO.Path.Combine(runner, "UpdateRunner.exe");
        File.Copy(Environment.ProcessPath!, exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--apply-update", root, stage, Environment.ProcessId.ToString(), Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString() }) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动更新程序。");
    }

    public static void Apply(string root, string stage, Action<string>? beforeReplace = null)
    {
        root = System.IO.Path.GetFullPath(root);
        var updates = UpdateDirectory(root);
        stage = System.IO.Path.GetFullPath(stage);
        if (!stage.StartsWith(updates + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || File.GetAttributes(stage).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("更新暂存目录不属于当前安装目录。");
        var settings = UpdateSettings.Load(root);
        Recover(root);
        var release = Verify(File.ReadAllBytes(System.IO.Path.Combine(stage, "manifest.json")), File.ReadAllBytes(System.IO.Path.Combine(stage, "manifest.json.sig")), settings.PublicKeyPem);
        using (var checker = new UpdateService(root, settings, _ => { }))
            if (Version.Parse(release.Manifest.Version) <= checker.InstalledVersion()) throw new InvalidDataException("拒绝重复更新或降级。");
        foreach (var file in release.Manifest.Files)
        {
            ValidateFile(SafePath(stage, file.Path), file);
            SafePath(root, file.Path);
        }
        var backup = System.IO.Path.Combine(updates, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var statePath = System.IO.Path.Combine(root, "installed-version.txt");
        if (File.Exists(statePath)) File.Copy(statePath, System.IO.Path.Combine(backup, "installed-version.txt"));
        var journal = new UpdateJournal(backup, []);
        var journalPath = System.IO.Path.Combine(updates, "pending.json");
        try
        {
            foreach (var file in release.Manifest.Files)
            {
                var target = SafePath(root, file.Path);
                var saved = SafePath(backup, file.Path);
                bool existed = File.Exists(target);
                if (existed)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(saved)!);
                    File.Copy(target, saved);
                }
                journal.Entries.Add(new(file.Path, existed));
                UpdateSettings.AtomicWrite(journalPath, JsonSerializer.Serialize(journal, UpdateSettings.Json));
                beforeReplace?.Invoke(file.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                // 临时文件与目标同卷；拷贝失败时原文件仍在。
                var temporary = target + ".updating";
                File.Copy(SafePath(stage, file.Path), temporary, true);
                File.Move(temporary, target, true);
            }
            UpdateSettings.AtomicWrite(statePath, release.Manifest.Version);
            File.Delete(journalPath);
            try { File.WriteAllText(System.IO.Path.Combine(updates, "last-result.txt"), "更新成功：" + release.Manifest.Version); } catch { /* 已提交的更新不因诊断日志失败而报错。 */ }
        }
        catch
        {
            Recover(root);
            throw;
        }
    }

    public static void Recover(string root)
    {
        var updates = UpdateDirectory(root);
        var journalPath = System.IO.Path.Combine(updates, "pending.json");
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(journalPath), UpdateSettings.Json) ?? throw new InvalidDataException("更新恢复记录无效。");
        var backup = System.IO.Path.GetFullPath(journal.BackupDirectory);
        if (!backup.StartsWith(updates + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(backup) || File.GetAttributes(backup).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("备份路径无效。");
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            var target = SafePath(root, entry.Path);
            if (entry.Existed)
            {
                var saved = SafePath(backup, entry.Path);
                File.Copy(saved, target + ".recovering", true);
                File.Move(target + ".recovering", target, true);
            }
            else if (File.Exists(target)) File.Delete(target);
            if (File.Exists(target + ".updating")) File.Delete(target + ".updating");
        }
        var versionBackup = System.IO.Path.Combine(backup, "installed-version.txt");
        var statePath = System.IO.Path.Combine(root, "installed-version.txt");
        if (File.Exists(versionBackup)) File.Copy(versionBackup, statePath, true);
        else if (File.Exists(statePath)) File.Delete(statePath);
        File.Delete(journalPath);
        try { File.WriteAllText(System.IO.Path.Combine(updates, "last-result.txt"), "上次更新中断或失败，已恢复原版本。"); } catch { }
    }

    public void Dispose() => _http.Dispose();
}

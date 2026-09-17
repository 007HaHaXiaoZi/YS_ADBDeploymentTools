using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AdbDeploymentTool;

namespace ReleaseTool;

internal sealed class GitHubPublisher(HttpClient http, string repository)
{
    private string Api => "https://api.github.com/repos/" + repository;

    private async Task<JsonElement?> Send(HttpMethod method, string url, HttpContent? content = null, bool allowMissing = false)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host != "api.github.com" && uri.Host != "uploads.github.com") throw new IOException("GitHub 返回了意外的地址。");
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        using var response = await http.SendAsync(request);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new IOException("GitHub 请求失败：HTTP " + (int)response.StatusCode + "。请检查登录权限、网络或已有 Release。");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task Preflight(string version)
    {
        var repo = (await Send(HttpMethod.Get, Api))!.Value;
        if (repo.GetProperty("private").GetBoolean()) throw new IOException("自动更新需要公开仓库。");
        // List releases as well as tags: draft releases do not necessarily have a tag yet.
        for (var page = 1; ; page++)
        {
            var releases = (await Send(HttpMethod.Get, Api + "/releases?per_page=100&page=" + page))!.Value;
            foreach (var release in releases.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString()!;
                if (tag == "v" + version) throw new IOException("Release v" + version + " 已存在（可能是草稿），拒绝覆盖。请检查已有发布。");
                if (!release.GetProperty("draft").GetBoolean() && !release.GetProperty("prerelease").GetBoolean() &&
                    Version.TryParse(tag.TrimStart('v', 'V'), out var published) && Version.Parse(version) <= published)
                    throw new IOException("发布版本必须高于已公开版本 " + tag + "。");
            }
            if (releases.GetArrayLength() < 100) break;
        }
        if (await Send(HttpMethod.Get, Api + "/git/ref/tags/v" + version, allowMissing: true) != null)
            throw new IOException("版本标签已存在，请使用新版本，避免关联到错误的提交。");
    }

    public static VerifiedRelease ValidateAssets(string output, string repository, string version, string publicKey)
    {
        var expected = new[] { "manifest.json", "manifest.json.sig", UpdateService.ExeName };
        var actual = Directory.GetFileSystemEntries(output).Select(Path.GetFileName).Order().ToArray();
        if (!actual.SequenceEqual(expected.Order())) throw new IOException("发布目录必须且只能包含 EXE、manifest.json、manifest.json.sig。");
        var release = UpdateService.Verify(File.ReadAllBytes(Path.Combine(output, "manifest.json")),
            File.ReadAllBytes(Path.Combine(output, "manifest.json.sig")), publicKey);
        if (release.Manifest.Version != version || release.Manifest.Files.Count != 1) throw new IOException("更新清单的版本或文件数量不匹配。");
        var file = release.Manifest.Files.Single();
        if (file.Path != UpdateService.ExeName || file.Url != $"https://github.com/{repository}/releases/download/v{version}/{UpdateService.ExeName}")
            throw new IOException("更新清单的下载地址与目标仓库不一致。");
        UpdateService.ValidateFile(Path.Combine(output, UpdateService.ExeName), file);
        return release;
    }

    public async Task<string> Upload(string output, string version, string notes, string commit, string publicKey)
    {
        ValidateAssets(output, repository, version, publicKey);
        var release = (await Send(HttpMethod.Post, Api + "/releases", JsonContent.Create(new
        {
            tag_name = "v" + version, target_commitish = commit, name = "v" + version,
            body = notes, draft = true, prerelease = false
        })))!.Value;
        var id = release.GetProperty("id").GetInt64();
        var releaseUrl = $"https://github.com/{repository}/releases/tag/v{version}";
        Console.WriteLine("已创建草稿 Release：v" + version);
        try
        {
            var uploadUrl = release.GetProperty("upload_url").GetString()!.Split('{')[0];
            var uri = new Uri(uploadUrl);
            if (uri.Scheme != "https" || uri.Host != "uploads.github.com" || !string.IsNullOrEmpty(uri.UserInfo)) throw new IOException("附件上传地址无效。");
            foreach (var name in new[] { "manifest.json", "manifest.json.sig", UpdateService.ExeName })
            {
                await using var stream = File.OpenRead(Path.Combine(output, name));
                var size = stream.Length;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
                stream.Position = 0;
                using var body = new StreamContent(stream);
                body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                var asset = (await Send(HttpMethod.Post, uploadUrl + "?name=" + Uri.EscapeDataString(name), body))!.Value;
                if (asset.GetProperty("name").GetString() != name || asset.GetProperty("state").GetString() != "uploaded" || asset.GetProperty("size").GetInt64() != size)
                    throw new IOException("GitHub 附件上传校验失败：" + name);
                if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String && digest.GetString() != "sha256:" + hash)
                    throw new IOException("GitHub 附件哈希不一致：" + name);
                Console.WriteLine("已上传并核对：" + name);
            }
            var published = (await Send(HttpMethod.Patch, Api + "/releases/" + id, JsonContent.Create(new { draft = false, make_latest = "true" })))!.Value;
            if (published.GetProperty("draft").GetBoolean()) throw new IOException("GitHub 未将草稿公开。");
            Console.WriteLine("发布成功：" + releaseUrl);
            Console.WriteLine($"客户端检查地址：https://github.com/{repository}/releases/latest/download/manifest.json");
            return releaseUrl;
        }
        catch
        {
            Console.Error.WriteLine($"本次发布未确认完成，请到 https://github.com/{repository}/releases 检查 Release ID {id}。附件上传失败时草稿会保留；请勿公开不完整草稿。");
            throw;
        }
    }
}

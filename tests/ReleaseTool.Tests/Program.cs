using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ReleaseTool;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch { checks++; return; }
    throw new Exception(message);
}
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../.build", "release-tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
try
{
    var source = Path.Combine(root, "client");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "YS_ADBDeploymentTools.exe"), "mock client");
    var keys = Path.Combine(root, "keys");
    Check(PackageCommands.Run(["keygen", keys]) == 0, "keygen");
    var output = Path.Combine(root, "assets");
    Check(PackageCommands.Run(["publish-github", source, output, "example/repo", "2.0.2", Path.Combine(keys, "publisher-private.pem"), "测试说明\n第二行"]) == 0, "package");
    var publicKey = File.ReadAllText(Path.Combine(keys, "publisher-public.pem"));
    GitHubPublisher.ValidateAssets(output, "example/repo", "2.0.2", publicKey); checks++;
    await Reject(() => Task.Run(() => GitHubPublisher.ValidateAssets(output, "other/repo", "2.0.2", publicKey)), "wrong repository accepted");
    await Reject(() => Task.Run(() => GitHubPublisher.ValidateAssets(output, "example/repo", "2.0.3", publicKey)), "wrong version accepted");
    File.WriteAllText(Path.Combine(output, "secret.txt"), "must not upload");
    await Reject(() => Task.Run(() => GitHubPublisher.ValidateAssets(output, "example/repo", "2.0.2", publicKey)), "extra file accepted");
    File.Delete(Path.Combine(output, "secret.txt"));
    var clientPath = Path.Combine(output, "YS_ADBDeploymentTools.exe");
    var saved = File.ReadAllBytes(clientPath);
    File.WriteAllText(clientPath, "tampered");
    await Reject(() => Task.Run(() => GitHubPublisher.ValidateAssets(output, "example/repo", "2.0.2", publicKey)), "tampered exe accepted");
    File.WriteAllBytes(clientPath, saved);
    var sigPath = Path.Combine(output, "manifest.json.sig");
    var signature = File.ReadAllBytes(sigPath);
    var badSig = signature.ToArray(); badSig[0] ^= 1; File.WriteAllBytes(sigPath, badSig);
    await Reject(() => Task.Run(() => GitHubPublisher.ValidateAssets(output, "example/repo", "2.0.2", publicKey)), "tampered signature accepted");
    File.WriteAllBytes(sigPath, signature);

    foreach (var scenario in new[] { "success", "duplicate", "old", "private", "tag", "page-two", "fail-upload", "bad-digest", "bad-size", "bad-host", "patch-fail" })
    {
        using var handler = new FakeGitHub(scenario);
        using var http = new HttpClient(handler);
        var publisher = new GitHubPublisher(http, "example/repo");
        if (scenario is "duplicate" or "old" or "private" or "tag" or "page-two")
        {
            await Reject(() => publisher.Preflight("2.0.2"), scenario + " preflight accepted");
            Check(!handler.Calls.Any(c => c.StartsWith("POST")), "preflight mutated remote");
            continue;
        }
        await publisher.Preflight("2.0.2");
        if (scenario == "success")
        {
            var url = await publisher.Upload(output, "2.0.2", "测试说明\n第二行", "abc123", publicKey);
            Check(url.EndsWith("/v2.0.2"), "wrong release URL");
            Check(handler.Uploads == 3 && handler.Patches == 1, "incorrect upload order/count");
            Check(handler.Calls.Last().StartsWith("PATCH"), "published before assets");
            Check(handler.Commit == "abc123" && handler.Notes == "测试说明\n第二行", "commit or notes lost");
        }
        else
        {
            await Reject(() => publisher.Upload(output, "2.0.2", "notes", "abc123", publicKey), scenario + " accepted");
            Check(handler.Patches == (scenario == "patch-fail" ? 1 : 0), "published incomplete release");
        }
    }

    foreach (var file in new[] { "../outside.txt", ".git/config", "tools/a.apk", ".build/a.txt", ".release-private/publisher-private.pem", "other/private.key", "deployment-options.json" })
        await Reject(() => Task.Run(() => GitCommands.ValidateFile(root, file)), "unsafe git path accepted: " + file);
    File.WriteAllText(Path.Combine(root, "renamed.txt"), "-----BEGIN PRIVATE KEY-----\nsecret");
    await Reject(() => Task.Run(() => GitCommands.ValidateFile(root, "renamed.txt")), "renamed private key accepted");
    GitCommands.ValidateFile(root, "normal.txt"); checks++;
    var repo = Path.Combine(root, "repo"); Directory.CreateDirectory(repo);
    await GitCommands.Run(repo, "init");
    await GitCommands.Run(repo, "remote", "add", "origin", "https://github.com/example/repo.git");
    await Reject(() => GitCommands.ValidateRepository(repo, "wrong/repo"), "wrong remote accepted");
    File.WriteAllText(Path.Combine(repo, "one.txt"), "one");
    await GitCommands.Run(repo, "add", "--", "one.txt");
    await Reject(() => GitCommands.Upload(repo, "example/repo", "test", ["two.txt"]), "existing stage accepted");
    Check((await GitCommands.Run(repo, "diff", "--cached", "--name-only")) == "one.txt", "existing index changed");
    await Reject(() => ReleaseWorkflow.RunAsync(["release", "2.0.2", "--push"]), "push without upload accepted");
    await Reject(() => ReleaseWorkflow.RunAsync(["release", "2.0.2", "--unknown"]), "unknown flag accepted");
    await Reject(() => Task.Run(() => ReleaseWorkflow.ValidateClient(source, "2.0.2")), "fake EXE version accepted");
    Console.WriteLine($"PASS: {checks} release checks; mock GitHub only, no real upload.");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }

sealed class FakeGitHub(string scenario) : HttpMessageHandler
{
    public List<string> Calls { get; } = [];
    public int Uploads, Patches;
    public string? Commit, Notes;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        Calls.Add(request.Method + " " + uri.AbsoluteUri);
        if (uri.Host is not ("api.github.com" or "uploads.github.com")) throw new Exception("credentials sent to wrong host");
        if (request.Method == HttpMethod.Get)
        {
            if (uri.AbsolutePath.EndsWith("/example/repo")) return Json(new { @private = scenario == "private" });
            if (uri.AbsolutePath.Contains("/git/ref/")) return scenario == "tag" ? Json(new { @ref = "exists" }) : new(HttpStatusCode.NotFound);
            var existing = new { tag_name = scenario == "old" ? "v2.0.3" : "v2.0.2", draft = scenario != "old", prerelease = false };
            if (scenario == "page-two" && uri.Query.EndsWith("&page=1"))
                return Json(Enumerable.Range(0, 100).Select(i => new { tag_name = "v0.0." + i, draft = false, prerelease = false }));
            return Json(scenario is "duplicate" or "old" or "page-two" ? new[] { existing } : []);
        }
        if (request.Method == HttpMethod.Post && uri.Host == "api.github.com")
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            if (!body.GetProperty("draft").GetBoolean()) throw new Exception("release created public");
            Commit = body.GetProperty("target_commitish").GetString(); Notes = body.GetProperty("body").GetString();
            return Json(new { id = 7, upload_url = "https://" + (scenario == "bad-host" ? "evil.example" : "uploads.github.com") + "/repos/example/repo/releases/7/assets{?name,label}" });
        }
        if (request.Method == HttpMethod.Post)
        {
            Uploads++;
            if (scenario == "fail-upload" && Uploads == 2) return new(HttpStatusCode.InternalServerError);
            var bytes = await request.Content!.ReadAsByteArrayAsync(token);
            var name = Uri.UnescapeDataString(uri.Query[6..]);
            return Json(new { name, state = "uploaded", size = bytes.Length + (scenario == "bad-size" ? 1 : 0),
                digest = "sha256:" + (scenario == "bad-digest" ? "bad" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) });
        }
        if (request.Method == HttpMethod.Patch)
        {
            Patches++;
            if (Uploads != 3) throw new Exception("incomplete release published");
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            if (body.GetProperty("draft").GetBoolean() || body.GetProperty("make_latest").GetString() != "true") throw new Exception("wrong publication flags");
            return scenario == "patch-fail" ? new(HttpStatusCode.BadGateway) : Json(new { draft = false });
        }
        throw new Exception("unexpected request");
    }
    static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}

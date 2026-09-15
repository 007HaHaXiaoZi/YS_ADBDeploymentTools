using AdbDeploymentTool;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private static int _checks;
    private static void Assert(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message) { try { action(); } catch { _checks++; return; } throw new Exception(message); }
    private static async Task ThrowsAsync(Func<Task> action, string message) { try { await action(); } catch { _checks++; return; } throw new Exception(message); }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--apk-packages")
        {
            foreach (var path in args.Skip(1)) Console.WriteLine(Path.GetFileName(path) + " => " + ApkMetadata.ReadPackageName(path));
            return 0;
        }
        if (args.Length >= 3 && args[0] == "-s" && args[1].StartsWith("TEST-", StringComparison.Ordinal)) return RunProcessFixture(args);
        var root = Path.Combine(Path.GetTempPath(), "como-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Console.WriteLine("Testing UI controls..."); TestUi();
            Console.WriteLine("Testing actual child-process ADB capture (fake executable, no devices)..."); TestAdbProcesses(root).GetAwaiter().GetResult();
            Console.WriteLine("Testing deployment selections..."); TestDeployment(root).GetAwaiter().GetResult();
            Console.WriteLine("Testing metadata, device inspection, custom sources and export..."); TestInspection(root).GetAwaiter().GetResult();
            Console.WriteLine("Testing update transactions..."); TestUpdates(root).GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_checks} assertions; UI radio groups, 127 deployment selections, signed downloads, rollback and crash recovery.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // 此目录由本测试独立创建，清理范围固定在系统临时目录内。
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static IEnumerable<Control> AllControls(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(AllControls(c)));
    private static void TestUi()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new MainForm();
        var radios = AllControls(form).OfType<RadioButton>().ToArray();
        var qc = radios.Single(r => r.Text.StartsWith("QC"));
        var dl = radios.Single(r => r.Text.StartsWith("DL"));
        dl.Checked = true; Assert(!qc.Checked && dl.Checked, "QC/DL must be mutually exclusive");
        qc.Checked = true; Assert(qc.Checked && !dl.Checked, "QC selection must deselect DL");
        var overwrites = radios.Where(r => r.Text == "覆盖").ToArray();
        Assert(overwrites.Length == 7, "Seven components need independent choices");
        var firstSkip = overwrites[0].Parent!.Controls.OfType<RadioButton>().Single(r => r.Text == "跳过");
        firstSkip.Checked = true;
        Assert(!overwrites[0].Checked && overwrites.Skip(1).All(r => r.Checked), "Skipping Unity must not change other component choices");
        overwrites[0].Checked = true; Assert(!firstSkip.Checked, "Each component uses a separate radio group");
        // WinForms installs a synchronization context even without Application.Run.
        // Async service tests below must not wait for this non-running UI message loop.
        SynchronizationContext.SetSynchronizationContext(null);
    }

    private static async Task TestDeployment(string root)
    {
        var resources = Path.Combine(root, "resources"); var tools = Path.Combine(resources, "tools"); Directory.CreateDirectory(tools);
        foreach (var c in Component.All.Where(c => c.Target != null)) File.WriteAllText(Path.Combine(tools, c.Id), c.Id.EndsWith(".json") ? "{}" : "test content");
        var qc = Path.Combine(tools, "COMO_UN1_QC.apk"); var dl = Path.Combine(tools, "Unity_2_DL.apk"); var launcher = Path.Combine(tools, "COMO_LA.apk");
        foreach (var path in new[] { qc, dl, launcher }) File.WriteAllText(path, "fake apk");
        var catalog = new ResourceCatalog(resources);
        Assert(catalog.Apks(UnityVariant.QC).SequenceEqual(new[] { qc }), "QC filtering");
        Assert(catalog.Apks(UnityVariant.DL).SequenceEqual(new[] { dl }), "DL filtering");
        Assert(!ResourceCatalog.IsUnity("Unity_qc.apk", UnityVariant.QC), "Lowercase qc must not match");
        Assert(!ResourceCatalog.IsUnity("Unity_dl.apk", UnityVariant.DL), "Lowercase dl must not match");
        Assert(!ResourceCatalog.IsUnity("Unity_QC_DL.apk", UnityVariant.QC), "Ambiguous APK must be rejected");
        Assert(!ResourceCatalog.IsUnity("Launcher_QC.apk", UnityVariant.QC), "Non-Unity APK must not match");
        Assert(ResourceCatalog.IsUnity("COMO_UN_QC.APK", UnityVariant.QC), "APK extension can be uppercase");
        Throws(() => DeploymentPlan.Create(catalog, UnityVariant.DL, new HashSet<string> { "Unity" }, qc, null), "Wrong variant cannot be installed");
        Throws(() => DeploymentPlan.Create(catalog, UnityVariant.DL, new HashSet<string> { "Unity" }, null, null), "No fallback to QC when DL missing");
        Throws(() => DeploymentPlan.Create(catalog, UnityVariant.QC, new HashSet<string>(), null, null), "Empty plan must not execute");

        for (int mask = 1; mask < 128; mask++)
        {
            var selected = Component.All.Where((_, i) => (mask & (1 << i)) != 0).Select(c => c.Id).ToHashSet();
            var plan = DeploymentPlan.Create(catalog, UnityVariant.DL, selected, dl, launcher);
            var adb = new FakeAdb(); await new DeploymentService(adb, _ => { }).ExecuteAsync("device-2", plan, CancellationToken.None);
            Assert(adb.Commands.All(c => c.Serial == "device-2"), "Every command must address selected serial");
            var pushes = adb.Commands.Where(c => c.Args[0] == "push").ToArray();
            var expected = selected.Where(id => id is not "Unity" and not "Launcher").Order().ToArray();
            Assert(pushes.Select(c => Path.GetFileName(c.Args[1])).Order().SequenceEqual(expected), "Skip must produce no file writes, mask=" + mask);
            foreach (var item in plan.Items.Where(i => i.Component.Target != null))
            {
                Assert(pushes.Any(c => c.Args[2].StartsWith(item.Component.Target + ".deploy-", StringComparison.Ordinal)), "Correct destination " + item.Component.Id);
                Assert(adb.Commands.Any(c => c.Args[0] == "shell" && c.Args[1].Contains(" " + item.Component.Target + " && sync", StringComparison.Ordinal)), "Atomic replace only selected file");
            }
            var installs = adb.Commands.Where(c => c.Args[0] == "install").ToArray();
            Assert(installs.Length == selected.Count(id => id is "Unity" or "Launcher"), "APK skipping");
            Assert(installs.All(c => c.Args[1] == "-r"), "APK updates preserve data");
            Assert(installs.All(c => c.Args[2] != qc), "DL mode cannot install QC");
            Assert(adb.Commands.Any(c => c.Args[0] == "reboot") == selected.Contains("Launcher"), "Skipping Launcher skips reboot");
            Assert(adb.RootCalls > 0 == plan.Items.Any(i => i.Component.Group is DeploymentGroup.System or DeploymentGroup.Launcher), "Root only for selected system/Launcher work");
        }
        var effectOnly = DeploymentPlan.Create(catalog, UnityVariant.DL, new HashSet<string> { "CameraEffect.json" }, null, null);
        Assert(effectOnly.Items.Single().Component.Target == "/sdcard/Android/data/com.horeal.UnityAndroid/files/CameraEffect.json", "CameraEffect exact path");
        File.Delete(qc); File.Delete(dl); File.Delete(launcher); File.Delete(Path.Combine(tools, "libdsp_wrapper.so"));
        Assert(DeploymentPlan.Create(catalog, UnityVariant.DL, new HashSet<string> { "CameraEffect.json" }, null, null).Items.Count == 1, "Missing skipped resources do not block config-only work");
        File.WriteAllText(Path.Combine(tools, "CameraEffect.json"), "broken json");
        Throws(() => DeploymentPlan.Create(catalog, UnityVariant.DL, new HashSet<string> { "CameraEffect.json" }, null, null), "Invalid JSON must fail preflight");
        var badAdb = new FakeAdb { InstallFailure = true };
        await ThrowsAsync(() => new DeploymentService(badAdb, _ => { }).ExecuteAsync("serial", new(UnityVariant.QC, new[] { new DeploymentItem(Component.All[0], "fake.apk"), new DeploymentItem(Component.All[^1], "fake.json") }), CancellationToken.None), "Zero-exit failed APK output must stop deployment");
        Assert(badAdb.Commands.Count == 1, "No config written after APK failure");
    }

    private static VerifiedRelease Sign(ReleaseManifest manifest, RSA rsa)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateSettings.Json);
        return UpdateService.Verify(bytes, rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), rsa.ExportSubjectPublicKeyInfoPem());
    }
    private static ReleaseFile Entry(string path, byte[] data) => new(path, "https://updates.example.test/" + path, data.Length, Convert.ToHexString(SHA256.HashData(data)));

    private static async Task TestUpdates(string root)
    {
        using var rsa = RSA.Create(2048);
        var install = Path.Combine(root, "client"); Directory.CreateDirectory(Path.Combine(install, "tools"));
        var settings = new UpdateSettings { ManifestUrl = "https://updates.example.test/manifest.json", PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem() }; settings.Save(install);
        byte[] executable = Encoding.UTF8.GetBytes("test executable"), effect = Encoding.UTF8.GetBytes("{\"effect\":2}"), existing = Encoding.UTF8.GetBytes("original exe");
        File.WriteAllBytes(Path.Combine(install, UpdateService.ExeName), existing);
        File.WriteAllText(Path.Combine(install, "tools", "CameraParametersSY.json"), "{\"keep\":true}");
        var manifest = new ReleaseManifest(UpdateService.Product, "2.0.1", "test", [Entry(UpdateService.ExeName, executable), Entry("tools/CameraEffect.json", effect)]);
        var release = Sign(manifest, rsa);
        var tampered = release.ManifestBytes.ToArray(); tampered[^1] ^= 1;
        Throws(() => UpdateService.Verify(tampered, release.Signature, settings.PublicKeyPem), "Tampered signature");
        using var wrong = RSA.Create(2048);
        Throws(() => UpdateService.Verify(release.ManifestBytes, release.Signature, wrong.ExportSubjectPublicKeyInfoPem()), "Wrong publisher key");
        foreach (var path in new[] { "../escape.exe", "tools/../../escape", "tools/sub/file.apk", "tools\\Unity_QC.apk", "update-settings.json", "tools/Unity_QC.apk:evil", "C:/escape.exe" })
            Throws(() => Sign(manifest with { Files = [Entry(path, effect)] }, rsa), "Unsafe path " + path);
        Throws(() => Sign(manifest with { Files = [manifest.Files[0], manifest.Files[0]] }, rsa), "Duplicate paths");
        Throws(() => Sign(manifest with { Version = "latest" }, rsa), "Malformed version");
        Throws(() => Sign(manifest with { Files = [manifest.Files[0] with { Url = "http://insecure/file" }] }, rsa), "Insecure URL");
        Throws(() => UpdateService.RequireHttps("https://user:pass@example.com/file"), "Credentials in update URL");

        var handler = new FakeHttp();
        handler.Content[settings.ManifestUrl] = release.ManifestBytes;
        handler.Content[settings.ManifestUrl + ".sig"] = release.Signature;
        handler.Content[new Uri(manifest.Files[0].Url).AbsoluteUri] = executable;
        handler.Content[new Uri(manifest.Files[1].Url).AbsoluteUri] = effect;
        using var updater = new UpdateService(install, settings, _ => { }, handler);
        Assert((await updater.CheckAsync(CancellationToken.None))?.Manifest.Version == "2.0.1", "Discover newer signed version");
        // 模拟 GitHub latest → 指定版本 → 资产域名的跳转，签名仍验证原始下载字节。
        handler.Redirects[settings.ManifestUrl] = "/tagged/manifest.json";
        handler.Redirects["https://updates.example.test/tagged/manifest.json"] = "https://assets.example.test/manifest";
        handler.Content["https://assets.example.test/manifest"] = release.ManifestBytes;
        handler.Redirects[settings.ManifestUrl + ".sig"] = "https://assets.example.test/signature";
        handler.Content["https://assets.example.test/signature"] = release.Signature;
        Assert((await updater.CheckAsync(CancellationToken.None))?.Manifest.Version == "2.0.1", "GitHub-style cross-host and relative HTTPS redirects");
        handler.Redirects[settings.ManifestUrl] = "http://unsafe.example.test/manifest";
        await ThrowsAsync(() => updater.CheckAsync(CancellationToken.None), "HTTPS redirect downgrade rejected");
        handler.Redirects[settings.ManifestUrl] = settings.ManifestUrl;
        await ThrowsAsync(() => updater.CheckAsync(CancellationToken.None), "Redirect loop is bounded");
        handler.Redirects.Clear();
        handler.Redirects[new Uri(manifest.Files[0].Url).AbsoluteUri] = "https://assets.example.test/client.exe";
        handler.Content["https://assets.example.test/client.exe"] = executable;
        var stage = await updater.DownloadAsync(release, CancellationToken.None);
        handler.Redirects.Clear();
        Assert(File.Exists(Path.Combine(stage, UpdateService.ExeName)), "Asset redirects work for file downloads");
        Assert(File.ReadAllBytes(Path.Combine(install, UpdateService.ExeName)).SequenceEqual(existing), "Download does not replace running client");
        Assert(!File.Exists(Path.Combine(install, "tools", "CameraEffect.json")), "Download does not replace resource files");
        Throws(() => UpdateService.Apply(install, stage, path => { if (path.StartsWith("tools/")) throw new IOException("simulated locked target"); }), "Failed update must throw");
        Assert(File.ReadAllBytes(Path.Combine(install, UpdateService.ExeName)).SequenceEqual(existing), "Rollback restores executable");
        Assert(!File.Exists(Path.Combine(install, "tools", "CameraEffect.json")), "Rollback removes newly added files");
        Assert(!File.Exists(Path.Combine(install, "installed-version.txt")), "Failed update does not advance installed version");

        // Simulate process death after a journaled replacement; next launch must recover.
        var backup = Path.Combine(install, ".updates", "backup-crash"); Directory.CreateDirectory(backup);
        File.WriteAllBytes(Path.Combine(backup, UpdateService.ExeName), existing);
        File.WriteAllBytes(Path.Combine(install, UpdateService.ExeName), executable);
        File.WriteAllText(Path.Combine(install, ".updates", "pending.json"), JsonSerializer.Serialize(new UpdateJournal(backup, [new(UpdateService.ExeName, true)]), UpdateSettings.Json));
        UpdateService.Recover(install);
        Assert(File.ReadAllBytes(Path.Combine(install, UpdateService.ExeName)).SequenceEqual(existing), "Interrupted update recovery");

        var stagedEffect = Path.Combine(stage, "tools", "CameraEffect.json");
        File.WriteAllText(stagedEffect, "corrupt");
        Throws(() => UpdateService.Apply(install, stage), "Tampered staged file cannot be installed");
        Assert(File.ReadAllBytes(Path.Combine(install, UpdateService.ExeName)).SequenceEqual(existing), "Preflight validation before first replacement");
        File.WriteAllBytes(stagedEffect, effect);
        UpdateService.Apply(install, stage);
        Assert(File.ReadAllBytes(Path.Combine(install, UpdateService.ExeName)).SequenceEqual(executable), "Executable updated");
        Assert(File.ReadAllBytes(Path.Combine(install, "tools", "CameraEffect.json")).SequenceEqual(effect), "Resources updated");
        Assert(File.ReadAllText(Path.Combine(install, "tools", "CameraParametersSY.json")) == "{\"keep\":true}", "Unlisted resources preserved");
        Assert(UpdateSettings.Load(install).PublicKeyPem == settings.PublicKeyPem, "Update configuration preserved");
        Assert(await updater.CheckAsync(CancellationToken.None) == null, "Current version produces no update");
        Throws(() => UpdateService.Apply(install, stage), "Repeated/downgrade update rejected");

        handler.Content[new Uri(manifest.Files[1].Url).AbsoluteUri] = Encoding.UTF8.GetBytes("wrong");
        await ThrowsAsync(() => updater.DownloadAsync(release, CancellationToken.None), "Corrupt download must be rejected");
        Assert(File.ReadAllBytes(Path.Combine(install, UpdateService.ExeName)).SequenceEqual(executable), "Corrupt download leaves installed client intact");
        handler.Content.Clear();
        await ThrowsAsync(() => updater.CheckAsync(CancellationToken.None), "Unavailable server error must surface");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsAsync(() => updater.CheckAsync(cancelled.Token), "Cancelled checks must terminate");
    }

    private sealed class FakeAdb : IAdbClient
    {
        public List<(string? Serial, string[] Args)> Commands = [];
        public int RootCalls;
        public bool InstallFailure;
        public Task<CommandResult> RunAsync(string? serial, CancellationToken token, params string[] arguments)
        {
            token.ThrowIfCancellationRequested(); Commands.Add((serial, arguments));
            return Task.FromResult(new CommandResult(0, arguments[0] == "install" ? InstallFailure ? "Failure [INSTALL_FAILED]" : "Success" : ""));
        }
        public Task EnsureRootAndRemountAsync(string serial, CancellationToken token) { RootCalls++; return Task.CompletedTask; }
    }
    private sealed class FakeHttp : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Content = [];
        public Dictionary<string, string> Redirects = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Redirects.TryGetValue(request.RequestUri!.AbsoluteUri, out var location))
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return Task.FromResult(response);
            }
            return Task.FromResult(Content.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

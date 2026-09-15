using AdbDeploymentTool;
using ApplicationIdentity = AdbDeploymentTool.ApplicationIdentity;
using System.IO.Compression;
using System.Text;

internal static partial class Program
{
    private static async Task TestInspection(string root)
    {
        var client = Path.Combine(root, "custom-client"); Directory.CreateDirectory(Path.Combine(client, "tools"));
        var external = Path.Combine(root, "custom input files"); Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(client, "CameraEffect.json"), "{\"wrong\":true}");
        var catalog = new ResourceCatalog(client);
        Assert(catalog.Find("CameraEffect.json") == null, "Default lookup must only scan tools");
        var effect = Path.Combine(external, "my-effect.json"); File.WriteAllText(effect, "{\"amount\":7}");
        var slam = Path.Combine(external, "calibration.yaml"); File.WriteAllText(slam, "mode: custom\n");
        var selected = new HashSet<string> { "CameraEffect.json", "YsSlamSensorSettingConfig.yaml" };
        var plan = DeploymentPlan.Create(catalog, UnityVariant.DL, selected, null, null,
            new Dictionary<string, string> { ["CameraEffect.json"] = effect, ["YsSlamSensorSettingConfig.yaml"] = slam });
        Assert(plan.Items.Any(i => i.LocalPath == effect && i.Component.Target == Component.UnityDirectory + "/CameraEffect.json"), "Custom filename preserves Android target filename");
        Assert(plan.Items.Any(i => i.LocalPath == slam), "Custom YAML is selected outside tools");
        Throws(() => DeploymentPlan.Create(catalog, UnityVariant.QC, new HashSet<string> { "CameraEffect.json" }, null, null,
            new Dictionary<string, string> { ["CameraEffect.json"] = slam }), "Wrong custom file extension rejected");
        Assert(ConfigurationPreview.ReadLocal(effect).Contains("amount"), "Local JSON content preview");
        var large = Path.Combine(external, "large.yaml"); File.WriteAllText(large, new string('x', ConfigurationPreview.Limit * 2));
        Assert(ConfigurationPreview.ReadLocal(large).EndsWith("[预览截断：文件超过 64 KiB]"), "Bounded previews disclose truncation");
        Assert(ConfigurationPreview.Tooltip(new string('a', 4000)).Contains("双击"), "Long hover text offers scrollable preview");

        var customApk = Path.Combine(external, "Launcher-custom.apk");
        using (var zip = ZipFile.Open(customApk, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("AndroidManifest.xml").Open()))
            writer.Write("<manifest package=\"com.example.customlauncher\" />");
        Assert(ApkMetadata.ReadPackageName(customApk) == "com.example.customlauncher", "Custom APK reads actual package");
        foreach (bool utf8 in new[] { true, false })
            Assert(ApkMetadata.ParseManifest(BinaryApkManifest(utf8)) == "com.test.launcher", "Android binary manifest string pool " + (utf8 ? "UTF8" : "UTF16"));
        Throws(() => ApkMetadata.ParseManifest(new byte[] { 3, 0, 8, 0, 99, 0, 0, 0 }), "Truncated AXML rejected");
        Throws(() => ApkMetadata.ParseManifest(Encoding.UTF8.GetBytes("<manifest package=\"com.test'; touch x;\"/>")), "APK package cannot inject shell commands");
        Throws(() => ApkMetadata.ParseManifest(Encoding.UTF8.GetBytes("<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><manifest package='com.test.app'/>")), "XML external entities prohibited");

        var fake = new InspectionAdb(); var service = new DeviceInspectionService(fake);
        var packages = new Dictionary<string, string?> { ["Unity"] = DeviceInspectionService.UnityPackage, ["Launcher"] = "com.example.customlauncher" };
        var states = await service.InspectAsync("chosen-device", packages, CancellationToken.None);
        Assert(states["Unity"].Presence == Presence.Present, "Unity application installed by exact package");
        Assert(states["Launcher"].Presence == Presence.Absent, "Package prefix cannot cause false installed state");
        Assert(states["CameraEffect.json"].Presence == Presence.Present && states["CameraEffect.json"].Preview == "{\"device\":true}\n", "Remote configuration preview retained");
        Assert(states["CameraParametersSY.json"].Presence == Presence.Absent, "Missing file shown absent");
        Assert(states["YsSlamSensorSettingConfig.yaml"].Presence == Presence.Error, "Permission errors are not missing files");
        Assert(states["libdsp_wrapper.so"].Presence == Presence.Present, "Library presence detected");
        Assert(fake.Commands.All(c => c.Serial == "chosen-device" && c.Args[0] == "shell"), "Inspection reads only selected device");
        Assert(fake.Commands.All(c => !new[] { "remount", "install", "push", "reboot" }.Contains(c.Args[0])), "Inspection never remounts, writes or reboots");
        Assert(fake.RootCalls == 1 && states["YsSlamSensorSettingConfig.yaml"].Label == "权限不足", "Denied read retries root once and reports permission failure");
        fake.AllowRootRead = true;
        states = await service.InspectAsync("chosen-device", packages, CancellationToken.None);
        Assert(states["YsSlamSensorSettingConfig.yaml"].Presence == Presence.Present, "Protected YAML becomes visible after root retry");
        Assert(fake.Commands.Any(c => c.Args.Length > 1 && c.Args[1] == "ls -ld '/mnt/vendor/persist/calibdata/slam/YsSlamSensorSettingConfig.yaml'"), "YAML inspection uses persist calibdata slam path");
        fake.PackageList = "";
        states = await service.InspectAsync("chosen-device", packages, CancellationToken.None);
        Assert(states["Unity"].Presence == Presence.Error, "Empty pm output cannot mark every app uninstalled");
        packages["Launcher"] = null;
        states = await service.InspectAsync("chosen-device", packages, CancellationToken.None);
        Assert(states["Launcher"].Presence == Presence.Unknown, "Unknown APK package is not uninstalled");

        var exportParent = Path.Combine(root, "chosen export path"); Directory.CreateDirectory(exportParent);
        var existing = Path.Combine(exportParent, "keep.txt"); File.WriteAllText(existing, "keep me");
        var exported = await service.ExportUnityFilesAsync("serial/with:characters", exportParent, CancellationToken.None);
        Assert(exported.StartsWith(exportParent + Path.DirectorySeparatorChar) && File.Exists(Path.Combine(exported, "fixture.json")), "Export creates independent destination and pulls contents");
        Assert(File.ReadAllText(existing) == "keep me", "Export does not overwrite existing user files");
        var pull = fake.Commands.Last();
        Assert(pull.Args.SequenceEqual(new[] { "pull", Component.UnityDirectory + "/.", exported }), "Correct Android files directory exported");
        var secondExport = await service.ExportUnityFilesAsync("serial/with:characters", exportParent, CancellationToken.None);
        Assert(secondExport != exported, "Repeat exports create separate folders");
        Assert(ApplicationIdentity.NormalizeUpdateUrl("git@github.com:007HaHaXiaoZi/YS_ADBDeploymentTools.git") == ApplicationIdentity.ManifestUrl, "Git SSH repository maps to HTTPS Release manifest");
        var defaults = UpdateSettings.Load(client);
        Assert(defaults.ManifestUrl == ApplicationIdentity.ManifestUrl && defaults.AutoCheck && defaults.PublicKeyPem.Contains("BEGIN PUBLIC KEY"), "Every clean PC has working update defaults and pinned public key");
        using var ui = new MainForm(preview: true);
        var controls = AllControls(ui).ToArray();
        Assert(ui.Text.StartsWith("YS_ADBDeploymentTools"), "Application renamed");
        Assert(!controls.Any(c => c.Text.Contains("选择型号与覆盖项目")), "Red boxed subtitle removed");
        Assert(!controls.OfType<Button>().Any(b => b.Text is "① 仅 SLAM" or "② 仅 Unity / 相机配置" or "③ 仅 Launcher"), "Separate deployment buttons removed");
        Assert(controls.OfType<Button>().Count(b => b.Text == "选择…") == 7, "Every component supports custom source");
        Assert(controls.OfType<Button>().Any(b => b.Text == "导出 Unity files…"), "Export action present");
        var deviceHeader = controls.OfType<Label>().Single(c => c.Text == "设备状态");
        var overwriteHeader = controls.OfType<Label>().Single(c => c.Text == "是否覆盖 / 安装");
        var table = (TableLayoutPanel)deviceHeader.Parent!;
        Assert(table.GetColumn(deviceHeader) < table.GetColumn(overwriteHeader), "Device status precedes overwrite choices");
        SynchronizationContext.SetSynchronizationContext(null);
    }

    private static byte[] BinaryApkManifest(bool utf8)
    {
        var values = new[] { "manifest", "package", "com.test.launcher" };
        using var poolData = new MemoryStream(); using var strings = new BinaryWriter(poolData, Encoding.UTF8, true);
        var offsets = new List<uint>();
        foreach (var value in values)
        {
            offsets.Add((uint)poolData.Position);
            if (utf8) { strings.Write((byte)value.Length); strings.Write((byte)value.Length); strings.Write(Encoding.UTF8.GetBytes(value)); strings.Write((byte)0); }
            else { strings.Write((ushort)value.Length); strings.Write(Encoding.Unicode.GetBytes(value)); strings.Write((ushort)0); }
        }
        while (poolData.Length % 4 != 0) strings.Write((byte)0);
        using var result = new MemoryStream(); using var w = new BinaryWriter(result, Encoding.UTF8, true);
        uint poolSize = (uint)(28 + 12 + poolData.Length), total = 8 + poolSize + 56;
        w.Write((ushort)3); w.Write((ushort)8); w.Write(total);
        w.Write((ushort)1); w.Write((ushort)28); w.Write(poolSize); w.Write(3u); w.Write(0u); w.Write(utf8 ? 0x100u : 0u); w.Write(40u); w.Write(0u);
        foreach (var offset in offsets) w.Write(offset); w.Write(poolData.ToArray());
        w.Write((ushort)0x102); w.Write((ushort)16); w.Write(56u); w.Write(1u); w.Write(uint.MaxValue);
        w.Write(uint.MaxValue); w.Write(0u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)1); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write(uint.MaxValue); w.Write(1u); w.Write(2u); w.Write((ushort)8); w.Write((byte)0); w.Write((byte)3); w.Write(2u);
        return result.ToArray();
    }

    private sealed class InspectionAdb : IAdbClient
    {
        public string PackageList = "package:com.horeal.UnityAndroid\npackage:com.example.customlauncher.extra\n";
        public List<(string? Serial, string[] Args)> Commands = [];
        public int RootCalls;
        public bool AllowRootRead;
        public Task EnsureRootAsync(string serial, CancellationToken token) { RootCalls++; return Task.CompletedTask; }
        public Task EnsureRootAndRemountAsync(string serial, CancellationToken token) => throw new Exception("Inspection cannot root or remount");
        public Task<CommandResult> RunAsync(string? serial, CancellationToken token, params string[] args)
        {
            token.ThrowIfCancellationRequested(); Commands.Add((serial, args));
            if (args[0] == "pull") { File.WriteAllText(Path.Combine(args[2], "fixture.json"), "{}"); return Task.FromResult(new CommandResult(0, "1 file pulled")); }
            if (args[1] == "pm list packages") return Task.FromResult(new CommandResult(0, PackageList, AdbClient.BinderWarning));
            if (args[1].StartsWith("test -d")) return Task.FromResult(new CommandResult(0, "drwxrwxr-x files"));
            if (args[1].Contains("CameraParametersSY.json")) throw new AdbCommandException("ls", new(1, "", "No such file or directory"));
            if (args[1].Contains("YsSlamSensorSettingConfig.yaml") && !(AllowRootRead && RootCalls > 0)) throw new AdbCommandException("ls", new(1, "", "Permission denied"));
            if (args[1].StartsWith("head -c")) return Task.FromResult(new CommandResult(0, "{\"device\":true}\n", AdbClient.BinderWarning));
            return Task.FromResult(new CommandResult(0, "-rw-r--r-- 1 root root 114 config\n"));
        }
    }
}

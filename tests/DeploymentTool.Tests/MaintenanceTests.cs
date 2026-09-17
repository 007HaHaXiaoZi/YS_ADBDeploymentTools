using AdbDeploymentTool;
using System.Globalization;

internal static partial class Program
{
    private static async Task TestMaintenance()
    {
        var now = new DateTimeOffset(2026, 9, 17, 9, 30, 15, TimeSpan.FromHours(8));
        var fake = new MaintenanceAdb { Epoch = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
        var service = new DeviceMaintenance(fake, _ => { }, () => now);
        await service.ClearUnityDataAsync("selected", CancellationToken.None);
        Assert(fake.Commands.Last().SequenceEqual(new[] { "shell", DeviceMaintenance.ClearFilesCommand }), "Cleanup uses only bounded files command");
        Assert(!DeviceMaintenance.ClearFilesCommand.Contains("pm clear"), "Never resets application data");
        Assert(fake.RootCalls == 0, "Data clear does not remount or root");
        fake.ClearFailure = true;
        await ThrowsAsync(() => service.ClearUnityDataAsync("selected", CancellationToken.None), "Permission or find failure cannot report success");
        await TestCleanupSelection();
        await service.SyncTimeAsync("selected", CancellationToken.None);
        Assert(fake.Commands.Any(c => c.SequenceEqual(new[] { "shell", "date -u 091701302026.15" })), "Sync formats UTC, independent of PC offset");
        Assert(fake.RootCalls == 1 && fake.Commands.Last()[1] == "date +%s", "Sync roots then verifies device time");
        fake.Epoch = "0";
        await ThrowsAsync(() => service.SyncTimeAsync("selected", CancellationToken.None), "Wrong readback cannot report synchronized");
        using var ui = new MainForm(preview: true);
        var controls = AllControls(ui).ToArray();
        Assert(controls.OfType<Button>().Single(c => c.Text == "清除 Unity 数据").BackColor == Color.Firebrick, "Clear data button is red");
        Assert(controls.OfType<Button>().Any(c => c.Text == "同步设备时间"), "Dedicated time sync button");
        Assert(controls.OfType<Label>().Single(c => c.Text == "准备就绪").ForeColor == Color.Red, "Ready status red");
        Assert(controls.OfType<Label>().Single(c => c.Text == "客户端更新：尚未检查").ForeColor == Color.Red, "Update status red");
        SynchronizationContext.SetSynchronizationContext(null);
    }

    private sealed class MaintenanceAdb : IAdbClient
    {
        public List<string[]> Commands = [];
        public string Epoch = "0";
        public bool ClearFailure;
        public int RootCalls;
        public Task EnsureRootAsync(string serial, CancellationToken token) { Assert(serial == "selected", "Root selected device"); RootCalls++; return Task.CompletedTask; }
        public Task EnsureRootAndRemountAsync(string serial, CancellationToken token) => throw new Exception("Maintenance must not remount");
        public Task<CommandResult> RunAsync(string? serial, CancellationToken token, params string[] args)
        {
            token.ThrowIfCancellationRequested(); Assert(serial == "selected", "Maintenance targets selected device"); Commands.Add(args);
            if (args[1] == DeviceMaintenance.ClearFilesCommand && ClearFailure) throw new AdbCommandException("cleanup", new(1, "", "Permission denied"));
            return Task.FromResult(new CommandResult(0, args[1] == "date +%s" ? Epoch : "", AdbClient.BinderWarning));
        }
    }

    private static async Task TestCleanupSelection()
    {
        var git = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, "git.exe")).FirstOrDefault(File.Exists);
        var bash = git == null ? "" : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "bin", "bash.exe"));
        if (!File.Exists(bash)) throw new Exception("Cleanup selection test requires Git for Windows bash.");
        var fixture = Path.Combine(Path.GetTempPath(), "unity-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string[] keep = ["CameraEffect.json", "CameraParametersSY.json", "sub/UPPER.YAML", "sub/config.ini", "configs/opaque.dat", "settings/user.bin", "sub/keep.xml"];
        string[] remove = ["record.bin", "sub/file with spaces.log", "sub/.hidden", "-option.dat"];
        foreach (var path in keep.Concat(remove)) { var full = Path.Combine(fixture, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, "fixture"); }
        // Exercise the real find predicates with a print action, never delete host files.
        var command = DeviceMaintenance.ClearFilesCommand.Replace(Component.UnityDirectory, fixture.Replace('\\', '/'))
            .Replace("rm -f --", "printf '%s\\n'");
        var start = new System.Diagnostics.ProcessStartInfo(bash) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(command);
        using var process = System.Diagnostics.Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var output = await outputTask; var error = await errorTask;
        Assert(process.ExitCode == 0, "Cleanup predicates valid in shell: " + error);
        var selected = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.StartsWith("./") ? p[2..] : p).Order().ToArray();
        Assert(selected.SequenceEqual(remove.Order()), "Nested/hidden data selected; camera files, config extensions and config directories excluded");
    }
}

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
        Assert(fake.Commands.Last().SequenceEqual(new[] { "shell", "pm clear --user 10 com.horeal.UnityAndroid" }), "Clear only fixed Unity package for current user");
        Assert(fake.RootCalls == 0, "Data clear does not remount or root");
        fake.ClearOutput = "Failed";
        await ThrowsAsync(() => service.ClearUnityDataAsync("selected", CancellationToken.None), "Zero-exit pm clear Failure rejected");
        fake.User = "invalid";
        var count = fake.Commands.Count;
        await ThrowsAsync(() => service.ClearUnityDataAsync("selected", CancellationToken.None), "Invalid current user stops clear");
        Assert(fake.Commands.Count == count + 1, "No clear command after invalid user");
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
        public string User = "10", ClearOutput = "Success", Epoch = "0";
        public int RootCalls;
        public Task EnsureRootAsync(string serial, CancellationToken token) { Assert(serial == "selected", "Root selected device"); RootCalls++; return Task.CompletedTask; }
        public Task EnsureRootAndRemountAsync(string serial, CancellationToken token) => throw new Exception("Maintenance must not remount");
        public Task<CommandResult> RunAsync(string? serial, CancellationToken token, params string[] args)
        {
            token.ThrowIfCancellationRequested(); Assert(serial == "selected", "Maintenance targets selected device"); Commands.Add(args);
            return Task.FromResult(new CommandResult(0, args[1] switch { "am get-current-user" => User, "date +%s" => Epoch,
                _ when args[1].StartsWith("pm clear ") => ClearOutput, _ => "" }, AdbClient.BinderWarning));
        }
    }
}

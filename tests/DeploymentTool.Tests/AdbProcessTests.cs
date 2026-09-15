using AdbDeploymentTool;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private static int RunProcessFixture(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var parts = args[1].Split('|', 2);
        var scenario = parts[0]; var command = args[2];
        if (scenario == "TEST-echo") { Console.Write(JsonSerializer.Serialize(args)); return 0; }
        if (scenario == "TEST-stdin") { Console.Write(Console.In.ReadToEnd().Length == 0 ? "STDIN-EOF" : "unexpected input"); return 0; }
        if (scenario == "TEST-bulk")
        {
            Task.WhenAll(Task.Run(() => Console.Out.Write(new string('O', 300000) + "末尾 stdout")),
                Task.Run(() => Console.Error.Write(new string('E', 300000) + "末尾 stderr"))).GetAwaiter().GetResult();
            return 0;
        }
        Console.Error.WriteLine(AdbClient.BinderWarning);
        Console.Error.Flush();
        if (scenario == "TEST-block") { Console.WriteLine("partial stdout before timeout"); Console.Out.Flush(); Thread.Sleep(20000); return 0; }
        if (scenario == "TEST-minus-one") return -1;
        if (scenario == "TEST-stderr-only") return 0;
        if (scenario == "TEST-failure") { Console.WriteLine("remount succeeded"); Console.Error.WriteLine("Permission denied"); return 1; }
        if (scenario == "TEST-root-recovery")
        {
            if (command == "root") { File.WriteAllText(parts[1], "rooted"); return -1; }
            if (command == "get-state") { Console.WriteLine("device"); return 0; }
            if (command == "shell" && args[3] == "id") { Console.WriteLine(File.Exists(parts[1]) ? "uid=0(root) context=u:r:su:s0" : "uid=2000(shell)"); return 0; }
        }
        if (command == "root") return 71; // 已 root 的测试场景不应再次执行 root。
        if (command == "shell" && args.Length > 3 && args[3] == "id") { Console.WriteLine("uid=0(root) gid=0(root) context=u:r:su:s0"); return 0; }
        Thread.Sleep(120); // warning 先输出，stdout 的成功信息后输出。
        Console.Write(command switch { "remount" => "remount succeeded\r\n", "install" => "Success\r\n", "push" => "1 file pushed\r\n", "get-state" => "device\r\n", _ => "shell ok\r\n" });
        return 0;
    }

    private static async Task<T> ExpectExceptionAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); } catch (T ex) { _checks++; return ex; }
        throw new Exception(message);
    }

    private static async Task TestAdbProcesses(string root)
    {
        var logs = new List<string>();
        var executable = Environment.ProcessPath!; // 子进程是测试 EXE，绝不执行设备 ADB。
        var adb = new AdbClient(executable, logs.Add);
        var remount = await adb.RunRemountAsync("TEST-warning", CancellationToken.None);
        Assert(remount.ExitCode == 0, "remount with stderr warning must succeed");
        Assert(remount.StandardOutput == "remount succeeded\r\n", "Delayed stdout must be fully preserved");
        Assert(remount.StandardError.Contains(AdbClient.BinderWarning), "Raw stderr preserved separately");
        Assert(logs.Any(l => l.StartsWith("ADB Warning: " + AdbClient.BinderWarning)), "Warning remains visible");
        Assert(logs.Any(l => l.Contains("Process.ExitCode=0 (0x00000000)")), "Diagnostic logs expose actual exit code");
        Assert(logs.Any(l => l.Contains("ADB stdout: remount succeeded")), "Success stdout appears in log");
        Assert((await adb.RunRemountAsync("TEST-stderr-only", CancellationToken.None)).ExitCode == 0, "Exit zero must not require success text");
        foreach (var command in new[] { "push", "install", "shell", "get-state" })
        {
            var result = await adb.RunAsync("TEST-warning", CancellationToken.None, command, "test argument");
            Assert(result.ExitCode == 0 && result.StandardError.Contains(AdbClient.BinderWarning), command + " ignores stderr for exit status");
        }
        Assert(AdbClient.IsOnline(new(0, "device\r\n", AdbClient.BinderWarning)), "stderr cannot contaminate device-state parsing");
        Assert(!AdbClient.IsOnline(new(1, "device\r\n")), "get-state still requires exit zero");
        Assert(!AdbClient.IsOnline(new(0, "offline\n", "device")), "stderr cannot fake online state");

        var failure = await ExpectExceptionAsync<AdbCommandException>(() => adb.RunRemountAsync("TEST-failure", CancellationToken.None), "Nonzero exit must fail even with success text");
        Assert(failure.Result.ExitCode == 1 && failure.Result.StandardOutput.Contains("remount succeeded"), "Nonzero result and conflicting success text retained");
        var rawMinusOne = await adb.RunRawAsync("TEST-minus-one", CancellationToken.None, "remount");
        Assert(rawMinusOne.ExitCode == -1, "Windows child process -1 passes through unchanged");
        Assert(rawMinusOne.StandardOutput == "" && rawMinusOne.StandardError.Contains(AdbClient.BinderWarning), "Minus-one fixture reproduces warning-only failure shape");
        var minusOne = await ExpectExceptionAsync<AdbCommandException>(() => adb.RunRemountAsync("TEST-minus-one", CancellationToken.None), "True -1 must not be hidden");
        Assert(minusOne.Result.ExitCode == -1 && minusOne.Message.Contains("0xFFFFFFFF"), "-1 source is visible and not reclassified as warning-success");
        foreach (var command in new[] { "push", "install", "shell" })
            await ExpectExceptionAsync<AdbCommandException>(() => adb.RunAsync("TEST-failure", CancellationToken.None, command), command + " retains true failure");

        var bulk = await adb.RunAsync("TEST-bulk", CancellationToken.None, "shell");
        Assert(bulk.StandardOutput == new string('O', 300000) + "末尾 stdout", "Large stdout fully drained, including no-newline tail");
        Assert(bulk.StandardError == new string('E', 300000) + "末尾 stderr", "Large stderr concurrently drained without deadlock");
        Assert((await adb.RunAsync("TEST-stdin", CancellationToken.None, "shell")).StandardOutput == "STDIN-EOF", "Noninteractive stdin is explicitly closed");
        var echoArgs = new[] { "push", @"C:\含空格目录\quoted ""name"".apk", "/sdcard/path with space/test.apk" };
        var echo = await adb.RunAsync("TEST-echo", CancellationToken.None, echoArgs);
        Assert(JsonSerializer.Deserialize<string[]>(echo.StandardOutput)!.SequenceEqual(new[] { "-s", "TEST-echo" }.Concat(echoArgs)), "Arguments do not pass through a shell or lose quoting");

        logs.Clear();
        await adb.EnsureRootAndRemountAsync("TEST-already-root", CancellationToken.None);
        Assert(!logs.Any(l => l == "> adb -s TEST-already-root root"), "Do not restart adbd when shell id already confirms root");
        Assert(logs.Any(l => l.Contains("跳过 adb root")), "Already-root decision is explicit");
        var serial = "TEST-root-recovery|" + Path.Combine(root, "mock-root-state");
        await adb.EnsureRootAndRemountAsync(serial, CancellationToken.None);
        Assert(logs.Any(l => l.Contains("root 身份验证通过")), "Root reconnect uses clean stdout despite warning in get-state");

        logs.Clear();
        await ExpectExceptionAsync<InvalidOperationException>(() => new AdbClient(Path.Combine(root, "missing-adb.exe"), logs.Add).RunAsync("TEST-warning", CancellationToken.None, "remount"), "Startup error must remain a host exception");
        Assert(logs.Any(l => l.Contains("ADB HostError")) && !logs.Any(l => l.Contains("Process.ExitCode=-1")), "Startup error has no fabricated exit code");
        logs.Clear();
        await ExpectExceptionAsync<TimeoutException>(() => new AdbClient(executable, logs.Add, TimeSpan.FromSeconds(2)).RunAsync("TEST-block", CancellationToken.None, "remount"), "Timeout must remain timeout");
        Assert(logs.Any(l => l.Contains("partial stdout before timeout")) && logs.Any(l => l.StartsWith("ADB Warning:")), "Timeout retains partial output from both channels");
        Assert(logs.Any(l => l.StartsWith("ADB Timeout:")) && !logs.Any(l => l.Contains("Process.ExitCode=-1")), "Timeout is not encoded as -1");
        logs.Clear();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await ExpectExceptionAsync<OperationCanceledException>(() => adb.RunAsync("TEST-block", cancelled.Token, "shell"), "Cancellation must remain cancellation");
        Assert(logs.Any(l => l.StartsWith("ADB Cancelled:")) && !logs.Any(l => l.Contains("Process.ExitCode=-1")), "Cancellation is not encoded as -1");
        logs.Clear();
    }
}

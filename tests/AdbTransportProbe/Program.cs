using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

// 使用真实的 Windows adb.exe，但仅连接此进程创建的 loopback 模拟服务器。
// 不连接 5037，不枚举设备，不接触 Android。
internal static class Program
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] private static extern bool SetStdHandle(int n, IntPtr handle);
    private static readonly List<string> Transcript = [];
    private static string Scenario = "success";
    private static void Log(string line) { lock (Transcript) Transcript.Add(line); }

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) return 2;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serving = ServeAsync(listener, deadline.Token);
            var old = await Probe(args[0], port, false, deadline.Token);
            var fixedResult = await Probe(args[0], port, true, deadline.Token);
            Scenario = "early-close";
            var interrupted = await Probe(args[0], port, true, deadline.Token);
            Scenario = "exit-255";
            var remote255 = await Probe(args[0], port, true, deadline.Token);
            Log($"SUMMARY old={old}; redirected-stdin={fixedResult}");
            Log($"SUMMARY closed-before-stdout={interrupted}; remote-exit-255={remote255}");
            deadline.Cancel(); listener.Stop();
            try { await serving; } catch (OperationCanceledException) { }
            File.WriteAllLines(args[1], Transcript);
            return fixedResult == 0 ? 0 : 1;
        }
        catch (Exception ex) { Log(ex.ToString()); File.WriteAllLines(args[1], Transcript); return 1; }
    }

    private static async Task<int> Probe(string adb, int port, bool redirectInput, CancellationToken token)
    {
        var start = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = redirectInput };
        foreach (var arg in new[] { "-H", "127.0.0.1", "-P", port.ToString(), "-s", "MOCK-ONLY", "remount" }) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        // 模拟资源管理器启动 WinExe 时缺少控制台 stdin；只影响此隔离测试进程。
        var previous = GetStdHandle(-10);
        try { SetStdHandle(-10, IntPtr.Zero); process.Start(); }
        finally { SetStdHandle(-10, previous); }
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        if (redirectInput) process.StandardInput.Close();
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        Log($"scenario={Scenario}; redirect-stdin={redirectInput}; exit={process.ExitCode}; stdout=[{await output}]; stderr=[{await error}]");
        return process.ExitCode;
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        var sessions = new List<Task>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                sessions.Add(HandleAsync(client, token));
            }
        }
        catch (OperationCanceledException) { }
        finally { await Task.WhenAll(sessions); }
    }

    private static async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (true)
                {
                    var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
                    var payload = new byte[Convert.ToInt32(Encoding.ASCII.GetString(header), 16)]; await stream.ReadExactlyAsync(payload, token);
                    var service = Encoding.UTF8.GetString(payload); Log("mock service: " + service);
                    if (service == "host:version") { await Reply(stream, "0029", token); return; }
                    if (service.Contains("features")) { await Reply(stream, "shell_v2,remount_shell", token); return; }
                    if (service.StartsWith("host:tport:")) { await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAY"), token); await stream.WriteAsync(BitConverter.GetBytes(1UL), token); continue; }
                    if (service.StartsWith("host:transport")) { await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAY"), token); continue; }
                    if (service.StartsWith("shell") && service.EndsWith(":remount"))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAY"), token);
                        await Frame(stream, 2, Encoding.UTF8.GetBytes("Binder ioctl to enable oneway spam detection failed: Invalid argument\n"), token);
                        var scenario = Scenario;
                        if (scenario == "early-close") return;
                        await Task.Delay(350, token);
                        await Frame(stream, 1, Encoding.UTF8.GetBytes("remount succeeded\n"), token);
                        await Frame(stream, 3, new byte[] { scenario == "exit-255" ? (byte)255 : (byte)0 }, token);
                        await Task.Delay(50, token);
                        return;
                    }
                    throw new InvalidDataException("Unexpected mock service: " + service);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException ex) { Log("mock connection ended: " + ex.Message); }
        }
    }

    private static async Task Reply(NetworkStream stream, string value, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAY" + bytes.Length.ToString("X4")), token);
        await stream.WriteAsync(bytes, token);
    }
    private static async Task Frame(NetworkStream stream, byte channel, byte[] bytes, CancellationToken token)
    {
        var header = new byte[5]; header[0] = channel; BitConverter.GetBytes(bytes.Length).CopyTo(header, 1);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }
}

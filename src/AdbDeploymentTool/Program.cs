using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AdbDeploymentTool;

internal static class Program
{
    private static Mutex InstanceMutex(string root) => new(false, "Local\\ComoAdbTool_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant()))));

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 5 && args[0] == "--apply-update") return ApplyUpdate(args);
        if (args.Length == 2 && args[0] == "--render-preview")
        {
            using var preview = new MainForm(preview: true); preview.RenderPreview(Path.GetFullPath(args[1])); return 0;
        }
        using var mutex = InstanceMutex(AppContext.BaseDirectory);
        bool acquired;
        try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { MessageBox.Show("此目录的工具已在运行。", ApplicationIdentity.Name); return 1; }
        try
        {
            UpdateService.Recover(AppContext.BaseDirectory);
            Application.Run(new MainForm()); return 0;
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "工具启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        finally { mutex.ReleaseMutex(); }
    }

    private static int ApplyUpdate(string[] args)
    {
        var root = Path.GetFullPath(args[1]);
        try
        {
            var pid = int.Parse(args[3]); var ticks = long.Parse(args[4]);
            try
            {
                using var parent = Process.GetProcessById(pid);
                if (parent.StartTime.ToUniversalTime().Ticks == ticks && !parent.WaitForExit(60000)) throw new IOException("主程序尚未退出，更新未执行。");
            }
            catch (ArgumentException) { /* 主程序已退出。 */ }
            using (var mutex = InstanceMutex(root))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new IOException("另一个客户端实例正在运行，更新未执行。");
                try { UpdateService.Apply(root, args[2]); } finally { mutex.ReleaseMutex(); }
            }
            Process.Start(new ProcessStartInfo(Path.Combine(root, UpdateService.ExeName)) { UseShellExecute = true, WorkingDirectory = root });
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(UpdateService.UpdateDirectory(root), "last-result.txt"), "更新失败：" + ex.Message); } catch { }
            MessageBox.Show("更新未完成：" + ex.Message + "\n请重新打开原工具查看日志。", "客户端更新", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

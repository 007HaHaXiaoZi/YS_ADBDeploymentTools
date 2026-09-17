using ReleaseTool;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;
bool interactive = args.Length == 0 && !Console.IsInputRedirected;
try
{
    if (interactive) args = ReleaseWorkflow.Menu();
    return await ReleaseWorkflow.RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("失败：" + ex.Message);
    return 1;
}
finally
{
    if (interactive) { Console.WriteLine("按回车关闭窗口。"); Console.ReadLine(); }
}
using System.Globalization;

namespace AdbDeploymentTool;

internal sealed class DeviceMaintenance(IAdbClient adb, Action<string> log, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task ClearUnityDataAsync(string serial, CancellationToken token)
    {
        await adb.RunAsync(serial, token, "shell", ClearFilesCommand);
        log("Unity files 内的非配置普通文件已清除；配置文件、配置目录、其他目录及符号链接保留。应用私有数据未清除。");
    }

    internal const string PreservedConfigurations = "JSON、YAML/YML、INI、CFG、CONF、CONFIG、XML、TOML、PROPERTIES 文件，以及 config/configs/configuration/settings 目录（不区分大小写）";

    // 固定目录，cd 失败即停止；不跟随符号链接、不跨文件系统，不删除目录。
    // find 的批量 -exec 将 rm 失败传递为非零退出码，保留 ADB 原始错误。
    internal static string ClearFilesCommand => "target='" + Component.UnityDirectory + "'; " +
        "[ -d \"$target\" ] && [ ! -L \"$target\" ] && cd -P \"$target\" && " +
        "find . -xdev \\( -type d \\( -iname config -o -iname configs -o -iname configuration -o -iname settings \\) -prune \\) -o " +
        "\\( -type f ! -iname '*.json' ! -iname '*.yaml' ! -iname '*.yml' ! -iname '*.ini' " +
        "! -iname '*.cfg' ! -iname '*.conf' ! -iname '*.config' ! -iname '*.xml' ! -iname '*.toml' ! -iname '*.properties' " +
        "-exec rm -f -- {} + \\)";

    public async Task SyncTimeAsync(string serial, CancellationToken token)
    {
        await adb.EnsureRootAsync(serial, token);
        var timestamp = Now.ToUniversalTime().ToString("MMddHHmmyyyy.ss", CultureInfo.InvariantCulture);
        await adb.RunAsync(serial, token, "shell", "date -u " + timestamp);
        var before = Now.ToUnixTimeSeconds();
        var result = await adb.RunAsync(serial, token, "shell", "date +%s");
        var after = Now.ToUnixTimeSeconds();
        if (!long.TryParse(result.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var actual)
            || actual < before - 5 || actual > after + 5)
            throw new InvalidOperationException("时间同步校验失败：设备时间与电脑相差超过 5 秒，或设备未返回有效时间。请检查设备自动时间设置。\n" + result.Output);
        log("设备时间已同步到当前电脑时间并校验通过。设备时区和自动时间设置保持原值。");
    }
}

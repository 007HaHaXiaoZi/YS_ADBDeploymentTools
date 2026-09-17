using System.Globalization;

namespace AdbDeploymentTool;

internal sealed class DeviceMaintenance(IAdbClient adb, Action<string> log, Func<DateTimeOffset>? clock = null)
{
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task ClearUnityDataAsync(string serial, CancellationToken token)
    {
        var user = (await adb.RunAsync(serial, token, "shell", "am get-current-user")).StandardOutput.Trim();
        if (!int.TryParse(user, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId < 0)
            throw new InvalidOperationException("无法确定 Android 当前用户，未清除数据。");
        var result = await adb.RunAsync(serial, token, "shell", $"pm clear --user {userId} {DeviceInspectionService.UnityPackage}");
        if (!result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => line.Trim() == "Success"))
            throw new InvalidOperationException("Unity 数据清除未返回 Success：" + result.Output);
        log("已清除当前 Android 用户的 Unity 应用数据，APK 保留。相机配置需要重新部署。");
    }

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

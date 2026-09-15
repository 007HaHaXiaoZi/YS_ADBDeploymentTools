using System.Reflection;

namespace AdbDeploymentTool;

internal static class ApplicationIdentity
{
    public const string Name = "YS_ADBDeploymentTools";
    public const string Repository = "007HaHaXiaoZi/YS_ADBDeploymentTools";
    public const string ManifestUrl = "https://github.com/" + Repository + "/releases/latest/download/manifest.json";
    public static string PublisherPublicKey
    {
        get
        {
            using var stream = typeof(ApplicationIdentity).Assembly.GetManifestResourceStream("publisher-public.pem")
                ?? throw new InvalidOperationException("发布者公钥未嵌入构建。");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static string NormalizeUpdateUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "git@github.com:" + Repository + ".git" ||
            value.Trim().TrimEnd('/') is "https://github.com/007HaHaXiaoZi/YS_ADBDeploymentTools" or "https://github.com/007HaHaXiaoZi/YS_ADBDeploymentTools.git")
            return ManifestUrl;
        return value.Trim();
    }
}

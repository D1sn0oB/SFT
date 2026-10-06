namespace SFTLauncher.Download;

public static class DownloadSettings
{
    public const int DefaultParallelDownloads = 32;
    public const int MinParallelDownloads = 1;
    public const int MaxParallelDownloads = 32;

    public static int ParallelDownloads
    {
        get
        {
            var value = Properties.Settings.Default.DownloadParallelDownloads;
            return value >= MinParallelDownloads && value <= MaxParallelDownloads
                ? value
                : DefaultParallelDownloads;
        }
    }

    public static void SaveParallelDownloads(int count)
    {
        var clamped = Math.Clamp(count, MinParallelDownloads, MaxParallelDownloads);
        Properties.Settings.Default.DownloadParallelDownloads = clamped;
        TryPersist();
    }

    public static string ActiveSourceName
    {
        get => Properties.Settings.Default.DownloadActiveSource ?? DownloadSources.Bmcl.Name;
    }

    public static void SaveActiveSource(DownloadSource source)
    {
        Properties.Settings.Default.DownloadActiveSource = source.Name;
        TryPersist();
        DownloadSourceProvider.Active = source;
        DownloadSourceProvider.Fallback = source == DownloadSources.Official ? DownloadSources.Bmcl : DownloadSources.Official;
    }

    /// <summary>
    /// 尝试把用户设置落盘。user.config 无写权限时 <see cref="System.Configuration.ApplicationSettingsBase.Save"/>
    /// 会抛异常；配置落盘属于尽力而为，失败不应中断当前操作，
    /// 内存中的设置值依然生效。
    /// </summary>
    private static void TryPersist()
    {
        try
        {
            Properties.Settings.Default.Save();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"保存下载设置失败（已忽略，本次会话仍生效）：{exception.Message}");
        }
    }

    public static void ApplySavedSettings()
    {
        var activeName = ActiveSourceName;
        DownloadSourceProvider.Active = DownloadSources.All
            .FirstOrDefault(s => string.Equals(s.Name, activeName, StringComparison.OrdinalIgnoreCase))
            ?? DownloadSources.Bmcl;
        DownloadSourceProvider.Fallback = DownloadSourceProvider.Active == DownloadSources.Official
            ? DownloadSources.Bmcl : DownloadSources.Official;
    }
}

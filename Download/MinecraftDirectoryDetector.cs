using System.IO;

namespace SFTLauncher.Download;

/// <summary>一个候选的 Minecraft 目录及其状态。</summary>
public sealed class MinecraftDirectoryCandidate
{
    public required string Path { get; init; }
    public required string Source { get; init; }

    /// <summary>是否已安装可启动的版本（versions 下有 .jar）。</summary>
    public required bool HasInstalledVersions { get; init; }

    /// <summary>目录是否存在。</summary>
    public required bool Exists { get; init; }

    public int VersionCount { get; init; }
    public long TotalBytes { get; init; }

    public string Summary
    {
        get
        {
            if (HasInstalledVersions)
                return $"已安装 {VersionCount} 个版本 · {FormatSize(TotalBytes)}";
            if (Exists)
                return "空目录（将在此安装）";
            return "不存在（将新建）";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024 / 1024:F2} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / 1024d / 1024:F1} MB";
        return $"{bytes / 1024d:F0} KB";
    }
}

/// <summary>
/// Minecraft 目录自动检测。
///
/// 背景：原实现把目录写死在启动器自身目录下，
/// 用户实际安装在别处时既不提示也无法自动找到。
/// 这里按常见位置逐个探测，优先选择"已经装了版本"的目录。
/// </summary>
public static class MinecraftDirectoryDetector
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>按优先级枚举候选目录。</summary>
    public static List<MinecraftDirectoryCandidate> Enumerate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MinecraftDirectoryCandidate>();

        foreach (var (path, source) in CandidatePaths())
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var full = SafeFullPath(path);
            if (full is null || !seen.Add(full))
                continue;

            result.Add(Inspect(full, source));
        }

        // 已安装版本的排前面，其次按版本数、大小排序
        return result
            .OrderByDescending(c => c.HasInstalledVersions)
            .ThenByDescending(c => c.VersionCount)
            .ThenByDescending(c => c.TotalBytes)
            .ToList();
    }

    private static IEnumerable<(string Path, string Source)> CandidatePaths()
    {
        // 1) 官方启动器的默认位置（Roaming）
        yield return (DefaultDirectory, "官方默认位置");

        // 2) 用户主目录下的 .minecraft
        yield return (Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"), "用户目录");

        // 3) 本地 AppData
        yield return (Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".minecraft"), "本地 AppData");

        // 4) 启动器自身目录（便携模式 / 之前版本的默认值）
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
            yield return (Path.Combine(baseDir, ".minecraft"), "启动器目录");

        // 5) 常见第三方启动器的实例目录
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var launcher in new[] { "PCL", "HMCL", "BakaXL", "SFTLauncher" })
        {
            var path = Path.Combine(roaming, launcher, ".minecraft");
            if (Directory.Exists(path))
                yield return (path, $"{launcher} 目录");
        }
    }

    /// <summary>
    /// 选择要使用的目录：
    /// 依次尝试 已保存配置 → 已安装版本的目录 → 已存在的空目录 → 官方默认位置。
    /// </summary>
    public static (string Directory, string Reason) Resolve(string? configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory) &&
            !string.Equals(configuredDirectory, LauncherConfig.PlaceholderDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return (configuredDirectory, "使用上次保存的目录");
        }

        var candidates = Enumerate();

        var installed = candidates.FirstOrDefault(c => c.HasInstalledVersions);
        if (installed is not null)
            return (installed.Path, $"自动检测到已安装版本（{installed.Source}）");

        var existing = candidates.FirstOrDefault(c => c.Exists);
        if (existing is not null)
            return (existing.Path, $"自动检测到已有目录（{existing.Source}，尚未安装版本）");

        return (DefaultDirectory, "未检测到安装，将在默认位置新建");
    }

    /// <summary>确保目录及必要子目录存在。</summary>
    public static void EnsureCreated(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var sub in new[] { "versions", "libraries", "assets", "mods", "config", "saves", "resourcepacks" })
            Directory.CreateDirectory(Path.Combine(directory, sub));
    }

    /// <summary>检查目录是否包含可启动的版本。</summary>
    public static bool HasLaunchableVersion(string directory)
        => VersionScanner.Scan(directory).Any(v => v.IsLaunchable);

    private static MinecraftDirectoryCandidate Inspect(string path, string source)
    {
        var exists = Directory.Exists(path);
        var versions = exists ? VersionScanner.Scan(path) : new List<InstalledVersion>();
        var launchable = versions.Count(v => v.IsLaunchable);

        long bytes = 0;
        try
        {
            if (exists)
            {
                bytes = new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
        }
        catch
        {
            // 权限受限时忽略大小统计
        }

        return new MinecraftDirectoryCandidate
        {
            Path = path,
            Source = source,
            Exists = exists,
            HasInstalledVersions = launchable > 0,
            VersionCount = versions.Count,
            TotalBytes = bytes
        };
    }

    private static string? SafeFullPath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }
}

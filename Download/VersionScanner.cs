using System.IO;

namespace SFTLauncher.Download;

/// <summary>一个已安装（或部分安装）的 Minecraft 版本。</summary>
public sealed class InstalledVersion
{
    public required string VersionId { get; init; }

    /// <summary>版本 JSON 是否存在。缺失则无法启动。</summary>
    public required bool HasJson { get; init; }

    /// <summary>主 JAR 是否存在。</summary>
    public required bool HasJar { get; init; }

    public required long JarBytes { get; init; }
    public required string LoaderDisplay { get; init; }
    public required DateTime SortKey { get; init; }

    public bool IsLaunchable => HasJson && HasJar;

    public string DisplayName => $"Minecraft {VersionId}";

    /// <summary>加载器 + 安装状态，供列表单行显示（避免多 Run 绑定）。</summary>
    public string Subtitle => $"{LoaderDisplay} · {Detail}";

    public string Detail
    {
        get
        {
            if (IsLaunchable)
                return $"已安装 · {FormatSize(JarBytes)}";
            if (!HasJar && !HasJson)
                return "安装不完整 · 缺少主文件与版本描述";
            if (!HasJar)
                return "安装不完整 · 缺少主文件 (.jar)";
            return "安装不完整 · 缺少版本描述 (.json)";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024 / 1024:F2} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / 1024d / 1024:F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:F0} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// 扫描 Minecraft 目录下的 versions 子目录，得到已安装版本列表。
/// 主页与实例库共用，避免两处显示不一致。
/// </summary>
public static class VersionScanner
{
    public static List<InstalledVersion> Scan(string minecraftDirectory)
    {
        var result = new List<InstalledVersion>();
        if (string.IsNullOrWhiteSpace(minecraftDirectory))
            return result;

        try
        {
            var versionsDir = Path.Combine(minecraftDirectory, "versions");
            if (!Directory.Exists(versionsDir))
                return result;

            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var versionId = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(versionId))
                    continue;

                var jsonPath = Path.Combine(dir, $"{versionId}.json");
                var jarPath = Path.Combine(dir, $"{versionId}.jar");
                var hasJson = File.Exists(jsonPath);
                var hasJar = File.Exists(jarPath);

                // 只有残留临时文件的目录不算已安装
                if (!hasJson && !hasJar && !Directory.EnumerateFiles(dir).Any())
                    continue;

                result.Add(new InstalledVersion
                {
                    VersionId = versionId,
                    HasJson = hasJson,
                    HasJar = hasJar,
                    JarBytes = hasJar ? new FileInfo(jarPath).Length : 0,
                    LoaderDisplay = DetectLoader(dir, versionId),
                    SortKey = Directory.GetLastWriteTimeUtc(dir)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"扫描版本失败：{ex.Message}");
        }

        return result.OrderByDescending(v => v.SortKey).ToList();
    }

    /// <summary>识别目录中是否存在 Fabric / Forge / NeoForge 加载器。</summary>
    private static string DetectLoader(string versionDirectory, string versionId)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(versionDirectory))
            {
                var name = Path.GetFileName(file);
                if (name.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
                    return "NeoForge";
                if (name.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                    return "Fabric";
                if (name.Contains("forge", StringComparison.OrdinalIgnoreCase))
                    return "Forge";
            }
        }
        catch { }

        if (versionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
            return "Fabric";
        if (versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            return "NeoForge";
        if (versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
            return "Forge";

        return "原版";
    }
}

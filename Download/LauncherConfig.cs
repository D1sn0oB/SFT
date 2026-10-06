using System.IO;

namespace SFTLauncher.Download;

/// <summary>
/// 启动器全局路径配置的单一真源。
/// 设置页、版本下载页、实例库、启动器都从这里取路径，
/// 避免各处硬编码默认值导致「设置了却不生效」。
///
/// 持久化策略：<see cref="Properties.Settings"/>（user.config）为主，
/// 落盘失败时回退到 %AppData%\SFTLauncher\config.json，
/// 保证配置不会因为 user.config 不可写而丢失。
/// </summary>
public static class LauncherConfig
{
    private static string ConfigDirectory
    {
        get
        {
            // 允许测试或便携部署覆盖回退配置目录
            var overridden = Environment.GetEnvironmentVariable("SFT_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overridden))
                return overridden;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SFTLauncher");
        }
    }

    private static string JsonConfigPath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>默认 Minecraft 目录：%APPDATA%\.minecraft</summary>
    public static string DefaultMinecraftDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>
    /// 未保存任何目录时返回此占位符，用于触发自动检测。
    /// 旧版本会把启动器自身目录写进配置，那样会让检测逻辑误以为用户已选择目录。
    /// </summary>
    public const string PlaceholderDirectory = "\u0000unset";

    /// <summary>
    /// 已配置的 Minecraft 目录。
    ///
    /// 解析顺序：用户保存过的目录 → 自动检测（优先已安装版本的位置）→ 官方默认位置。
    /// 即使目录当前不存在也会返回，调用方负责创建（下载到新目录是合法场景）。
    /// </summary>
    public static string MinecraftDirectory
    {
        get
        {
            var configured = Properties.Settings.Default.DefaultMinecraftPath;
            if (string.IsNullOrWhiteSpace(configured))
                configured = ReadJsonValue("minecraftDirectory");

            var (directory, reason) = MinecraftDirectoryDetector.Resolve(configured);
            if (!string.Equals(reason, _lastResolveReason, StringComparison.Ordinal))
            {
                _lastResolveReason = reason;
                System.Diagnostics.Debug.WriteLine($"Minecraft 目录：{directory}（{reason}）");
            }
            return directory;
        }
    }

    private static string? _lastResolveReason;

    /// <summary>上次解析目录的原因，供界面提示。</summary>
    public static string LastResolveReason => _lastResolveReason ?? string.Empty;

    /// <summary>是否已由用户显式设置过目录。</summary>
    public static bool HasCustomMinecraftDirectory
    {
        get
        {
            var configured = Properties.Settings.Default.DefaultMinecraftPath;
            if (!string.IsNullOrWhiteSpace(configured))
                return true;
            return !string.IsNullOrWhiteSpace(ReadJsonValue("minecraftDirectory"));
        }
    }

    public static string JavaPath
    {
        get
        {
            var configured = Properties.Settings.Default.JavaPath;
            if (!string.IsNullOrWhiteSpace(configured))
                return configured;
            return ReadJsonValue("javaPath") ?? string.Empty;
        }
    }

    public static int MaxMemoryGb
    {
        get
        {
            var value = Properties.Settings.Default.MaxMemoryGb;
            if (value is >= 2 and <= 64)
                return value;
            var fromJson = ReadJsonValue("maxMemoryGb");
            return int.TryParse(fromJson, out var parsed) && parsed is >= 2 and <= 64 ? parsed : 4;
        }
    }

    public static void Save(string minecraftDirectory, string javaPath, int maxMemoryGb)
    {
        var normalizedMc = string.IsNullOrWhiteSpace(minecraftDirectory)
            ? DefaultMinecraftDirectory
            : minecraftDirectory.Trim();

        Properties.Settings.Default.DefaultMinecraftPath = normalizedMc;
        Properties.Settings.Default.JavaPath = javaPath?.Trim() ?? string.Empty;
        Properties.Settings.Default.MaxMemoryGb = Math.Clamp(maxMemoryGb, 2, 64);
        Persist(normalizedMc, javaPath?.Trim() ?? string.Empty, Math.Clamp(maxMemoryGb, 2, 64));
    }

    /// <summary>只更新目录（下载页选择目录后调用）。</summary>
    public static void SaveMinecraftDirectory(string minecraftDirectory)
    {
        if (string.IsNullOrWhiteSpace(minecraftDirectory))
            return;
        Save(minecraftDirectory, JavaPath, MaxMemoryGb);
    }

    /// <summary>把配置写入 user.config；失败时写入 JSON 回退，两者都失败也不抛给调用方。</summary>
    private static void Persist(string minecraftDirectory, string javaPath, int maxMemoryGb)
    {
        var savedToUserConfig = true;
        try
        {
            Properties.Settings.Default.Save();
        }
        catch (Exception exception)
        {
            savedToUserConfig = false;
            System.Diagnostics.Debug.WriteLine($"user.config 保存失败，改用 JSON 回退：{exception.Message}");
        }

        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var payload = new Dictionary<string, object?>
            {
                ["minecraftDirectory"] = minecraftDirectory,
                ["javaPath"] = javaPath,
                ["maxMemoryGb"] = maxMemoryGb,
                ["savedAtUtc"] = DateTime.UtcNow.ToString("o")
            };
            File.WriteAllText(JsonConfigPath,
                System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"回退配置写入失败（user.config ok={savedToUserConfig}）：{exception.Message}");
        }
    }

    private static string? ReadJsonValue(string key)
    {
        try
        {
            if (!File.Exists(JsonConfigPath))
                return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(JsonConfigPath));
            if (!document.RootElement.TryGetProperty(key, out var element))
                return null;
            return element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }
        catch
        {
            return null;
        }
    }
}

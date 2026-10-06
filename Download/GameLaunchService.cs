using System.IO;
using System.Windows;
using SFTLauncher.Utils;

namespace SFTLauncher.Download;

/// <summary>
/// 启动 Minecraft 版本的共用流程：解析 Java（必要时自动下载），再交给 MinecraftLauncher。
/// 主页与实例库共用，避免两处逻辑不一致。
/// </summary>
public static class GameLaunchService
{
    public sealed record LaunchRequest(string VersionId, string MinecraftDirectory);

    /// <summary>
    /// 尝试启动指定版本。会自动准备所需 Java，并给出可操作的失败原因。
    /// </summary>
    public static bool TryLaunch(LaunchRequest request, Window? owner = null)
    {
        var versionId = request.VersionId;
        var minecraftDir = request.MinecraftDirectory;

        var versionDir = Path.Combine(minecraftDir, "versions", versionId);
        var jsonPath = Path.Combine(versionDir, $"{versionId}.json");
        var jarPath = Path.Combine(versionDir, $"{versionId}.jar");

        if (!File.Exists(jarPath))
        {
            MessageBox.Show(
                $"版本 {versionId} 安装不完整，缺少主文件：\n{jarPath}\n\n请在「版本下载」中重新下载该版本。",
                "无法启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!File.Exists(jsonPath))
        {
            MessageBox.Show(
                $"版本 {versionId} 缺少版本描述文件 {versionId}.json。\n\n" +
                "缺少它就无法确定启动参数与所需的 Java 版本。\n" +
                "请在「版本下载」中重新下载该版本以补全文件。",
                "无法启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // 1) 确定所需 Java 版本
        var requiredJava = JavaRuntime.GetRequiredJavaVersion(jsonPath, versionId);

        // 2) 选择可用的 Java；不足时自动下载
        var javaExe = ResolveJava(requiredJava, versionId, owner);
        if (javaExe is null)
            return false;

        // 3) 启动
        var playerName = string.IsNullOrWhiteSpace(Environment.UserName) ? "Player" : Environment.UserName;

        // 按物理内存夹紧堆上限：曾有机器只有 3.91GB 内存却被分配 4GB 堆导致未响应
        var requestedMb = LauncherConfig.MaxMemoryGb * 1024;
        var safeMb = GameLauncher.RecommendMaxMemoryMb(requestedMb);

        var result = GameLauncher.Launch(new GameLauncher.LaunchOptions(
            VersionId: versionId,
            MinecraftDirectory: minecraftDir,
            JavaExecutable: javaExe,
            MaxMemoryMb: safeMb,
            PlayerName: playerName));

        if (!result.Success)
        {
            MessageBox.Show($"启动失败：{result.Error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            var note = safeMb != requestedMb
                ? $"\n\n注意：已按本机物理内存（约 {GetTotalRamGb():F1} GB）把内存上限从 {requestedMb} MB 调整为 {safeMb} MB。"
                : string.Empty;

            MessageBox.Show(
                $"Minecraft {versionId} 已启动（内存 {safeMb} MB）。{note}\n\n" +
                "游戏窗口可能需要在十几秒后才会出现，请耐心等待。\n" +
                $"若启动失败，日志位置：\n{GameLauncher.LogPath}",
                "已启动", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return result.Success;
    }

    private static double GetTotalRamGb() => GameLauncher.TotalPhysicalMemoryMb / 1024d;

    /// <summary>
    /// 取得满足版本要求的 Java；没有就询问并自动下载安装。
    /// </summary>
    public static string? ResolveJava(int requiredMajorVersion, string versionId, Window? owner = null)
    {
        var runtime = JavaRuntime.ResolveFor(requiredMajorVersion);
        if (runtime is not null && runtime.MajorVersion >= requiredMajorVersion)
        {
            // 记录下来，后续启动无需再次探测
            if (!string.Equals(LauncherConfig.JavaPath, runtime.Path, StringComparison.OrdinalIgnoreCase))
                LauncherConfig.Save(LauncherConfig.MinecraftDirectory, runtime.Path, LauncherConfig.MaxMemoryGb);
            return runtime.Path;
        }

        var found = runtime is null
            ? "本机未检测到任何 Java。"
            : $"本机检测到的最高 Java 版本为 {runtime.MajorVersion}，低于该版本需要的 Java {requiredMajorVersion}。";

        var answer = MessageBox.Show(
            $"{found}\n\nMinecraft {versionId} 需要 Java {requiredMajorVersion}。\n\n" +
            $"是否现在自动下载并安装 Java {requiredMajorVersion}？\n" +
            "（免安装、约 40-50 MB，安装到启动器目录，不影响系统其他程序）",
            "需要 Java 运行时", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return null;

        try
        {
            var javaExe = DownloadWithProgress(requiredMajorVersion, owner);
            if (string.IsNullOrEmpty(javaExe))
                return null;

            JavaRuntime.InvalidateCache();
            LauncherConfig.Save(LauncherConfig.MinecraftDirectory, javaExe, LauncherConfig.MaxMemoryGb);
            MessageBox.Show($"Java {requiredMajorVersion} 安装完成。\n\n位置：{javaExe}",
                "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            return javaExe;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"自动安装 Java 失败：{ex.Message}\n\n" +
                "你可以手动安装 Java 后，在「设置」中指定 Java 路径。",
                "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>带模态进度窗口地下载 Java。</summary>
    private static string? DownloadWithProgress(int majorVersion, Window? owner)
    {
        var dialog = new JavaDownloadWindow(majorVersion) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.InstalledJavaPath : null;
    }

    /// <summary>
    /// 补全某个版本缺失的文件（如缺少版本 JSON）。
    /// 已存在且校验通过的文件会被跳过，因此只补差异部分。
    /// </summary>
    public static async Task<(bool Success, string Message)> RepairAsync(
        string versionId, string minecraftDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            var versions = await ManifestGet.GetVersionsAsync().ConfigureAwait(false);
            var metadata = versions.FirstOrDefault(v =>
                string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Url))
                return (false, $"在版本清单中未找到 {versionId}，无法自动补全。");

            var installer = new MinecraftVersionInstaller();
            await installer.InstallAsync(versionId, metadata.Url, minecraftDirectory,
                progress: null, cancellationToken).ConfigureAwait(false);

            return (true, $"Minecraft {versionId} 的文件已补全。");
        }
        catch (Exception ex)
        {
            return (false, $"补全失败：{ex.Message}");
        }
    }
}

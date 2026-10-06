using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SFTLauncher.Models;

namespace SFTLauncher.Download;

public enum GameDownloadPhase
{
    Idle,
    Preparing,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

public sealed record GameDownloadSnapshot(
    long Revision,
    long TaskId,
    GameDownloadPhase Phase,
    string VersionId,
    int StageIndex,
    string StageName,
    string Detail,
    double Percentage,
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    double BytesPerSecond)
{
    public static GameDownloadSnapshot Idle { get; } = new(
        0, 0, GameDownloadPhase.Idle, string.Empty, 0,
        "尚无下载任务", "请选择 Minecraft 版本进行下载。", 0, 0, 0, 0, 0, 0);

    public bool HasTask => Phase != GameDownloadPhase.Idle;
    public bool IsActive => Phase is GameDownloadPhase.Preparing or GameDownloadPhase.Downloading;
    public bool IsTerminal => Phase is GameDownloadPhase.Completed or
        GameDownloadPhase.Failed or GameDownloadPhase.Cancelled;
}

/// <summary>
/// 包装 MinecraftVersionInstaller，通过阶段/快照状态机对外发布下载进度。
/// 移植自 NyaLauncher.Core.Download.GameDownloadService
/// </summary>
public sealed class GameDownloadService
{
    public static readonly string[] StageNames =
    [
        "获取版本元数据",
        "分析下载清单",
        "下载游戏客户端",
        "下载依赖库",
        "下载资源索引",
        "下载游戏资源",
        "完成校验与安装"
    ];

    private readonly object _gate = new();
    private readonly MinecraftVersionInstaller _installer = new();
    private GameDownloadSnapshot _current = GameDownloadSnapshot.Idle;
    private CancellationTokenSource? _activeTask;
    private long _revision;
    private long _taskId;

    public GameDownloadSnapshot Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public event Action<GameDownloadSnapshot>? Changed;

    /// <summary>
    /// 开始下载原版 Minecraft。
    /// 游戏目录从 LauncherConfig.GameDirectory 或默认路径获取。
    /// </summary>
    public async Task<bool> StartAsync(
        MinecraftVersion version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (string.IsNullOrWhiteSpace(version.Id) || string.IsNullOrWhiteSpace(version.Url))
            return false;

        var minecraftDir = GetMinecraftDirectory();

        return await RunDownloadTaskAsync(
            instanceId: version.Id,
            displayName: version.DisplayName,
            completedDetail: $"{version.DisplayName} 下载并安装完成",
            installAsync: async (progress, taskCt) =>
                await _installer.InstallAsync(version.Id, version.Url, minecraftDir, progress, taskCt)
                    .ConfigureAwait(false),
            cancellationToken);
    }

    /// <summary>
    /// 开始下载带 Mod Loader 的实例。
    /// </summary>
    public async Task<bool> StartModLoaderAsync(
        MinecraftVersion version,
        string loaderType,
        string loaderVersion,
        string instanceName,
        CancellationToken cancellationToken = default)
    {
        // TODO: 实现 Mod Loader 下载逻辑
        throw new NotImplementedException("Mod Loader 下载暂未实现");
    }

    private async Task<bool> RunDownloadTaskAsync(
        string instanceId,
        string displayName,
        string completedDetail,
        Func<IProgress<MinecraftInstallProgress>, CancellationToken, Task> installAsync,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource taskCancellation;
        long taskId;
        lock (_gate)
        {
            if (_activeTask is not null)
                return false;
            taskCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeTask = taskCancellation;
            taskId = ++_taskId;
        }

        Publish(new GameDownloadSnapshot(
            NextRevision(), taskId, GameDownloadPhase.Preparing,
            instanceId, 1, StageNames[0],
            $"正在准备下载 {displayName}", 0, 0, 0, 0, 0, 0));

        try
        {
            var progress = new InlineProgress<MinecraftInstallProgress>(update =>
            {
                if (taskCancellation.IsCancellationRequested || taskId != Volatile.Read(ref _taskId))
                    return;
                Publish(new GameDownloadSnapshot(
                    NextRevision(), taskId, GameDownloadPhase.Downloading,
                    instanceId, update.StageIndex, update.StageName, update.Detail,
                    update.Percentage, update.CompletedBytes, update.TotalBytes,
                    update.CompletedFiles, update.TotalFiles, update.BytesPerSecond));
            });

            await installAsync(progress, taskCancellation.Token)
                .ConfigureAwait(false);

            var previous = Current;
            Publish(previous with
            {
                Revision = NextRevision(),
                Phase = GameDownloadPhase.Completed,
                StageIndex = MinecraftVersionInstaller.StageCount,
                StageName = StageNames[^1],
                Detail = completedDetail,
                Percentage = 100
            });
            return true;
        }
        catch (OperationCanceledException)
        {
            PublishTerminal(taskId, GameDownloadPhase.Cancelled, "下载已取消", $"{displayName} 下载已取消");
            return false;
        }
        catch (Exception exception)
        {
            PublishTerminal(taskId, GameDownloadPhase.Failed, "下载失败", exception.Message);
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeTask, taskCancellation))
                {
                    _activeTask.Dispose();
                    _activeTask = null;
                }
            }
        }
    }

    public bool CancelActive()
    {
        lock (_gate)
        {
            if (_activeTask is null)
                return false;
            _activeTask.Cancel();
            return true;
        }
    }

    private void PublishTerminal(long taskId, GameDownloadPhase phase, string stageName, string detail)
    {
        var previous = Current;
        if (previous.TaskId != taskId)
            return;
        Publish(previous with { Revision = NextRevision(), Phase = phase, StageName = stageName, Detail = detail });
    }

    private long NextRevision() => Interlocked.Increment(ref _revision);

    private void Publish(GameDownloadSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.Revision == 0)
                snapshot = snapshot with { Revision = Interlocked.Increment(ref _revision) };
            if (snapshot.Revision < _current.Revision)
                return;
            _current = snapshot;
        }

        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (Action<GameDownloadSnapshot> subscriber in handlers.GetInvocationList())
        {
            try { subscriber(snapshot); }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"GameDownloadService.Changed 订阅者异常：{exception}");
            }
        }
    }

    private static string GetMinecraftDirectory() => LauncherConfig.MinecraftDirectory;

    /// <summary>
    /// 设置游戏目录（用户选择后调用）。
    /// 配置持久化失败不得中断下载：本次会话仍使用传入目录，仅放弃落盘。
    /// </summary>
    public static void SetMinecraftDirectory(string path) => LauncherConfig.SaveMinecraftDirectory(path);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

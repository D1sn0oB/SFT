using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SFTLauncher.Download;

/// <summary>
/// 安装进度快照。移植自 NyaLauncher.Core.Download.MinecraftVersionInstaller
/// </summary>
public sealed record MinecraftInstallProgress(
    int StageIndex,
    string StageName,
    string Detail,
    long CompletedBytes,
    long TotalBytes,
    int CompletedFiles,
    int TotalFiles,
    double BytesPerSecond)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(CompletedBytes * 100d / TotalBytes, 0, 100);
}

/// <summary>
/// Minecraft 版本下载与安装引擎。
/// 核心功能：并发下载、SHA-1 校验、断点续传、多源回退、原子写入。
/// 移植自 NyaLauncher.Core.Download.MinecraftVersionInstaller
/// </summary>
public sealed class MinecraftVersionInstaller
{
    public const int StageCount = 7;
    private const int BufferSize = 128 * 1024;
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(120);
    private static readonly long ProgressIntervalStopwatchTicks =
        (long)(Stopwatch.Frequency * ProgressInterval.TotalSeconds);
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SFTLauncher/1.0");
        return client;
    }

    public async Task InstallAsync(
        string versionId,
        string metadataUrl,
        string minecraftDirectory,
        IProgress<MinecraftInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftDirectory);
        ValidateVersionId(versionId);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(minecraftDirectory));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "versions"));

        Report(progress, 1, "获取版本元数据", $"正在读取 Minecraft {versionId} 的版本描述", 0, 0, 0, 0, 0);
        var metadataBytes = await DownloadBytesAsync(metadataUrl, cancellationToken)
            .ConfigureAwait(false);

        await InstallFromMetadataBytesAsync(versionId, root, metadataBytes, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task InstallFromMetadataBytesAsync(
        string versionId,
        string root,
        byte[] metadataBytes,
        IProgress<MinecraftInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateVersionId(versionId);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "versions"));

        Report(progress, 2, "分析下载清单", "正在整理客户端、依赖库与资源索引", 0, 0, 0, 0, 0);
        using var metadata = JsonDocument.Parse(metadataBytes);
        var versionDirectory = Path.Combine(root, "versions", versionId);
        Directory.CreateDirectory(versionDirectory);

        var clientFiles = CreateClientPlan(metadata.RootElement, versionId, versionDirectory);
        var libraryFiles = CreateLibraryPlan(metadata.RootElement, root);
        var assetIndexFile = CreateAssetIndexPlan(metadata.RootElement, root);

        var stopwatch = Stopwatch.StartNew();
        var counters = new InstallCounters();
        counters.AddTotalBytes(clientFiles.Concat(libraryFiles).Sum(file => Math.Max(0, file.Size)));
        counters.AddTotalFiles(clientFiles.Count + libraryFiles.Count);
        if (assetIndexFile is not null)
        {
            counters.AddTotalBytes(Math.Max(0, assetIndexFile.Size));
            counters.AddTotalFiles(1);
        }

        await DownloadStageAsync(3, "下载游戏客户端", clientFiles, progress, counters, stopwatch, cancellationToken)
            .ConfigureAwait(false);
        await DownloadStageAsync(4, "下载依赖库", libraryFiles, progress, counters, stopwatch, cancellationToken)
            .ConfigureAwait(false);

        if (assetIndexFile is not null)
        {
            await DownloadStageAsync(5, "下载资源索引", [assetIndexFile], progress, counters, stopwatch, cancellationToken)
                .ConfigureAwait(false);
        }

        var assetFiles = assetIndexFile is not null
            ? await CreateAssetPlanAsync(assetIndexFile.TargetPath, root, cancellationToken)
                  .ConfigureAwait(false)
            : [];
        counters.AddTotalBytes(assetFiles.Sum(file => Math.Max(0, file.Size)));
        counters.AddTotalFiles(assetFiles.Count);
        await DownloadStageAsync(6, "下载游戏资源", assetFiles, progress, counters, stopwatch, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, 7, "完成校验与安装", "正在写入版本描述并完成安装",
            counters.CompletedBytes, counters.TotalBytes, counters.CompletedFiles, counters.TotalFiles,
            CalculateSpeed(counters.NetworkBytes, stopwatch));

        var versionJsonPath = Path.Combine(versionDirectory, $"{versionId}.json");
        await WriteAllBytesAtomicallyAsync(versionJsonPath, metadataBytes, cancellationToken)
            .ConfigureAwait(false);

        Report(progress, 7, "完成校验与安装", $"Minecraft {versionId} 已安装完成",
            counters.TotalBytes, counters.TotalBytes, counters.TotalFiles, counters.TotalFiles,
            CalculateSpeed(counters.NetworkBytes, stopwatch));
    }

    private static IReadOnlyList<DownloadFile> CreateClientPlan(JsonElement root, string versionId, string versionDirectory)
    {
        if (!root.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("client", out var client))
            return [];

        return
        [
            CreateDownloadFile(client, Path.Combine(versionDirectory, $"{versionId}.jar"), "客户端文件")
        ];
    }

    private static IReadOnlyList<DownloadFile> CreateLibraryPlan(JsonElement root, string minecraftDirectory)
    {
        if (!root.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
            return [];

        var result = new Dictionary<string, DownloadFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries.EnumerateArray())
        {
            if (!IsLibraryAllowed(library))
                continue;

            if (TryPlanDownloadsArtifact(library, result, minecraftDirectory))
                continue;

            PlanCoordinateLibrary(library, result, minecraftDirectory);
        }

        return result.Values.ToArray();
    }

    private static bool TryPlanDownloadsArtifact(JsonElement library, IDictionary<string, DownloadFile> result, string minecraftDirectory)
    {
        if (!library.TryGetProperty("downloads", out var downloads))
            return false;

        if (downloads.TryGetProperty("artifact", out var artifact))
            AddLibraryDownload(result, artifact, minecraftDirectory, "依赖库");

        return true;
    }

    private static void PlanCoordinateLibrary(JsonElement library, IDictionary<string, DownloadFile> result, string minecraftDirectory)
    {
        if (!library.TryGetProperty("name", out var nameElement))
            return;
        var libraryName = nameElement.GetString();
        var relativePath = CreateMavenPath(libraryName);
        if (relativePath is null)
            return;

        var baseUrl = library.TryGetProperty("url", out var urlElement)
            ? urlElement.GetString()
            : InferMavenRepository(libraryName);
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = "https://libraries.minecraft.net/";

        var url = $"{baseUrl.TrimEnd('/')}/{relativePath.Replace('\\', '/')}";
        var target = ResolveRelativePath(Path.Combine(minecraftDirectory, "libraries"), relativePath);
        result[target] = new DownloadFile(url, target, null, 0, "依赖库");
    }

    private static bool IsLibraryAllowed(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!MatchesRule(rule))
                continue;
            allowed = rule.TryGetProperty("action", out var action) &&
                      action.GetString() == "allow";
        }
        return allowed;
    }

    private static bool MatchesRule(JsonElement rule)
    {
        if (rule.TryGetProperty("os", out var os))
        {
            if (os.TryGetProperty("name", out var osName) &&
                !string.Equals(osName.GetString(), GetOperatingSystemName(), StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string GetOperatingSystemName() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    private static DownloadFile? CreateAssetIndexPlan(JsonElement root, string minecraftDirectory)
    {
        if (!root.TryGetProperty("assetIndex", out var assetIndex))
            return null;
        var id = assetIndex.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : root.TryGetProperty("assets", out var assetsElement)
                ? assetsElement.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidDataException("资源索引缺少 ID。");

        ValidateVersionId(id);
        var indexPath = ResolveRelativePath(Path.Combine(minecraftDirectory, "assets", "indexes"), $"{id}.json");
        return CreateDownloadFile(assetIndex, indexPath, "资源索引");
    }

    private static async Task<IReadOnlyList<DownloadFile>> CreateAssetPlanAsync(
        string indexPath, string minecraftDirectory, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(indexPath);
        using var index = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!index.RootElement.TryGetProperty("objects", out var objects) ||
            objects.ValueKind != JsonValueKind.Object)
            return [];

        var result = new Dictionary<string, DownloadFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in objects.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!asset.Value.TryGetProperty("hash", out var hashElement))
                continue;
            var hash = hashElement.GetString();
            if (!IsSha1(hash))
                continue;
            var size = asset.Value.TryGetProperty("size", out var sizeElement) &&
                       sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
            var relativePath = Path.Combine(hash![..2], hash);
            var target = ResolveRelativePath(Path.Combine(minecraftDirectory, "assets", "objects"), relativePath);
            result[target] = new DownloadFile(
                $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                target, hash, size, asset.Name);
        }
        return result.Values.ToArray();
    }

    private static async Task DownloadStageAsync(
        int stageIndex, string stageName, IReadOnlyList<DownloadFile> files,
        IProgress<MinecraftInstallProgress>? progress, InstallCounters counters,
        Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            Report(progress, stageIndex, stageName, "此阶段没有需要下载的文件",
                counters.CompletedBytes, counters.TotalBytes, counters.CompletedFiles, counters.TotalFiles, 0);
            return;
        }

        using var semaphore = new SemaphoreSlim(DownloadSettings.ParallelDownloads);
        var currentFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        async Task DownloadOneAsync(DownloadFile file)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                currentFiles.TryAdd(file.TargetPath, 0);
                var reusedBytes = await DownloadFileAsync(file, counters.AddProgressBytes, () =>
                    ReportThrottled(progress, stageIndex, stageName, file, currentFiles.Count, counters, stopwatch),
                    cancellationToken).ConfigureAwait(false);
                if (reusedBytes > 0)
                    counters.AddCompleted(reusedBytes);
                counters.IncrementCompletedFiles();
                ReportThrottled(progress, stageIndex, stageName, file, currentFiles.Count, counters, stopwatch);
            }
            finally
            {
                currentFiles.TryRemove(file.TargetPath, out _);
                semaphore.Release();
            }
        }

        await Task.WhenAll(files.Select(DownloadOneAsync)).ConfigureAwait(false);
        Report(progress, stageIndex, stageName, $"{stageName}完成",
            counters.CompletedBytes, counters.TotalBytes, counters.CompletedFiles, counters.TotalFiles,
            CalculateSpeed(counters.NetworkBytes, stopwatch));
    }

    private static void ReportThrottled(IProgress<MinecraftInstallProgress>? progress, int stageIndex, string stageName,
        DownloadFile file, int activeFileCount, InstallCounters counters, Stopwatch stopwatch)
    {
        var now = stopwatch.ElapsedTicks;
        var previous = Interlocked.Read(ref counters.LastReportTicks);
        if (now - previous < ProgressIntervalStopwatchTicks ||
            Interlocked.CompareExchange(ref counters.LastReportTicks, now, previous) != previous)
            return;

        Report(progress, stageIndex, stageName,
            $"正在处理 {activeFileCount} 个文件 · {file.DisplayName}",
            counters.CompletedBytes, counters.TotalBytes, counters.CompletedFiles, counters.TotalFiles,
            CalculateSpeed(counters.NetworkBytes, stopwatch));
    }

    private static async Task<long> DownloadFileAsync(
        DownloadFile file, Action<long> addProgressBytes, Action reportProgress,
        CancellationToken cancellationToken)
    {
        if (await IsExistingFileValidAsync(file, cancellationToken).ConfigureAwait(false))
            return file.Size > 0 ? file.Size : new FileInfo(file.TargetPath).Length;

        var directory = Path.GetDirectoryName(file.TargetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = $"{file.TargetPath}.sft-download";

        var resolvedUrl = DownloadSourceProvider.Resolve(file.Url);
        var fallbackUrl = DownloadSourceProvider.ResolveFallback(file.Url);
        var urls = fallbackUrl is not null && !string.Equals(resolvedUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase)
            ? new[] { resolvedUrl, fallbackUrl }
            : new[] { resolvedUrl };

        Exception? lastException = null;
        foreach (var url in urls)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var perFileCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var stallWatchdog = new InstallStallWatchdog(perFileCts, StallTimeout);
                var fileCt = perFileCts.Token;

                var attemptBytes = new AttemptBytes();
                try
                {
                    try
                    {
                        await DownloadToTemporaryAsync(url, temporaryPath, attemptBytes, addProgressBytes,
                            reportProgress, stallWatchdog, fileCt).ConfigureAwait(false);
                        stallWatchdog.Touch();
                        ValidateTemporaryFile(file, temporaryPath);
                        File.Move(temporaryPath, file.TargetPath, overwrite: true);
                        return 0;
                    }
                    catch
                    {
                        var written = Interlocked.Read(ref attemptBytes.Value);
                        if (written > 0)
                        {
                            try { addProgressBytes(-written); } catch { }
                        }
                        TryDeleteFile(temporaryPath);
                        throw;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    lastException = fileCt.IsCancellationRequested
                        ? new IOException($"下载连接停滞：{file.DisplayName}")
                        : ex;
                    continue;
                }
            }
        }

        throw lastException ?? new InvalidOperationException($"下载失败：{file.DisplayName}");
    }

    private static async Task DownloadToTemporaryAsync(
        string url, string temporaryPath, AttemptBytes attemptBytes,
        Action<long> addProgressBytes, Action reportProgress,
        InstallStallWatchdog stallWatchdog, CancellationToken fileCt)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ValidateHttpsUrl(url));
        using var response = await HttpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, fileCt)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(fileCt).ConfigureAwait(false);
        await using (var destination = new FileStream(temporaryPath, FileMode.Create,
                     FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            while (true)
            {
                var read = await source.ReadAsync(buffer, fileCt).ConfigureAwait(false);
                if (read == 0) break;
                stallWatchdog.Touch();
                await destination.WriteAsync(buffer.AsMemory(0, read), fileCt).ConfigureAwait(false);
                Interlocked.Add(ref attemptBytes.Value, read);
                addProgressBytes(read);
                reportProgress();
            }
        }
    }

    private static void ValidateTemporaryFile(DownloadFile file, string temporaryPath)
    {
        if (!MatchesSha1(temporaryPath, file.Sha1))
            throw new InvalidDataException($"下载文件校验失败：{file.DisplayName}");
        if (file.Size > 0 && new FileInfo(temporaryPath).Length != file.Size)
            throw new InvalidDataException($"下载文件大小不匹配：{file.DisplayName}");
    }

    private static async Task<bool> IsExistingFileValidAsync(DownloadFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(file.TargetPath))
            return false;
        if (file.Size > 0 && new FileInfo(file.TargetPath).Length != file.Size)
            return false;
        return await Task.Run(() => MatchesSha1(file.TargetPath, file.Sha1), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool MatchesSha1(string path, string? expectedSha1)
    {
        if (string.IsNullOrWhiteSpace(expectedSha1))
            return true;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: false);
        var hash = SHA1.HashData(stream);
        return string.Equals(BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant(), expectedSha1, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken) =>
        DownloadSourceProvider.GetBytesAsync(url, cancellationToken: cancellationToken);

    private static async Task WriteAllBytesAtomicallyAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.sft-download";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static DownloadFile CreateDownloadFile(JsonElement element, string targetPath, string displayName)
    {
        var url = element.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidDataException($"{displayName}缺少下载地址。");
        var sha1 = element.TryGetProperty("sha1", out var sha1Element) ? sha1Element.GetString() : null;
        var size = element.TryGetProperty("size", out var sizeElement) &&
                   sizeElement.TryGetInt64(out var parsedSize) ? parsedSize : 0;
        return new DownloadFile(url, targetPath, sha1, size, displayName);
    }

    private static void AddLibraryDownload(IDictionary<string, DownloadFile> result, JsonElement element,
        string minecraftDirectory, string displayName)
    {
        if (!element.TryGetProperty("path", out var pathElement))
            return;
        var relativePath = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(relativePath))
            return;
        var target = ResolveRelativePath(Path.Combine(minecraftDirectory, "libraries"), relativePath);
        result[target] = CreateDownloadFile(element, target, Path.GetFileName(target) ?? displayName);
    }

    internal static string? CreateMavenPath(string? coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
            return null;
        var extension = "jar";
        var name = coordinate;
        var extensionSeparator = coordinate.IndexOf('@');
        if (extensionSeparator >= 0)
        {
            extension = coordinate[(extensionSeparator + 1)..];
            name = coordinate[..extensionSeparator];
        }
        var parts = name.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrWhiteSpace))
            return null;
        var groupPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var classifier = parts.Length == 4 ? $"-{parts[3]}" : string.Empty;
        return Path.Combine(groupPath, parts[1], parts[2], $"{parts[1]}-{parts[2]}{classifier}.{extension}");
    }

    private static string ResolveRelativePath(string root, string relativePath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException($"下载路径超出 Minecraft 目录：{relativePath}");
        return target;
    }

    private static Uri ValidateHttpsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"下载地址不是有效的 HTTPS URL：{url}");
        return uri;
    }

    private static bool IsSha1(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static double CalculateSpeed(long completedBytes, Stopwatch stopwatch) =>
        stopwatch.Elapsed.TotalSeconds <= 0 ? 0 : completedBytes / stopwatch.Elapsed.TotalSeconds;

    private static void Report(IProgress<MinecraftInstallProgress>? progress, int stageIndex, string stageName,
        string detail, long completedBytes, long totalBytes, long completedFiles, long totalFiles, double speed) =>
        progress?.Report(new MinecraftInstallProgress(stageIndex, stageName, detail,
            completedBytes, totalBytes, (int)completedFiles, (int)totalFiles, speed));

    internal static void ValidateVersionId(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
            throw new ArgumentException("版本 ID 不能为空。", nameof(versionId));
        if (versionId is "." or ".." ||
            versionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            versionId.Contains('/') || versionId.Contains('\\'))
            throw new ArgumentException($"版本 ID 包含不安全字符：{versionId}", nameof(versionId));
    }

    private static string? InferMavenRepository(string? coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
            return "https://libraries.minecraft.net/";
        if (coordinate.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
            return "https://maven.neoforged.net/releases/";
        if (coordinate.StartsWith("net.minecraftforge", StringComparison.OrdinalIgnoreCase))
            return "https://maven.minecraftforge.net/";
        if (coordinate.StartsWith("net.fabricmc", StringComparison.OrdinalIgnoreCase))
            return "https://maven.fabricmc.net/";
        return "https://libraries.minecraft.net/";
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class InstallCounters
    {
        public long LastReportTicks;
        private long _completedBytes, _networkBytes, _completedFiles, _totalBytes, _totalFiles;
        public long CompletedBytes => Volatile.Read(ref _completedBytes);
        public long NetworkBytes => Volatile.Read(ref _networkBytes);
        public long CompletedFiles => Volatile.Read(ref _completedFiles);
        public long TotalBytes => Volatile.Read(ref _totalBytes);
        public long TotalFiles => Volatile.Read(ref _totalFiles);
        public void AddProgressBytes(long value)
        {
            if (value > 0)
                Interlocked.Add(ref _networkBytes, value);
            Interlocked.Add(ref _completedBytes, value);
        }
        public void AddCompleted(long value) => Interlocked.Add(ref _completedBytes, value);
        public void IncrementCompletedFiles() => Interlocked.Increment(ref _completedFiles);
        public void AddTotalBytes(long value) => Interlocked.Add(ref _totalBytes, value);
        public void AddTotalFiles(int value) => Interlocked.Add(ref _totalFiles, value);
    }

    private sealed record DownloadFile(string Url, string TargetPath, string? Sha1, long Size, string DisplayName);
    private sealed class AttemptBytes { public long Value; }
}

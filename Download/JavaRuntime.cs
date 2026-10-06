using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace SFTLauncher.Download;

/// <summary>
/// Java 运行时检测与自动安装。
/// 数据源为 Eclipse Adoptium (Temurin) 官方 API，
/// 直接下载免安装的 JRE 压缩包解压到启动器目录，无需管理员权限。
/// </summary>
public static class JavaRuntime
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SFTLauncher/1.0");
        return client;
    }

    /// <summary>
    /// 免安装 JRE 的候选存放目录，按优先级排列。
    /// 某些受管环境（沙箱/组策略/安全软件）会拒绝对 AppData 下新建目录的写入，
    /// 因此提供多个候选位置，逐个尝试。
    /// </summary>
    public static IEnumerable<string> RuntimeRootCandidates()
    {
        var overridden = Environment.GetEnvironmentVariable("SFT_JAVA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            yield return overridden;
            yield break;
        }

        // 1) 启动器自身目录（跟随启动器，通常可写）
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
            yield return Path.Combine(baseDir, "java");

        // 2) 用户本地 AppData
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SFTLauncher", "java");

        // 3) 用户漫游 AppData
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SFTLauncher", "java");
    }

    /// <summary>默认安装目录（第一个候选），供界面显示。</summary>
    public static string RuntimeRoot => RuntimeRootCandidates().First();

    public sealed record JavaRuntimeInfo(int MajorVersion, string Path, bool IsAutoInstalled);

    private static List<JavaRuntimeInfo>? _cached;
    private static readonly object CacheLock = new();

    /// <summary>
    /// 探测本机可用的 Java。结果会缓存：探测需要启动 java 进程读版本，
    /// 在界面线程反复执行会明显卡顿。安装新运行时后调用 <see cref="InvalidateCache"/>。
    /// </summary>
    public static IEnumerable<JavaRuntimeInfo> Discover()
    {
        lock (CacheLock)
        {
            if (_cached is not null)
                return _cached;
        }

        var found = DiscoverUncached().ToList();
        lock (CacheLock)
        {
            _cached = found;
        }
        return found;
    }

    /// <summary>清除探测缓存（安装或更换 Java 后调用）。</summary>
    public static void InvalidateCache()
    {
        lock (CacheLock)
        {
            _cached = null;
        }
    }

    private static IEnumerable<JavaRuntimeInfo> DiscoverUncached()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 自动安装的运行时
        foreach (var javaExe in EnumerateInstalledRuntimes())
        {
            var version = GetMajorVersion(javaExe);
            if (version > 0 && seen.Add(javaExe))
                yield return new JavaRuntimeInfo(version, javaExe, true);
        }

        // 2) 用户在设置里配置的路径
        var configured = LauncherConfig.JavaPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            var version = GetMajorVersion(configured);
            if (version > 0 && seen.Add(configured))
                yield return new JavaRuntimeInfo(version, configured, false);
        }

        // 3) JAVA_HOME
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate))
            {
                var version = GetMajorVersion(candidate);
                if (version > 0 && seen.Add(candidate))
                    yield return new JavaRuntimeInfo(version, candidate, false);
            }
        }

        // 4) 系统常见安装位置
        foreach (var candidate in EnumerateSystemCandidates())
        {
            if (!File.Exists(candidate))
                continue;
            var version = GetMajorVersion(candidate);
            if (version > 0 && seen.Add(candidate))
                yield return new JavaRuntimeInfo(version, candidate, false);
        }

        // 5) PATH 中的 java
        var fromPath = FindOnPath();
        if (fromPath is not null)
        {
            var version = GetMajorVersion(fromPath);
            if (version > 0 && seen.Add(fromPath))
                yield return new JavaRuntimeInfo(version, fromPath, false);
        }
    }

    /// <summary>
    /// 为指定 Minecraft 版本挑选合适的 Java：优先满足版本要求且版本号最小的可用运行时。
    /// </summary>
    public static JavaRuntimeInfo? ResolveFor(int requiredMajorVersion)
    {
        var runtimes = Discover().ToList();
        if (runtimes.Count == 0)
            return null;

        var suitable = runtimes
            .Where(r => r.MajorVersion >= requiredMajorVersion)
            .OrderBy(r => r.MajorVersion)
            .FirstOrDefault();

        // 没有满足的版本时，返回版本最高的，让启动阶段给出更准确的提示
        return suitable ?? runtimes.OrderByDescending(r => r.MajorVersion).First();
    }

    /// <summary>读取版本 JSON 里的 javaVersion.majorVersion；缺失时按发布年份保守推断。</summary>
    public static int GetRequiredJavaVersion(string versionJsonPath, string versionId)
    {
        try
        {
            if (File.Exists(versionJsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(versionJsonPath));
                if (document.RootElement.TryGetProperty("javaVersion", out var javaVersion) &&
                    javaVersion.TryGetProperty("majorVersion", out var major) &&
                    major.TryGetInt32(out var value) && value > 0)
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"解析 javaVersion 失败（{versionId}）：{ex.Message}");
        }

        return InferJavaVersion(versionId);
    }

    /// <summary>版本 JSON 缺少 javaVersion 时，按版本号推断所需 Java。</summary>
    internal static int InferJavaVersion(string versionId)
    {
        // 1.20.5 起要求 Java 21；1.18 起要求 Java 17
        if (TryParseVersion(versionId, out var major, out var minor))
        {
            if (major >= 2)
                return 21;                       // 新式版本号（如 26.3）
            if (major == 1 && minor >= 21)
                return 21;
            if (major == 1 && minor >= 18)
                return 17;
            if (major == 1 && minor >= 17)
                return 16;
            return 8;
        }
        return 21;
    }

    private static bool TryParseVersion(string versionId, out int major, out int minor)
    {
        major = minor = 0;
        var parts = versionId.Split('.');
        if (parts.Length < 2)
            return false;
        return int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor);
    }

    /// <summary>
    /// 查询 Adoptium 可用的主版本，返回不小于 <paramref name="minimumMajor"/> 的最小可用版本。
    /// </summary>
    public static async Task<int> ResolveDownloadableMajorVersionAsync(
        int minimumMajor, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "https://api.adoptium.net/v3/info/available_releases";
            using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("available_releases", out var releases) &&
                releases.ValueKind == JsonValueKind.Array)
            {
                var available = releases.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => e.GetInt32())
                    .OrderBy(v => v)
                    .ToList();

                var match = available.FirstOrDefault(v => v >= minimumMajor);
                if (match > 0)
                    return match;

                if (available.Count > 0)
                    return available[^1];
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"查询 Adoptium 可用版本失败：{ex.Message}");
        }

        return minimumMajor;
    }

    /// <summary>
    /// 下载并解压免安装 JRE。
    /// </summary>
    /// <param name="majorVersion">要下载的 Java 主版本。</param>
    /// <param name="progress">进度回调：(已下载字节, 总字节, 阶段描述)。</param>
    public static async Task<string> InstallAsync(
        int majorVersion,
        IProgress<(long Downloaded, long Total, string Stage)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var actualVersion = await ResolveDownloadableMajorVersionAsync(majorVersion, cancellationToken)
            .ConfigureAwait(false);

        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "x64",
                System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
                System.Runtime.InteropServices.Architecture.X86 => "x86",
                _ => "x64"
            };

        // 下载到临时文件，随后尝试各候选目录解压，避免重复下载
        progress?.Report((0, 0, $"正在准备下载 Java {actualVersion}（{arch}）..."));
        var downloadUrls = await ResolveDownloadUrlsAsync(actualVersion, arch, cancellationToken)
            .ConfigureAwait(false);
        var tempZip = Path.Combine(ResolveDownloadTempDirectory(), $"sft-jre-{actualVersion}-{arch}.zip");
        await DownloadWithFallbackAsync(downloadUrls, tempZip, actualVersion, progress, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var errors = new List<string>();
            foreach (var root in RuntimeRootCandidates())
            {
                var extractDir = Path.Combine(root, $"jre-{actualVersion}");
                try
                {
                    // 已安装则直接复用
                    var installed = FindJavaExe(extractDir);
                    if (installed is not null && GetMajorVersion(installed) >= majorVersion)
                        return installed;

                    progress?.Report((0, 0, $"正在解压 Java 运行时到 {root} ..."));

                    Directory.CreateDirectory(root);
                    if (Directory.Exists(extractDir))
                        Directory.Delete(extractDir, recursive: true);

                    ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

                    var javaExe = FindJavaExe(extractDir);
                    if (javaExe is null)
                        throw new InvalidDataException("解压后的 Java 运行时缺少 bin\\java.exe。");

                    InvalidateCache();
                    return javaExe;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{root} → {ex.Message}");
                }
            }

            throw new IOException(
                "所有候选安装位置都不可写，无法安装 Java：\n" +
                string.Join("\n", errors.Select(e => "  · " + e)) +
                "\n\n可在设置中手动指定已安装的 Java 路径。");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// <summary>
    /// 构造 Java 运行时的下载候选地址，按可用性排序。
    ///
    /// 重要：Adoptium 官方 API 会 307 重定向到 github.com 下载，
    /// 该域名在部分网络下不可达（实测连接超时），因此国内镜像必须优先。
    /// 镜像按目录列表解析真实文件名，避免版本号硬编码失效。
    /// </summary>
    private static async Task<List<string>> ResolveDownloadUrlsAsync(
        int majorVersion, string arch, CancellationToken cancellationToken)
    {
        var urls = new List<string>();

        // 1) 国内镜像：解析目录列表拿到确切文件名
        var mirrorRoots = new[]
        {
            "https://mirrors.tuna.tsinghua.edu.cn/Adoptium",
            "https://mirrors.ustc.edu.cn/adoptium",
            "https://mirrors.aliyun.com/adoptium"
        };

        foreach (var mirror in mirrorRoots)
        {
            var dirUrl = $"{mirror}/{majorVersion}/jre/{arch}/windows/";
            try
            {
                var listing = await HttpClient.GetStringAsync(dirUrl, cancellationToken).ConfigureAwait(false);
                var fileName = FindJreZipName(listing);
                if (fileName is not null)
                    urls.Add(dirUrl + fileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"镜像目录不可用 {dirUrl}：{ex.Message}");
            }
        }

        // 2) 官方 Adoptium API（可能重定向到 GitHub）
        urls.Add($"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/windows/{arch}/jre/hotspot/normal/eclipse");

        return urls;
    }

    /// <summary>从镜像目录列表 HTML 中找出 JRE zip 文件名。</summary>
    private static string? FindJreZipName(string html)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            html, @"href=""([^""]+\.zip)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var name = match.Groups[1].Value;
            // 只要 JRE 包，且排除校验文件
            if (name.Contains("jre", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(".sha256", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(".sig", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileName(name);
        }
        return null;
    }

    /// <summary>下载临时目录：放在安装候选目录内，避免系统临时目录不可写。</summary>
    private static string ResolveDownloadTempDirectory()
    {
        var primary = RuntimeRootCandidates().First();
        Directory.CreateDirectory(primary);
        return primary;
    }

    /// <summary>下载到临时文件，带进度回报；逐个尝试候选地址。</summary>
    private static async Task DownloadWithFallbackAsync(
        IReadOnlyList<string> urls, string destinationPath, int displayVersion,
        IProgress<(long Downloaded, long Total, string Stage)>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var errors = new List<string>();

        foreach (var url in urls)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            try
            {
                progress?.Report((0, 0, $"正在从 {host} 下载 Java {displayVersion} ..."));

                using var response = await HttpClient.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var destination = new FileStream(destinationPath, FileMode.Create,
                    FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

                var buffer = new byte[128 * 1024];
                long downloaded = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    downloaded += read;
                    progress?.Report((downloaded, total, $"正在下载 Java {displayVersion}（{host}）"));
                }

                if (downloaded == 0)
                    throw new IOException("下载内容为空。");

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{host} → {ex.Message}");
                try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
            }
        }

        throw new IOException(
            "所有下载源都不可用：\n" + string.Join("\n", errors.Select(e => "  · " + e)) +
            "\n\n可手动安装 Java 后在设置中指定路径。");
    }

    /// <summary>
    /// 在解压目录中定位 java.exe。
    /// Adoptium 压缩包内含一层形如 jdk-17.0.9+9-jre 的顶层目录，需要一并兼容。
    /// </summary>
    private static string? FindJavaExe(string dir)
    {
        var direct = Path.Combine(dir, "bin", "java.exe");
        if (File.Exists(direct))
            return direct;

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var candidate = Path.Combine(sub, "bin", "java.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch { }

        return null;
    }

    /// <summary>读取 java -version 输出中的主版本号。</summary>
    public static int GetMajorVersion(string javaExe)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = javaExe,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(8000))
            {
                try { process.Kill(); } catch { }
                return 0;
            }

            // 形如: openjdk version "17.0.9" 2023-10-17
            var match = System.Text.RegularExpressions.Regex.Match(output, "\"([0-9]+)(?:\\.([0-9]+))?");
            if (!match.Success)
                return 0;

            var first = int.Parse(match.Groups[1].Value);
            // Java 8 及更早是 "1.8.0" 形式
            if (first == 1 && match.Groups[2].Success)
                return int.Parse(match.Groups[2].Value);
            return first;
        }
        catch
        {
            return 0;
        }
    }

    private static IEnumerable<string> EnumerateInstalledRuntimes()
    {
        if (!Directory.Exists(RuntimeRoot))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(RuntimeRoot))
        {
            var candidate = Path.Combine(dir, "bin", "java.exe");
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<string> EnumerateSystemCandidates()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Java")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "bin", "java.exe")
                     })
            {
                if (File.Exists(candidate))
                    yield return candidate;
            }

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var candidate = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(candidate))
                    yield return candidate;
            }
        }
    }

    private static string? FindOnPath()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                return null;
            foreach (var segment in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(segment))
                    continue;
                var candidate = Path.Combine(segment.Trim('"'), "java.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch { }
        return null;
    }
}

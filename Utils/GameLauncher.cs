using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SFTLauncher.Utils;

/// <summary>
/// 现代 Minecraft 启动器。
///
/// 原实现用 <c>java -jar client.jar</c> 启动，这在现代版本上不可能工作：
/// 必须按版本 JSON 的 mainClass + 完整 classpath + arguments 启动，
/// 并把 native 库解压到 java.library.path 指向的目录。
/// </summary>
public static class GameLauncher
{
    public sealed record LaunchOptions(
        string VersionId,
        string MinecraftDirectory,
        string JavaExecutable,
        int MaxMemoryMb,
        string PlayerName);

    public sealed record LaunchResult(bool Success, string? Error, int? ProcessId);

    /// <summary>
    /// 依据物理内存计算安全的堆上限。
    /// 曾出现「3.91 GB 内存的机器被分配 4 GB 堆」导致游戏未响应的情况，
    /// 因此这里强制留出系统余量。
    /// </summary>
    public static int RecommendMaxMemoryMb(int? requestedMb = null)
    {
        long totalMb;
        try
        {
            totalMb = (long)(GetTotalPhysicalMemoryBytes() / 1024 / 1024);
        }
        catch
        {
            totalMb = 4096;
        }

        // 留出约 1.5GB 给系统与显存映射，且不超过物理内存的 60%
        var byReserve = totalMb - 1536;
        var byRatio = (long)(totalMb * 0.6);
        var safeMax = Math.Max(512, Math.Min(byReserve, byRatio));
        safeMax = Math.Min(safeMax, 8192);

        var requested = requestedMb ?? (int)safeMax;
        return (int)Math.Clamp(Math.Min(requested, safeMax), 512, safeMax);
    }

    /// <summary>本机物理内存总量（MB）。</summary>
    public static int TotalPhysicalMemoryMb
    {
        get
        {
            try
            {
                return (int)((long)(GetTotalPhysicalMemoryBytes() / 1024 / 1024));
            }
            catch
            {
                return 0;
            }
        }
    }

    private static ulong GetTotalPhysicalMemoryBytes()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : 4UL * 1024 * 1024 * 1024;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>启动器日志路径，用于留存游戏输出便于排查启动问题。</summary>
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SFTLauncher", "game-launch.log");

    /// <summary>追加一行启动器日志。</summary>
    public static void Log(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不能影响启动
        }
    }

    /// <summary>
    /// 把 java.exe 换成同目录的 javaw.exe。
    ///
    /// 用 java.exe 启动会附带一个控制台窗口（看起来像终端），
    /// 正常启动器应使用 javaw（无控制台）。游戏日志由 log4j 写入 logs 目录，
    /// 启动器另外把 stdout/stderr 落盘，因此不需要可见控制台。
    /// </summary>
    public static string PreferJavaw(string javaExecutable)
    {
        try
        {
            var directory = Path.GetDirectoryName(javaExecutable);
            if (string.IsNullOrEmpty(directory))
                return javaExecutable;

            var javaw = Path.Combine(directory, "javaw.exe");
            if (File.Exists(javaw))
                return javaw;
        }
        catch
        {
            // 保持原路径
        }
        return javaExecutable;
    }

    public static LaunchResult Launch(LaunchOptions options)
    {
        try
        {
            var startInfo = BuildStartInfo(options, out var error);
            if (startInfo is null)
            {
                Log($"启动校验失败：{error}");
                return new LaunchResult(false, error, null);
            }

            // 无控制台窗口；输出仍被重定向以便留档
            startInfo.CreateNoWindow = true;

            // 捕获控制台输出：既能留存崩溃原因，也直接解决
            // 旧实现 "StdOut has not been redirected" 的问题。
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            Log($"启动 {options.VersionId}");
            Log($"  java   : {options.JavaExecutable}");
            Log($"  目录   : {options.MinecraftDirectory}");
            Log($"  参数   : {string.Join(" ", startInfo.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("  [out] " + e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("  [err] " + e.Data); };
            process.Exited += (_, _) => Log($"游戏进程退出，代码 {process.ExitCode}");

            if (!process.Start())
                return new LaunchResult(false, "无法创建游戏进程。", null);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return new LaunchResult(true, null, process.Id);
        }
        catch (Exception ex)
        {
            Log($"启动异常：{ex}");
            return new LaunchResult(false, $"{ex.GetType().Name}: {ex.Message}", null);
        }
    }

    /// <summary>
    /// 构造启动信息但不启动进程，便于测试与诊断。
    /// </summary>
    public static ProcessStartInfo? BuildStartInfo(LaunchOptions options, out string? error)
    {
        error = Validate(options);
        if (error is not null)
            return null;

        var versionDir = Path.Combine(options.MinecraftDirectory, "versions", options.VersionId);
        var jsonPath = Path.Combine(versionDir, $"{options.VersionId}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = document.RootElement;

        var classpath = BuildClasspath(root, options.MinecraftDirectory, versionDir, options.VersionId);
        var nativesDir = ExtractNatives(root, options.MinecraftDirectory, versionDir, options.VersionId);
        var arguments = BuildArguments(root, options, classpath, nativesDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = PreferJavaw(options.JavaExecutable),
            WorkingDirectory = options.MinecraftDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string? Validate(LaunchOptions options)
    {
        if (!File.Exists(options.JavaExecutable))
            return $"Java 不存在：{options.JavaExecutable}";

        var jar = Path.Combine(options.MinecraftDirectory, "versions", options.VersionId,
            $"{options.VersionId}.jar");
        if (!File.Exists(jar))
            return $"缺少客户端主文件：{jar}";

        var json = Path.Combine(options.MinecraftDirectory, "versions", options.VersionId,
            $"{options.VersionId}.json");
        if (!File.Exists(json))
            return $"缺少版本描述文件：{json}\n请在「实例库」中点击「补全文件」。";

        // 提前建好可写临时目录，避免游戏运行中途才失败
        try
        {
            var tempDir = Path.Combine(options.MinecraftDirectory, "temp");
            Directory.CreateDirectory(tempDir);
            var probe = Path.Combine(tempDir, ".write-probe");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return $"游戏目录下的 temp 目录不可写：{ex.Message}\n" +
                   "请检查杀毒软件或安全策略是否拦截了对该目录的写入。";
        }

        EnsureNarratorDisabled(options.MinecraftDirectory);

        return null;
    }

    /// <summary>
    /// 关闭游戏内置旁白。
    /// 旁白会加载 JNA，而 JNA 在临时目录受限时会抛 UnsatisfiedLinkError，
    /// 连锁导致游戏在 Initializing game 阶段崩溃。这里作为额外保险。
    /// </summary>
    private static void EnsureNarratorDisabled(string minecraftDirectory)
    {
        try
        {
            var optionsPath = Path.Combine(minecraftDirectory, "options.txt");
            var lines = File.Exists(optionsPath)
                ? File.ReadAllLines(optionsPath).ToList()
                : new List<string>();

            var narratorLine = lines.FindIndex(l =>
                l.StartsWith("narrator:", StringComparison.OrdinalIgnoreCase));

            if (narratorLine >= 0)
            {
                if (lines[narratorLine].Equals("narrator:0", StringComparison.OrdinalIgnoreCase))
                    return;
                lines[narratorLine] = "narrator:0";
            }
            else
            {
                lines.Add("narrator:0");
            }

            File.WriteAllLines(optionsPath, lines);
            Log("已在 options.txt 中关闭旁白（narrator:0）");
        }
        catch (Exception ex)
        {
            Log($"写入 options.txt 失败（不影响启动）：{ex.Message}");
        }
    }

    /// <summary>classpath = 全部库 jar + 客户端 jar（顺序：库在前，客户端最后）。</summary>
    private static string BuildClasspath(JsonElement root, string minecraftDir, string versionDir, string versionId)
    {
        var parts = new List<string>();
        var librariesDir = Path.Combine(minecraftDir, "libraries");

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!IsAllowed(library))
                    continue;
                if (!library.TryGetProperty("downloads", out var downloads))
                    continue;
                if (!downloads.TryGetProperty("artifact", out var artifact))
                    continue;
                if (!artifact.TryGetProperty("path", out var pathElement))
                    continue;

                var relative = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(relative))
                    continue;

                var full = Path.Combine(librariesDir, relative.Replace('/', Path.DirectorySeparatorChar));
                // 只加入存在的 jar，缺失的库会让游戏在启动时报错，但先让它跑起来
                if (File.Exists(full))
                    parts.Add(full);
            }
        }

        parts.Add(Path.Combine(versionDir, $"{versionId}.jar"));
        return string.Join(Path.PathSeparator, parts);
    }

    /// <summary>
    /// 解压 native 库到 versions/&lt;id&gt;/natives。
    /// 1.19+ 的 native 以「名称含 -natives-windows」的独立 artifact 形式出现，
    /// 旧版本则通过 downloads.classifiers 指定。
    /// </summary>
    private static string ExtractNatives(JsonElement root, string minecraftDir, string versionDir, string versionId)
    {
        var nativesDir = Path.Combine(versionDir, "natives");
        Directory.CreateDirectory(nativesDir);
        var librariesDir = Path.Combine(minecraftDir, "libraries");
        var extracted = 0;

        if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
            return nativesDir;

        foreach (var library in libraries.EnumerateArray())
        {
            if (!IsAllowed(library))
                continue;
            if (!library.TryGetProperty("name", out var nameElement))
                continue;
            var name = nameElement.GetString() ?? string.Empty;
            if (!library.TryGetProperty("downloads", out var downloads))
                continue;

            // 新式：native 是独立 artifact，名称带 -natives-<os>
            if (!name.Contains("-natives-", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(":natives-", StringComparison.OrdinalIgnoreCase))
                continue;

            // 只取当前平台的 natives
            if (!IsCurrentPlatformNatives(name))
                continue;

            if (!downloads.TryGetProperty("artifact", out var artifact) ||
                !artifact.TryGetProperty("path", out var pathElement))
                continue;

            var relative = pathElement.GetString();
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            var jarPath = Path.Combine(librariesDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jarPath))
                continue;

            extracted += ExtractNativeJar(jarPath, nativesDir);
        }

        System.Diagnostics.Debug.WriteLine($"natives 解压 {extracted} 个文件到 {nativesDir}");
        return nativesDir;
    }

    private static bool IsCurrentPlatformNatives(string libraryName)
    {
        var lower = libraryName.ToLowerInvariant();
        var isArm = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                    System.Runtime.InteropServices.Architecture.Arm64;

        // 明确标注了其他平台的，跳过
        if (lower.Contains("-natives-linux") || lower.Contains("-natives-macos") ||
            lower.Contains("-natives-osx"))
            return false;

        // arm64 专用包只在 arm64 上使用，反之亦然
        if (lower.Contains("-natives-windows-arm64"))
            return isArm;
        if (lower.Contains("-natives-windows-x86"))
            return false; // 32 位包
        if (lower.Contains("-natives-windows"))
            return !isArm;

        // 形如 "xxx:1.0:natives-windows" 的分类名
        return lower.Contains("natives-windows");
    }

    private static int ExtractNativeJar(string jarPath, string nativesDir)
    {
        var count = 0;
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            foreach (var entry in archive.Entries)
            {
                // 跳过目录项与 META-INF
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                    continue;

                // 只解压动态库与可执行文件，避免污染目录
                var extension = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (extension is not (".dll" or ".exe" or ".so" or ".dylib"))
                    continue;

                var target = Path.Combine(nativesDir, entry.Name);
                if (File.Exists(target))
                    continue;

                entry.ExtractToFile(target, overwrite: true);
                count++;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"解压 natives 失败 {jarPath}：{ex.Message}");
        }
        return count;
    }

    /// <summary>按版本 JSON 的 arguments 构造完整启动参数。</summary>
    private static List<string> BuildArguments(
        JsonElement root, LaunchOptions options, string classpath, string nativesDir)
    {
        var versionId = options.VersionId;
        var minecraftDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.MinecraftDirectory));
        var assetsDir = Path.Combine(minecraftDir, "assets");
        var assetsIndex = root.TryGetProperty("assetIndex", out var index) &&
                          index.TryGetProperty("id", out var indexId)
            ? indexId.GetString() ?? "legacy"
            : root.TryGetProperty("assets", out var assetsElement)
                ? assetsElement.GetString() ?? "legacy"
                : "legacy";

        var uuid = OfflineUuid(options.PlayerName);

        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["${natives_directory}"] = nativesDir,
            ["${launcher_name}"] = "SFTLauncher",
            ["${launcher_version}"] = "3.0",
            ["${classpath}"] = classpath,
            ["${auth_player_name}"] = options.PlayerName,
            ["${version_name}"] = versionId,
            ["${game_directory}"] = minecraftDir,
            ["${assets_root}"] = assetsDir,
            ["${assets_index_name}"] = assetsIndex,
            ["${auth_uuid}"] = uuid,
            ["${auth_access_token}"] = "0",
            ["${auth_session}"] = "0",
            ["${user_type}"] = "legacy",
            ["${clientid}"] = string.Empty,
            ["${auth_xuid}"] = string.Empty,
            ["${version_type}"] = "release",
            ["${resolution_width}"] = "854",
            ["${resolution_height}"] = "480",
            ["${game_assets}"] = Path.Combine(assetsDir, "virtual", "legacy")
        };

        var result = new List<string> { $"-Xmx{options.MaxMemoryMb}M", "-Xms512M" };

        // JNA（oshi/Narrator）需要把 jnidispatch.dll 解压到临时目录。
        // 部分环境下系统临时目录不可写会导致：
        //   UnsatisfiedLinkError: Failed to create temporary file for jnidispatch.dll
        // 进而连锁造成 NoClassDefFoundError 并在 Initializing game 阶段崩溃。
        // 因此把 Java 与 JNA 的临时目录都指向游戏目录下确定可写的位置。
        var tempDir = Path.Combine(minecraftDir, "temp");
        try { Directory.CreateDirectory(tempDir); } catch { }
        result.Add($"-Djava.io.tmpdir={tempDir}");
        result.Add($"-Djna.tmpdir={tempDir}");

        // JVM 参数
        if (root.TryGetProperty("arguments", out var arguments) &&
            arguments.TryGetProperty("jvm", out var jvm) && jvm.ValueKind == JsonValueKind.Array)
        {
            AppendArgumentArray(jvm, result, placeholders);
        }

        // 旧版本没有 arguments，使用固定的 natives 参数
        if (!result.Any(a => a.StartsWith("-Djava.library.path", StringComparison.Ordinal)))
            result.Add($"-Djava.library.path={nativesDir}");

        result.Add(root.TryGetProperty("mainClass", out var mainClass)
            ? mainClass.GetString() ?? "net.minecraft.client.main.Main"
            : "net.minecraft.client.main.Main");

        // 游戏参数
        if (root.TryGetProperty("arguments", out var arguments2) &&
            arguments2.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Array)
        {
            AppendArgumentArray(game, result, placeholders);
        }
        else if (root.TryGetProperty("minecraftArguments", out var legacy) &&
                 legacy.GetString() is { Length: > 0 } legacyArgs)
        {
            foreach (var token in legacyArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add(Substitute(token, placeholders));
        }

        return result;
    }

    private static void AppendArgumentArray(
        JsonElement array, List<string> target, Dictionary<string, string> placeholders)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value))
                    target.Add(Substitute(value, placeholders));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (!EvaluateRules(item))
                continue;

            if (!item.TryGetProperty("value", out var valueElement))
                continue;

            if (valueElement.ValueKind == JsonValueKind.String)
            {
                var value = valueElement.GetString();
                if (!string.IsNullOrEmpty(value))
                    target.Add(Substitute(value, placeholders));
            }
            else if (valueElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in valueElement.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                        continue;
                    var value = entry.GetString();
                    if (!string.IsNullOrEmpty(value))
                        target.Add(Substitute(value, placeholders));
                }
            }
        }
    }

    private static string Substitute(string value, Dictionary<string, string> placeholders)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
            return value;

        var builder = new StringBuilder(value);
        foreach (var (key, replacement) in placeholders)
            builder.Replace(key, replacement);
        return builder.ToString();
    }

    /// <summary>评估库或参数的 rules（只实现 Windows 场景所需的部分）。</summary>
    private static bool IsAllowed(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatches(rule))
                continue;
            allowed = rule.TryGetProperty("action", out var action) &&
                      action.GetString() == "allow";
        }
        return allowed;
    }

    private static bool EvaluateRules(JsonElement argument)
        => !argument.TryGetProperty("rules", out var rules) ||
           rules.ValueKind != JsonValueKind.Array ||
           IsAllowed(argument);

    private static bool RuleMatches(JsonElement rule)
    {
        if (!rule.TryGetProperty("os", out var os))
            return true;

        if (os.TryGetProperty("name", out var name))
        {
            var expected = name.GetString();
            // Windows 平台：osx/linux 规则不匹配
            if (string.Equals(expected, "osx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(expected, "linux", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(expected, "windows", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (os.TryGetProperty("arch", out var arch))
        {
            var expected = arch.GetString();
            var actual = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                         System.Runtime.InteropServices.Architecture.Arm64
                ? "arm64"
                : "x86";
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>离线模式 UUID：与官方离线模式一致（name 的 MD5，置版本位）。</summary>
    private static string OfflineUuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

using System.IO;
using System.Net.Http;

namespace SFTLauncher.Download;

/// <summary>
/// 一个完整的下载源定义，包含所有需要替换的基础 URL。
/// 移植自 NyaLauncher.Core.Download.DownloadSource
/// </summary>
public sealed record DownloadSource
{
    public required string Name { get; init; }
    public required string LauncherMeta { get; init; }
    public required string Meta { get; init; }
    public required string Libraries { get; init; }
    public required string Resources { get; init; }
    public required string Maven { get; init; }
}

public static class DownloadSources
{
    public static DownloadSource Official { get; } = new()
    {
        Name = "Official",
        LauncherMeta = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
        Meta = "https://piston-meta.mojang.com",
        Libraries = "https://libraries.minecraft.net",
        Resources = "https://resources.download.minecraft.net",
        Maven = "https://libraries.minecraft.net"
    };

    public static DownloadSource Bmcl { get; } = new()
    {
        Name = "BMCL",
        LauncherMeta = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
        Meta = "https://bmclapi2.bangbang93.com",
        Libraries = "https://bmclapi2.bangbang93.com/maven",
        Resources = "https://bmclapi2.bangbang93.com/assets",
        Maven = "https://bmclapi2.bangbang93.com/maven"
    };

    public static IReadOnlyList<DownloadSource> All { get; } = [Official, Bmcl];
}

public static class DownloadSourceProvider
{
    public static DownloadSource Active { get; set; } = DownloadSources.Bmcl;
    public static DownloadSource? Fallback { get; set; } = DownloadSources.Official;

    private static readonly HttpClient SharedClient = CreateSharedClient();
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SFTLauncher/1.0");
        return client;
    }

    public static string Resolve(string officialUrl)
    {
        if (string.IsNullOrWhiteSpace(officialUrl) || Active == DownloadSources.Official)
            return officialUrl;
        return ReplaceBaseUrl(officialUrl, Active);
    }

    public static string? ResolveFallback(string officialUrl)
    {
        if (Fallback is null || string.IsNullOrWhiteSpace(officialUrl))
            return null;
        return ReplaceBaseUrl(officialUrl, Fallback);
    }

    public static async Task<string> GetStringAsync(
        string officialUrl,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var primaryUrl = ValidateHttpsUrl(Resolve(officialUrl));
        var fallbackUrl = ResolveFallback(officialUrl);
        if (fallbackUrl is not null) fallbackUrl = ValidateHttpsUrl(fallbackUrl);

        try
        {
            using var cts = CreateTimeoutCts(timeout, cancellationToken);
            return await SharedClient.GetStringAsync(primaryUrl, cts?.Token ?? cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (IsFallbackEligible(fallbackUrl, primaryUrl, cancellationToken))
        {
            using var cts = CreateTimeoutCts(timeout, cancellationToken);
            return await SharedClient.GetStringAsync(fallbackUrl!, cts?.Token ?? cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public static async Task<byte[]> GetBytesAsync(
        string officialUrl,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var primaryUrl = ValidateHttpsUrl(Resolve(officialUrl));
        var fallbackUrl = ResolveFallback(officialUrl);
        if (fallbackUrl is not null) fallbackUrl = ValidateHttpsUrl(fallbackUrl);

        try
        {
            using var cts = CreateTimeoutCts(timeout, cancellationToken);
            return await SharedClient.GetByteArrayAsync(primaryUrl, cts?.Token ?? cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (IsFallbackEligible(fallbackUrl, primaryUrl, cancellationToken))
        {
            using var cts = CreateTimeoutCts(timeout, cancellationToken);
            return await SharedClient.GetByteArrayAsync(fallbackUrl!, cts?.Token ?? cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool IsFallbackEligible(string? fallbackUrl, string primaryUrl, CancellationToken ct) =>
        !ct.IsCancellationRequested &&
        fallbackUrl is not null &&
        !string.Equals(fallbackUrl, primaryUrl, StringComparison.OrdinalIgnoreCase);

    private static CancellationTokenSource? CreateTimeoutCts(TimeSpan? timeout, CancellationToken ct)
    {
        var effective = timeout ?? DefaultRequestTimeout;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(effective);
        return cts;
    }

    private static string ValidateHttpsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"下载地址不是有效的 HTTPS URL：{url}");
        return url;
    }

    private static string ReplaceBaseUrl(string url, DownloadSource target)
    {
        if (target == DownloadSources.Official || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;
        var baseUrl = uri.Host.ToLowerInvariant() switch
        {
            "libraries.minecraft.net" => target.Libraries,
            "resources.download.minecraft.net" => target.Resources,
            "piston-meta.mojang.com" or "launchermeta.mojang.com" or
            "piston-data.mojang.com" or "launcher.mojang.com" => target.Meta,
            _ => null
        };
        return baseUrl is null ? url : baseUrl.TrimEnd('/') + uri.PathAndQuery;
    }
}

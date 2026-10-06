using SFTLauncher.Models;

namespace SFTLauncher.Download;

public static class ManifestGet
{
    public static async Task<List<MinecraftVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        var json = await DownloadSourceProvider.GetStringAsync(
                DownloadSources.Official.LauncherMeta,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var manifest = System.Text.Json.JsonSerializer.Deserialize<VersionManifest>(json);
        if (manifest is null)
            throw new System.Text.Json.JsonException("版本清单响应为空。");

        var versions = manifest.Versions ?? [];
        foreach (var version in versions)
        {
            if (version.Id == manifest.Latest.Release)
                version.IsLatestRelease = true;
            if (version.Id == manifest.Latest.Snapshot)
                version.IsLatestSnapshot = true;
        }

        return [.. versions.OrderByDescending(v => v.ReleaseTime)];
    }
}

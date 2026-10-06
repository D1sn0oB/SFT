using System.IO;
using System.Net.Http;

namespace SFTLauncher.Download;

/// <summary>
/// Mod 文件下载服务。支持断点续传和自动重试。
/// 移植自 NyaLauncher.Core.Download.ModDownloadService
/// </summary>
public static class ModDownloadService
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 4;

    private static readonly HttpClient Client = new() { Timeout = AttemptTimeout };

    static ModDownloadService()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("SFTLauncher/1.0");
    }

    public static async Task DownloadAsync(
        string downloadUrl,
        string targetPath,
        IProgress<(long downloaded, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = $"{targetPath}.sft-download";
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await DownloadAttemptAsync(downloadUrl, temporaryPath, progress, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporaryPath, targetPath, overwrite: true);
                    return;
                }
                catch (Exception exception) when (
                    attempt < MaxAttempts && IsTransientFailure(exception, cancellationToken))
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 6)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static async Task DownloadAttemptAsync(
        string downloadUrl, string temporaryPath,
        IProgress<(long downloaded, long total)>? progress,
        CancellationToken cancellationToken)
    {
        var resumeFrom = File.Exists(temporaryPath) ? new FileInfo(temporaryPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (resumeFrom > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

        using var response = await Client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var remoteTotal = response.Content.Headers.ContentRange?.Length;
            if (remoteTotal is { } total && resumeFrom == total)
                return;
            TryDeleteFile(temporaryPath);
            throw new IOException("断点信息与远端文件不一致，已重置下载。");
        }

        response.EnsureSuccessStatusCode();

        long totalBytes;
        long downloadedBase;
        FileStream destination;
        if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            totalBytes = response.Content.Headers.ContentRange?.Length ?? -1;
            downloadedBase = resumeFrom;
            destination = new FileStream(temporaryPath, FileMode.Append, FileAccess.Write,
                FileShare.None, 128 * 1024, useAsync: true);
        }
        else
        {
            totalBytes = response.Content.Headers.ContentLength ?? -1;
            downloadedBase = 0;
            destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 128 * 1024, useAsync: true);
        }

        await using (destination)
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var buffer = new byte[128 * 1024];
            var downloaded = downloadedBase;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                downloaded += read;
                progress?.Report((downloaded, totalBytes));
            }
        }
    }

    private static bool IsTransientFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        return exception is HttpRequestException or IOException or System.Net.Sockets.SocketException;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

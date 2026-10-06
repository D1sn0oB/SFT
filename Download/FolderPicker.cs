using System.IO;
using System.Windows;

namespace SFTLauncher.Download;

/// <summary>
/// 文件夹选择对话框。
///
/// 原实现用 <c>Microsoft.Win32.OpenFileDialog</c> 选目录，
/// 那是「选文件」的对话框，用户看到的是文件列表、无法选中文件夹，
/// 还出现了 filter 写成 <c>文件夹|*.minecraft</c> 却匹配不到任何文件的情况。
/// 这里改用系统原生的文件夹选择器。
/// </summary>
public static class FolderPicker
{
    /// <summary>让用户选择一个文件夹；取消时返回 null。</summary>
    public static string? Pick(string title, string? initialDirectory = null, Window? owner = null)
    {
        // 优先使用 Vista+ 的现代文件夹选择器（支持新建文件夹、路径栏）
        var picked = TryPickModern(title, initialDirectory, owner);
        if (picked is not null)
            return picked;

        // 回退到 WinForms 的文件夹浏览器
        return TryPickLegacy(title, initialDirectory);
    }

    private static string? TryPickModern(string title, string? initialDirectory, Window? owner)
    {
        try
        {
            var dialogType = Type.GetTypeFromProgID("Shell.Application");
            if (dialogType is null)
                return null;

            dynamic? shell = Activator.CreateInstance(dialogType);
            if (shell is null)
                return null;

            dynamic folder = shell.BrowseForFolder(0, title, 0, 0);
            if (folder is null)
            {
                Release(shell);
                return null;
            }

            string? path = null;
            try
            {
                dynamic self = folder.Self;
                path = self.Path as string;
            }
            catch
            {
                // 某些外壳项没有文件系统路径
            }
            finally
            {
                Release(folder);
                Release(shell);
            }

            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                return path;
        }
        catch
        {
            // 交给回退实现
        }

        return null;
    }

    private static void Release(object? comObject)
    {
        try
        {
            if (comObject is not null && System.Runtime.InteropServices.Marshal.IsComObject(comObject))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 释放失败不影响功能
        }
    }

    private static string? TryPickLegacy(string title, string? initialDirectory)
    {
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = title,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = ResolveStartDirectory(initialDirectory)
            };

            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK &&
                   !string.IsNullOrWhiteSpace(dialog.SelectedPath)
                ? dialog.SelectedPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveStartDirectory(string? initialDirectory)
    {
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            return initialDirectory;

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        if (Directory.Exists(fallback))
            return fallback;

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}

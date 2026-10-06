using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SFTLauncher.Download;

namespace SFTLauncher.Pages
{
    public partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            LoadSettings();
        }

        /// <summary>进入设置页时把已保存的配置回填到界面，避免「保存后看不见」。</summary>
        private void LoadSettings()
        {
            try
            {
                McPathBox.Text = LauncherConfig.MinecraftDirectory;
                JavaPathBox.Text = LauncherConfig.JavaPath;
                var memory = LauncherConfig.MaxMemoryGb;
                MemorySlider.Value = Math.Clamp(memory, (int)MemorySlider.Minimum, (int)MemorySlider.Maximum);
                MemoryText.Text = $"{memory} GB";
                UpdatePathStatus();
                UpdateJavaHint();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取设置失败：{ex.Message}");
            }
        }

        private void BrowseMcPath_Click(object sender, RoutedEventArgs e)
        {
            var selected = FolderPicker.Pick("选择 Minecraft 目录（选中该目录本身，不是里面的文件）",
                McPathBox.Text, Window.GetWindow(this));
            if (selected is null)
                return;

            McPathBox.Text = selected;
            UpdatePathStatus();
        }

        /// <summary>自动检测本机已安装的 Minecraft 目录。</summary>
        private void DetectMcPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidates = MinecraftDirectoryDetector.Enumerate();
                var best = candidates.FirstOrDefault(c => c.HasInstalledVersions);
                if (best is null)
                {
                    var existing = candidates.FirstOrDefault(c => c.Exists);
                    if (existing is not null)
                    {
                        McPathBox.Text = existing.Path;
                        UpdatePathStatus();
                        MessageBox.Show(
                            $"未检测到已安装版本的目录。\n\n" +
                            $"找到已有目录（{existing.Source}）：\n{existing.Path}\n\n" +
                            "已填入该路径，可直接在此安装。",
                            "检测完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        var fallback = MinecraftDirectoryDetector.DefaultDirectory;
                        McPathBox.Text = fallback;
                        UpdatePathStatus();
                        MessageBox.Show(
                            $"未检测到任何 Minecraft 目录。\n\n将在以下默认位置新建：\n{fallback}",
                            "检测完成", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }

                McPathBox.Text = best.Path;
                UpdatePathStatus();
                MessageBox.Show(
                    $"检测到已安装的 Minecraft 目录：\n{best.Path}\n\n" +
                    $"来源：{best.Source}\n状态：{best.Summary}\n\n" +
                    (candidates.Count > 1
                        ? $"（共发现 {candidates.Count} 个候选位置，已自动选择最完整的一个）"
                        : string.Empty),
                    "检测完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"自动检测失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseJavaPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 Java 可执行文件",
                Filter = "可执行文件|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };

            if (dialog.ShowDialog() == true)
            {
                JavaPathBox.Text = dialog.FileName;
            }
        }

        private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MemoryText != null)
                MemoryText.Text = $"{(int)e.NewValue} GB";
        }

        /// <summary>检测本机可用的 Java 运行时并填入路径。</summary>
        private void DetectJava_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var runtimes = JavaRuntime.Discover().ToList();
                if (runtimes.Count == 0)
                {
                    MessageBox.Show(
                        "本机未检测到 Java 运行时。\n\n" +
                        "你不需要手动安装：启动游戏时会自动下载匹配版本的 Java。",
                        "未检测到 Java", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var best = runtimes.OrderByDescending(r => r.MajorVersion).First();
                JavaPathBox.Text = best.Path;
                MessageBox.Show(
                    $"检测到 {runtimes.Count} 个 Java 运行时，已填入版本最高的一个。\n\n" +
                    $"版本：Java {best.MajorVersion}\n路径：{best.Path}",
                    "检测完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检测 Java 失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateJavaHint()
        {
            if (JavaHintText == null)
                return;
            try
            {
                var runtimes = JavaRuntime.Discover().ToList();
                JavaHintText.Text = runtimes.Count == 0
                    ? "未检测到本机 Java；启动游戏时会自动下载所需版本"
                    : $"本机最高 Java {runtimes.OrderByDescending(r => r.MajorVersion).First().MajorVersion}（留空则启动时自动选择）";
            }
            catch
            {
                JavaHintText.Text = string.Empty;
            }
        }

        private void UpdatePathStatus()
        {
            if (McPathStatus == null)
                return;
            var path = McPathBox.Text;
            if (string.IsNullOrWhiteSpace(path))
            {
                McPathStatus.Text = "将使用默认路径";
                McPathStatus.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
            }
            else if (Directory.Exists(path))
            {
                McPathStatus.Text = "目录已存在";
                McPathStatus.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            }
            else
            {
                McPathStatus.Text = "目录不存在，下载时将自动创建";
                McPathStatus.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
            }
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var mcPath = string.IsNullOrWhiteSpace(McPathBox.Text)
                    ? LauncherConfig.DefaultMinecraftDirectory
                    : McPathBox.Text.Trim();

                if (File.Exists(mcPath))
                {
                    MessageBox.Show("Minecraft 目录不能是一个文件，请选择文件夹。",
                        "路径无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                LauncherConfig.Save(mcPath, JavaPathBox.Text, (int)MemorySlider.Value);

                // 回读确认，避免「提示已保存但其实没写进去」
                var saved = LauncherConfig.MinecraftDirectory;
                if (!string.Equals(saved, mcPath, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        $"设置未能写入配置。\n\n期望: {mcPath}\n实际: {saved}\n\n本次会话仍会使用你填写的路径。",
                        "保存未生效", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                McPathBox.Text = saved;
                UpdatePathStatus();
                MessageBox.Show("设置已保存！", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存设置失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

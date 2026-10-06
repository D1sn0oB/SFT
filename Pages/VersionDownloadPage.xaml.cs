using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SFTLauncher.Download;
using SFTLauncher.Models;

namespace SFTLauncher.Pages
{
    public partial class VersionDownloadPage : Page
    {
        private GameDownloadService? _downloadService;
        private CancellationTokenSource? _currentCts;
        private bool isDownloading = false;
        private List<DownloadRecord> downloadHistory = new List<DownloadRecord>();
        private List<MinecraftVersion> allVersions = new List<MinecraftVersion>();

        public VersionDownloadPage()
        {
            InitializeComponent();
            LoadSettings();
            InitializeDownloadService();

            // 页面被缓存复用，每次重新显示都刷新目录，反映设置页的最新保存结果
            Loaded += (_, _) => RefreshInstallPath();

            try
            {
                LoadVersionsFromApi();
                RefreshInstallPath();
            }
            catch (Exception ex)
            {
                StatusText.Text = "初始化失败: " + ex.Message;
                StatusText.Foreground = (Brush)FindResource("ErrorBrush");
            }
        }

        /// <summary>页面显示时以配置为准刷新目录框。</summary>
        private void RefreshInstallPath()
        {
            InstallPathBox.Text = GetDefaultMinecraftDirectory();
            UpdatePathStatus();
        }

        /// <summary>显示当前目录来源：来自设置页的已保存配置，还是内置默认值。</summary>
        private void UpdatePathStatus()
        {
            if (LauncherConfig.HasCustomMinecraftDirectory)
            {
                PathStatus.Text = "来自设置中保存的目录";
                PathStatus.Foreground = (Brush)FindResource("SuccessBrush");
            }
            else
            {
                PathStatus.Text = "使用默认路径";
                PathStatus.Foreground = (Brush)FindResource("TextMutedBrush");
            }
        }

        private void LoadSettings()
        {
            DownloadSettings.ApplySavedSettings();
            DownloadSourceComboBox.ItemsSource = DownloadSources.All;
            DownloadSourceComboBox.SelectedItem = DownloadSourceProvider.Active;
            var concurrency = DownloadSettings.ParallelDownloads;
            DownloadConcurrencyComboBox.ItemsSource = new[] { 8, 16, 32 }.Append(concurrency).Distinct().Order().ToArray();
            DownloadConcurrencyComboBox.SelectedItem = concurrency;
        }

        private void InitializeDownloadService()
        {
            _downloadService = new GameDownloadService();
            _downloadService.Changed += OnDownloadStateChanged;
        }

        private async void LoadVersionsFromApi()
        {
            try
            {
                UpdateStatus("正在加载版本列表...", "#A78BFA");
                
                var versions = await ManifestGet.GetVersionsAsync();
                allVersions.Clear();
                
                foreach (var version in versions)
                {
                    if (version.Type == "release")
                    {
                        allVersions.Add(version);
                    }
                }
                
                // 只显示最近的版本
                if (allVersions.Count > 50)
                {
                    allVersions = allVersions.GetRange(0, 50);
                }
                
                VersionComboBox.ItemsSource = allVersions;
                VersionComboBox.DisplayMemberPath = "DisplayName";
                if (VersionComboBox.Items.Count > 0)
                    VersionComboBox.SelectedIndex = 0;
                
                VersionStatus.Text = "✓";
                VersionStatus.Foreground = (Brush)FindResource("SuccessBrush");
                UpdateStatus("就绪", "#34D399");
            }
            catch (Exception ex)
            {
                VersionStatus.Text = "✗";
                VersionStatus.Foreground = (Brush)FindResource("ErrorBrush");
                UpdateStatus("加载版本失败: " + ex.Message, "#F87171");
            }
        }

        /// <summary>默认目录来自统一配置（设置页保存的值），未设置时才用 %USERPROFILE%\.minecraft。</summary>
        private string GetDefaultMinecraftDirectory() => LauncherConfig.MinecraftDirectory;

        private void BrowsePath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = FolderPicker.Pick(
                    "选择 Minecraft 安装目录（选中该文件夹本身）",
                    InstallPathBox.Text, Window.GetWindow(this));
                if (selected is null)
                    return;

                InstallPathBox.Text = selected;
                CheckMinecraftDirectory();
            }
            catch (Exception ex)
            {
                MessageBox.Show("选择目录失败：" + ex.Message, "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>自动检测已安装的 Minecraft 目录并填入。</summary>
        private void DetectPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var candidates = MinecraftDirectoryDetector.Enumerate();
                var best = candidates.FirstOrDefault(c => c.HasInstalledVersions)
                           ?? candidates.FirstOrDefault(c => c.Exists);

                var path = best?.Path ?? MinecraftDirectoryDetector.DefaultDirectory;
                InstallPathBox.Text = path;
                CheckMinecraftDirectory();

                var detail = best is null
                    ? $"未检测到已有目录，将在默认位置新建：\n{path}"
                    : $"检测到（{best.Source}）：\n{path}\n状态：{best.Summary}";

                MessageBox.Show(detail, "自动检测", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"自动检测失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CheckMinecraftDirectory()
        {
            try
            {
                string mcDir = InstallPathBox.Text;
                if (!string.IsNullOrEmpty(mcDir) && Directory.Exists(mcDir))
                {
                    PathStatus.Text = "目录已存在";
                    PathStatus.Foreground = (Brush)FindResource("SuccessBrush");
                }
                else if (!string.IsNullOrEmpty(mcDir))
                {
                    PathStatus.Text = "将自动创建";
                    PathStatus.Foreground = (Brush)FindResource("WarningBrush");
                }
                else
                {
                    PathStatus.Text = "使用默认路径";
                    PathStatus.Foreground = (Brush)FindResource("TextMutedBrush");
                }
            }
            catch { }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            var minecraftDir = string.IsNullOrEmpty(InstallPathBox.Text) 
                ? GetDefaultMinecraftDirectory() 
                : InstallPathBox.Text;

            if (isDownloading) return;
            
            var selectedVersion = VersionComboBox.SelectedItem as MinecraftVersion;
            if (selectedVersion == null)
            {
                MessageBox.Show("请先选择一个版本！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            isDownloading = true;
            using var operationCts = new CancellationTokenSource();
            _currentCts = operationCts;
            DownloadSourceComboBox.IsEnabled = false;
            DownloadConcurrencyComboBox.IsEnabled = false;
            CancelButton.IsEnabled = true;
            var downloadBtn = sender as Button;
            if (downloadBtn != null) downloadBtn.IsEnabled = false;
            CancelButton.Visibility = Visibility.Visible;
            
            UpdateStatus("准备下载...", "#A78BFA");
            ProgressBar.Visibility = Visibility.Visible;
            ProgressText.Text = "0%";

            try
            {
                // 确保目录存在（包含 versions/libraries/assets 等子目录）
                if (!Directory.Exists(minecraftDir))
                {
                    UpdateStatus("创建 Minecraft 目录...", "#A78BFA");
                }
                MinecraftDirectoryDetector.EnsureCreated(minecraftDir);

                // 保存游戏目录到配置（统一由 LauncherConfig 落盘）
                GameDownloadService.SetMinecraftDirectory(minecraftDir);

                // 使用新的下载服务（不再需要传递目录参数）
                if (DownloadSourceComboBox.SelectedItem is DownloadSource source)
                    DownloadSettings.SaveActiveSource(source);
                if (DownloadConcurrencyComboBox.SelectedItem is int concurrency)
                    DownloadSettings.SaveParallelDownloads(concurrency);
                var success = await _downloadService!.StartAsync(selectedVersion, operationCts.Token);

                if (!success && _downloadService.Current.Phase == GameDownloadPhase.Failed)
                    throw new InvalidOperationException(_downloadService.Current.Detail);
                if (success)
                {
                    downloadHistory.Insert(0, new DownloadRecord 
                    { 
                        Version = selectedVersion.Id, 
                        Path = minecraftDir, 
                        Time = DateTime.Now, 
                        Success = true 
                    });
                    UpdateDownloadHistory();
                    UpdateStatus("✓ 下载完成!", "#34D399");
                    VersionStatus.Text = "✓";
                    VersionStatus.Foreground = (Brush)FindResource("SuccessBrush");

                    MessageBox.Show($"Minecraft {selectedVersion.Id} 下载完成!\n\n位置: {minecraftDir}",
                        "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                UpdateStatus("✗ 下载失败: " + ex.Message, "#F87171");
                VersionStatus.Text = "✗";
                VersionStatus.Foreground = (Brush)FindResource("ErrorBrush");
                downloadHistory.Insert(0, new DownloadRecord
                {
                    Version = selectedVersion?.Id ?? "unknown",
                    Path = minecraftDir,
                    Time = DateTime.Now,
                    Success = false,
                    Error = ex.Message
                });
                UpdateDownloadHistory();
                MessageBox.Show("下载失败: " + ex.Message + "\n\n" + DescribeDownloadFailureHint(ex),
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isDownloading = false;
                if (downloadBtn != null) downloadBtn.IsEnabled = true;
                CancelButton.Visibility = Visibility.Collapsed;
                DownloadSourceComboBox.IsEnabled = true;
                DownloadConcurrencyComboBox.IsEnabled = true;
                if (ReferenceEquals(_currentCts, operationCts))
                    _currentCts = null;
            }
        }

        private readonly object _progressGate = new();
        private GameDownloadSnapshot? _pendingSnapshot;
        private bool _progressQueued;
        private long _appliedRevision;
        private void OnDownloadStateChanged(GameDownloadSnapshot snapshot)
        {
            lock (_progressGate)
            {
                if (snapshot.Revision <= _appliedRevision ||
                    (_pendingSnapshot is not null && snapshot.Revision <= _pendingSnapshot.Revision))
                    return;
                _pendingSnapshot = snapshot;
                if (_progressQueued) return;
                _progressQueued = true;
            }
            Dispatcher.BeginInvoke(new Action(ApplyPendingDownloadState));
        }

        private void ApplyPendingDownloadState()
        {
            GameDownloadSnapshot? snapshot;
            lock (_progressGate)
            {
                snapshot = _pendingSnapshot;
                _pendingSnapshot = null;
                _progressQueued = false;
                if (snapshot is null || snapshot.Revision <= _appliedRevision) return;
                _appliedRevision = snapshot.Revision;
            }
            ProgressBar.Value = snapshot.Percentage;
            ProgressText.Text = snapshot.Percentage.ToString("F0") + "%";
            InfoText.Text = $"{snapshot.CompletedFiles:N0}/{snapshot.TotalFiles:N0} 文件 · {snapshot.BytesPerSecond / 1024 / 1024:F2} MiB/s（平均）";
            if (snapshot.IsTerminal)
            {
                var color = snapshot.Phase == GameDownloadPhase.Completed ? "#34D399" : "#F87171";
                UpdateStatus(snapshot.Detail, color);
            }
            else if (snapshot.HasTask)
            {
                UpdateStatus($"{snapshot.StageName}: {snapshot.Detail}", "#A78BFA");
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _currentCts?.Cancel();
            CancelButton.IsEnabled = false;
            UpdateStatus("正在取消下载，请等待当前连接退出…", "#FBBF24");
        }

        private void UpdateStatus(string text, string colorKey)
        {
            void Apply()
            {
                StatusText.Text = text;
                if (colorKey.StartsWith('#'))
                    StatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(colorKey)!;
                else
                    StatusText.Foreground = TryFindResource(colorKey + "Brush") as Brush ??
                        (Brush)FindResource("AccentBrush");
            }
            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.BeginInvoke(new Action(Apply));
        }

        /// <summary>
        /// 按失败原因给出可操作的提示，避免把本地文件/配置问题误报成网络问题。
        /// </summary>
        private static string DescribeDownloadFailureHint(Exception exception)
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                if (current is UnauthorizedAccessException)
                    return "原因：本地文件访问被拒绝，请检查该目录的写入权限，或关闭占用该文件的程序。";
                if (current is IOException && current.Message.Contains("being used", StringComparison.OrdinalIgnoreCase))
                    return "原因：目标文件被其他程序占用，请关闭后重试。";
                if (current.Message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase))
                    return "原因：无法写入本地路径，请检查目录权限或以管理员身份运行。";
            }

            return "若网络正常，请检查安装目录的写入权限。";
        }

        private void UpdateDownloadHistory()
        {
            Dispatcher.Invoke(() =>
            {
                DownloadHistoryPanel.Children.Clear();
                if (downloadHistory.Count > 0)
                {
                    foreach (var record in downloadHistory.Take(5))
                    {
                        var border = new Border
                        {
                            Background = (Brush)FindResource("BackgroundSecondaryBrush"),
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(16),
                            Margin = new Thickness(0, 0, 0, 8)
                        };
                        var grid = new Grid();
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var stackPanel = new StackPanel();
                        var titleBlock = new TextBlock
                        {
                            Text = record.Success ? "✓ Minecraft " + record.Version : "✗ Minecraft " + record.Version,
                            Foreground = record.Success ? (Brush)FindResource("SuccessBrush") : (Brush)FindResource("ErrorBrush"),
                            FontSize = 13,
                            FontWeight = FontWeights.Bold
                        };
                        var timeBlock = new TextBlock
                        {
                            Text = "时间: " + record.Time.ToString("yyyy-MM-dd HH:mm"),
                            Foreground = (Brush)FindResource("TextMutedBrush"),
                            FontSize = 11,
                            Margin = new Thickness(0, 4, 0, 0)
                        };
                        stackPanel.Children.Add(titleBlock);
                        stackPanel.Children.Add(timeBlock);
                        Grid.SetColumn(stackPanel, 0);

                        var pathBlock = new TextBlock
                        {
                            Text = Path.GetFileName(record.Path),
                            Foreground = (Brush)FindResource("TextMutedBrush"),
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center
                        };
                        Grid.SetColumn(pathBlock, 1);

                        grid.Children.Add(stackPanel);
                        grid.Children.Add(pathBlock);
                        border.Child = grid;
                        DownloadHistoryPanel.Children.Add(border);
                    }
                }
            });
        }

        private class DownloadRecord
        {
            public string Version { get; set; } = "";
            public string Path { get; set; } = "";
            public DateTime Time { get; set; }
            public bool Success { get; set; }
            public string Error { get; set; } = "";
        }
    }
}

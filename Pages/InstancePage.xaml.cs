using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SFTLauncher.Download;

namespace SFTLauncher.Pages
{
    public partial class InstancePage : Page
    {
        public InstancePage()
        {
            InitializeComponent();
            Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadInstances();
            }
            catch (Exception ex)
            {
                // 扫描失败不应打断界面，把原因显示出来即可（详细堆栈由 App 的全局处理记录）
                ScanHint.Text = "实例库加载失败：" + ex.Message;
                EmptyState.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 扫描已配置 Minecraft 目录下的 versions 子目录，
        /// 把真实已安装的版本显示为实例（此前这里是写死的空状态）。
        /// </summary>
        private void LoadInstances()
        {
            var minecraftDir = LauncherConfig.MinecraftDirectory;
            var instances = VersionScanner.Scan(minecraftDir);

            InstanceList.ItemsSource = instances;
            EmptyState.Visibility = instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var launchable = instances.Count(v => v.IsLaunchable);
            ScanHint.Text = instances.Count == 0
                ? $"当前目录：{minecraftDir}（未发现已安装版本，可点击「新建实例」下载）"
                : $"当前目录：{minecraftDir}（共 {instances.Count} 个版本，{launchable} 个可启动）";
        }

        private void NewInstance_Click(object sender, RoutedEventArgs e)
        {
            // 跳转到版本下载页面并更新侧边栏状态
            Application.Current.MainWindow?.Dispatcher.Invoke(() =>
            {
                (Application.Current.MainWindow as MainWindow)?.NavigateToExternal("downloads");
            });
        }

        private void StartInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string versionId)
                return;

            GameLaunchService.TryLaunch(
                new GameLaunchService.LaunchRequest(versionId, LauncherConfig.MinecraftDirectory),
                Window.GetWindow(this));
            LoadInstances();
        }

        /// <summary>补全某个版本缺失的文件（例如缺少版本 JSON）。</summary>
        private async void RepairInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string versionId)
                return;

            button.IsEnabled = false;
            ScanHint.Text = $"正在补全 Minecraft {versionId} 的文件...";
            try
            {
                var (success, message) = await GameLaunchService.RepairAsync(
                    versionId, LauncherConfig.MinecraftDirectory);
                MessageBox.Show(message,
                    success ? "补全完成" : "补全失败",
                    MessageBoxButton.OK,
                    success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally
            {
                button.IsEnabled = true;
                LoadInstances();
            }
        }
    }
}

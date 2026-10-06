using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SFTLauncher.Download;

namespace SFTLauncher.Pages
{
    public partial class HomePage : Page
    {
        public HomePage()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        /// <summary>
        /// 扫描已安装版本并更新主页状态。
        /// 此前这里是一段写死的「还没有实例」，所以下载完成后主页永远显示为空。
        /// </summary>
        private void Refresh()
        {
            var minecraftDir = LauncherConfig.MinecraftDirectory;
            DirectoryText.Text = minecraftDir;

            var instances = VersionScanner.Scan(minecraftDir);

            InstanceList.ItemsSource = instances;
            EmptyState.Visibility = instances.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            VersionCountText.Text = instances.Count.ToString();

            RefreshJavaStatus();
        }

        private void RefreshJavaStatus()
        {
            try
            {
                var runtimes = JavaRuntime.Discover().ToList();
                if (runtimes.Count == 0)
                {
                    JavaStatusText.Text = "未安装";
                    JavaStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
                    JavaStatusDetail.Text = "首次启动游戏时将自动下载";
                }
                else
                {
                    var best = runtimes.OrderByDescending(r => r.MajorVersion).First();
                    JavaStatusText.Text = $"Java {best.MajorVersion}";
                    JavaStatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                    JavaStatusDetail.Text = best.IsAutoInstalled ? "启动器自带运行时" : "系统已安装";
                }
            }
            catch
            {
                JavaStatusText.Text = "未知";
                JavaStatusDetail.Text = "Java 运行时";
            }
        }

        private void NewInstance_Click(object sender, RoutedEventArgs e)
        {
            (Application.Current.MainWindow as MainWindow)?.NavigateToExternal("downloads");
        }

        private void StartInstance_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string versionId)
                return;

            GameLaunchService.TryLaunch(
                new GameLaunchService.LaunchRequest(versionId, LauncherConfig.MinecraftDirectory),
                Window.GetWindow(this));
            Refresh();
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;

namespace SFTLauncher
{
    public partial class MainWindow : Window
    {
        private string currentPage = "home";
        private Pages.VersionDownloadPage? _downloadPage;

        public MainWindow()
        {
            InitializeComponent();
            NavigateTo("home");

            // 为每个导航按钮添加 tooltip
            SetupNavButton(BtnHome, "主页");
            SetupNavButton(BtnInstances, "实例库");
            SetupNavButton(BtnDownloads, "版本下载");
            SetupNavButton(BtnMods, "资源库");
            SetupNavButton(BtnAI, "AI 助手");
            SetupNavButton(BtnSettings, "设置");
        }

        // 公开方法，供子页面调用
        public void NavigateToExternal(string page)
        {
            NavigateTo(page);
        }

        private void SetupNavButton(Button btn, string tooltip)
        {
            // WPF 原生 Tooltip，简单可靠
            btn.ToolTip = tooltip;
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                string page = btn.Tag.ToString();
                NavigateTo(page);
            }
        }

        private void NavigateTo(string page)
        {
            currentPage = page;
            
            // 更新导航按钮状态
            UpdateNavStyle("BtnHome", page == "home");
            UpdateNavStyle("BtnInstances", page == "instances");
            UpdateNavStyle("BtnDownloads", page == "downloads");
            UpdateNavStyle("BtnMods", page == "mods");
            UpdateNavStyle("BtnAI", page == "ai");
            UpdateNavStyle("BtnSettings", page == "settings");
            
            // 加载对应页面
            switch (page)
            {
                case "home":
                    ContentFrame.Navigate(new Pages.HomePage());
                    break;
                case "instances":
                    ContentFrame.Navigate(new Pages.InstancePage());
                    break;
                case "downloads":
                    ContentFrame.Navigate(_downloadPage ??= new Pages.VersionDownloadPage());
                    break;
                case "mods":
                    ContentFrame.Navigate(new Pages.ModsPage());
                    break;
                case "ai":
                    ContentFrame.Navigate(new Pages.AIPage());
                    break;
                case "settings":
                    ContentFrame.Navigate(new Pages.SettingsPage());
                    break;
            }
        }

        private void UpdateNavStyle(string btnName, bool isActive)
        {
            var btn = FindName(btnName) as Button;
            if (btn != null)
            {
                btn.Style = isActive ? (Style)FindResource("NavItemActiveStyle") 
                                     : (Style)FindResource("NavItemStyle");
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                MaximizeIcon.Text = "□";
            }
            else
            {
                WindowState = WindowState.Maximized;
                MaximizeIcon.Text = "❐";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}

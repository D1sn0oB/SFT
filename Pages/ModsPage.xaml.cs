using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SFTLauncher.Pages
{
    public partial class ModsPage : Page
    {
        private static readonly HttpClient client = new HttpClient();
        private List<Utils.ModrinthApi.ModInfo> currentMods = new List<Utils.ModrinthApi.ModInfo>();
        private bool isLoading = false;
        private bool isPlaceholder = true;

        public ModsPage()
        {
            InitializeComponent();
            SearchBox.GotFocus += SearchBox_GotFocus;
            SearchBox.LostFocus += SearchBox_LostFocus;
            SearchBox.TextChanged += SearchBox_TextChanged;
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (isPlaceholder)
            {
                SearchBox.Text = "";
                SearchBox.Foreground = (Brush)FindResource("TextPrimaryBrush");
                isPlaceholder = false;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = "输入模组名称搜索...";
                SearchBox.Foreground = (Brush)FindResource("TextMutedBrush");
                isPlaceholder = true;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 用户手动输入时，确保不显示 placeholder
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                isPlaceholder = false;
                SearchBox.Foreground = (Brush)FindResource("TextPrimaryBrush");
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            string query = SearchBox.Text.Trim();
            if (string.IsNullOrEmpty(query) || isPlaceholder)
            {
                MessageBox.Show("请输入搜索关键词", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var searchBtn = sender as Button;
            if (searchBtn != null)
            {
                if (isLoading) return;
                isLoading = true;
                searchBtn.IsEnabled = false;
                searchBtn.Content = "搜索中...";
            }
            
            StatusText.Text = "正在搜索 '" + query + "'...";
            StatusText.Foreground = (Brush)FindResource("AccentBrush");
            ModsList.Children.Clear();

            try
            {
                var api = new Utils.ModrinthApi();
                currentMods = await api.SearchModsAsync(query, "minecraft", 20);

                if (currentMods.Count == 0)
                {
                    StatusText.Text = "未找到相关模组";
                    StatusText.Foreground = (Brush)FindResource("WarningBrush");
                }
                else
                {
                    StatusText.Text = "找到 " + currentMods.Count + " 个模组";
                    StatusText.Foreground = (Brush)FindResource("SuccessBrush");

                    foreach (var mod in currentMods)
                    {
                        var card = CreateModCard(mod);
                        ModsList.Children.Add(card);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "搜索失败: " + ex.Message;
                StatusText.Foreground = (Brush)FindResource("ErrorBrush");
                MessageBox.Show("搜索失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isLoading = false;
                if (searchBtn != null)
                {
                    searchBtn.IsEnabled = true;
                    searchBtn.Content = "搜索";
                }
            }
        }

        private Border CreateModCard(Utils.ModrinthApi.ModInfo mod)
        {
            var border = new Border
            {
                Style = (Style)FindResource("CardStyle"),
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(16)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧：名称和描述
            var leftPanel = new StackPanel();
            var titleBlock = new TextBlock
            {
                Text = mod.Title,
                Style = (Style)FindResource("LabelHeading")
            };
            var descText = mod.Description ?? "";
            if (descText.Length > 60) descText = descText.Substring(0, 60) + "...";
            var descBlock = new TextBlock
            {
                Text = descText,
                Style = (Style)FindResource("LabelMuted"),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            leftPanel.Children.Add(titleBlock);
            leftPanel.Children.Add(descBlock);
            Grid.SetColumn(leftPanel, 0);

            // 版本
            var versionBlock = new TextBlock
            {
                Text = mod.Version,
                Style = (Style)FindResource("LabelSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(versionBlock, 1);

            // 加载器标签
            var loaderText = mod.Loader != null && mod.Loader.Count > 0 ? mod.Loader[0] : "未知";
            var loaderBlock = new TextBlock
            {
                Text = loaderText,
                Style = (Style)FindResource("LabelMuted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(loaderBlock, 2);

            // 下载按钮
            var downloadBtn = new Button
            {
                Style = (Style)FindResource("AccentButton"),
                Height = 32,
                Width = 80,
                Content = "下载"
            };
            downloadBtn.Click += (s, e) => DownloadMod_Click(mod);
            Grid.SetColumn(downloadBtn, 3);

            grid.Children.Add(leftPanel);
            grid.Children.Add(versionBlock);
            grid.Children.Add(loaderBlock);
            grid.Children.Add(downloadBtn);

            border.Child = grid;
            return border;
        }

        private async void DownloadMod_Click(Utils.ModrinthApi.ModInfo mod)
        {
            try
            {
                var api = new Utils.ModrinthApi();
                var modDetail = await api.GetModAsync(mod.Id);
                
                if (modDetail == null)
                {
                    MessageBox.Show("无法获取模组详情", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var result = MessageBox.Show(
                    "是否下载 " + modDetail.Title + "?\n\n版本：" + modDetail.Version,
                    "确认下载",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    if (!string.IsNullOrEmpty(modDetail.Url))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = modDetail.Url,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("下载失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}

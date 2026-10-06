using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SFTLauncher.Pages
{
    public partial class AIPage : Page
    {
        private readonly HttpClient _httpClient = new();
        private const string ApiUrl = "https://api.agnes-ai.cn/v1/chat/completions";
        private const string DefaultApiKey = "sk-2p33XPxODG9kiM9SPrJqA6Nl5Os7qYj5tOpOsQq1A6G9IkUG";

        public AIPage()
        {
            InitializeComponent();
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessage();
        }

        private void MessageInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                SendMessage();
            }
        }

        private async Task SendMessage()
        {
            string message = MessageInput.Text.Trim();
            if (string.IsNullOrEmpty(message)) return;

            // 添加用户消息
            AddMessage("你", message, true);
            MessageInput.Clear();

            // 添加思考中消息
            var thinkingMsg = AddMessage("Nova", "🤔 正在思考...", false);
            ChatScroll.ScrollToEnd();

            try
            {
                var requestContent = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        model = "agnes-2.5-flash",
                        messages = new[]
                        {
                            new { role = "system", content = "你是 Nova，SFT Launcher 内置的 AI 助手。回答 Minecraft 相关问题，使用中文，简洁实用。" },
                            new { role = "user", content = message }
                        },
                        max_tokens = 500,
                        temperature = 0.7
                    }),
                    Encoding.UTF8,
                    "application/json"
                );

                _httpClient.DefaultRequestHeaders.Authorization = 
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DefaultApiKey);

                var response = await _httpClient.PostAsync(ApiUrl, requestContent);

                // 移除思考消息
                ChatMessages.Children.Remove(thinkingMsg);

                if (response.IsSuccessStatusCode)
                {
                    var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
                    string reply = result.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    AddMessage("Nova", reply, false);
                }
                else
                {
                    AddMessage("Nova", "❌ API 请求失败，请稍后重试。", false);
                }
            }
            catch (Exception ex)
            {
                // 移除思考消息
                if (ChatMessages.Children.Contains(thinkingMsg))
                    ChatMessages.Children.Remove(thinkingMsg);
                
                AddMessage("Nova", $"❌ 错误：{ex.Message}", false);
            }

            ChatScroll.ScrollToEnd();
        }

        private UIElement AddMessage(string sender, string text, bool isUser)
        {
            var bubble = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource("LabelSecondary"),
                Background = isUser ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BackgroundSecondaryBrush"),
                Foreground = isUser ? (Brush)FindResource("TextInverseBrush") : (Brush)FindResource("TextPrimaryBrush"),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 12),
                TextWrapping = TextWrapping.Wrap,
                FontWeight = isUser ? FontWeights.Bold : FontWeights.Normal,
            };
            
            ChatMessages.Children.Add(bubble);
            return bubble;
        }
    }
}

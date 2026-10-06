using System.Windows;

namespace SFTLauncher.Download;

/// <summary>
/// Java 运行时下载进度窗口。
/// 下载在后台线程进行，进度通过 IProgress 回到界面线程。
/// </summary>
public partial class JavaDownloadWindow : Window
{
    private readonly int _majorVersion;
    private readonly CancellationTokenSource _cts = new();
    private bool _running;

    /// <summary>安装成功后的 java.exe 路径。</summary>
    public string? InstalledJavaPath { get; private set; }

    public JavaDownloadWindow(int majorVersion)
    {
        InitializeComponent();
        _majorVersion = majorVersion;
        TitleText.Text = $"正在安装 Java {majorVersion}";
        Loaded += OnLoaded;
        Closed += (_, _) => _cts.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_running)
            return;
        _running = true;

        var progress = new Progress<(long Downloaded, long Total, string Stage)>(report =>
        {
            var (downloaded, total, stage) = report;
            DetailText.Text = stage;
            if (total > 0)
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Clamp(downloaded * 100d / total, 0, 100);
                DetailText.Text = $"{stage}  {downloaded / 1024d / 1024:F1} / {total / 1024d / 1024:F1} MB";
            }
            else
            {
                Progress.IsIndeterminate = true;
            }
        });

        try
        {
            var javaExe = await JavaRuntime.InstallAsync(_majorVersion, progress, _cts.Token);
            InstalledJavaPath = javaExe;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"下载或解压 Java 失败：\n{ex.Message}",
                "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
        }
        finally
        {
            if (IsVisible)
                Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelBtn.IsEnabled = false;
        DetailText.Text = "正在取消...";
        _cts.Cancel();
    }
}

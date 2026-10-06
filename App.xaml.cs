using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SFTLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未处理异常必须让用户看到原因，而不是直接闪退。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportCrash(e.Exception, "界面线程");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteCrashLog(exception, "后台线程");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception, "任务");
        e.SetObserved();
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SFTLauncher", "crash.log");

    private static void ReportCrash(Exception exception, string source)
    {
        WriteCrashLog(exception, source);

        var detail = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            detail.AppendLine($"{current.GetType().Name}: {current.Message}");
            if (current.StackTrace is not null)
                detail.AppendLine(current.StackTrace);
            detail.AppendLine();
        }

        MessageBox.Show(
            $"启动器遇到错误（{source}），已阻止崩溃。\n\n{detail}\n" +
            $"完整日志：{CrashLogPath}",
            "发生错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void WriteCrashLog(Exception exception, string source)
    {
        try
        {
            var directory = Path.GetDirectoryName(CrashLogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var text = new StringBuilder()
                .AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] =====")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            File.AppendAllText(CrashLogPath, text);
        }
        catch
        {
            // 记录日志失败不能再次引发崩溃
        }
    }
}

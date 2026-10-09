using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace WeChatCopilot.App;

/// <summary>应用入口：启动悬浮窗；全局套用 WPF-UI Fluent 暗色主题。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Fluent 暗色：与悬浮窗深色面板一致，避免控件亮色突兀
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        var overlay = new OverlayWindow();
        overlay.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string logPath = LogError("UI异常", e.Exception);
        string rootCause = e.Exception.GetBaseException()?.Message ?? e.Exception.Message;

        string msg = $"界面运行发生异常：\n{e.Exception.Message}";
        if (!string.IsNullOrEmpty(rootCause) && rootCause != e.Exception.Message)
        {
            msg += $"\n\n根本原因：\n{rootCause}";
        }

        msg += $"\n\n异常详情已记录至日志文件：\n{logPath}\n\n是否打开日志所在文件夹查看完整堆栈？";

        var result = MessageBox.Show(
            msg,
            "微信 Copilot 运行提示",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.GetDirectoryName(logPath) ?? logPath,
                    UseShellExecute = true
                });
            }
            catch
            {
                // 忽略打开目录失败
            }
        }

        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogError("致命异常", ex);
        }
    }

    private static string LogError(string category, Exception ex)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WeChatCopilot");
        string logFile = Path.Combine(dir, "crash.log");
        try
        {
            Directory.CreateDirectory(dir);
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {ex}\n\n");
        }
        catch
        {
            // 忽略日志写入错误
        }

        return logFile;
    }
}

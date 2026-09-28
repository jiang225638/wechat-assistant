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
        LogError("UI异常", e.Exception);
        MessageBox.Show(
            $"界面运行发生异常：\n{e.Exception.Message}\n\n详细信息已记录至日志。",
            "微信 Copilot 运行提示",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogError("致命异常", ex);
        }
    }

    private static void LogError(string category, Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WeChatCopilot");
            Directory.CreateDirectory(dir);
            string logFile = Path.Combine(dir, "crash.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {ex}\n\n");
        }
        catch
        {
            // 忽略日志写入错误
        }
    }
}

using System.Windows;
using Wpf.Ui.Appearance;

namespace WeChatCopilot.App;

/// <summary>应用入口：启动悬浮窗；全局套用 WPF-UI Fluent 暗色主题。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Fluent 暗色：与悬浮窗深色面板一致，避免控件亮色突兀
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        var overlay = new OverlayWindow();
        overlay.Show();
    }
}

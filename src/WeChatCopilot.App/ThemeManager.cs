using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace WeChatCopilot.App;

/// <summary>
/// 主题管理器：提供深色 (Fluent Dark)、浅色 (Fluent Light) 与跟随系统 (System) 的动态热切换，
/// 并同步刷新全窗口画刷调色板与 WPF-UI 全局控件风格，无需重启应用。
/// </summary>
public static class ThemeManager
{
    public const string ThemeDark = "Dark";
    public const string ThemeLight = "Light";
    public const string ThemeSystem = "System";

    /// <summary>
    /// 应用指定的主题模式（Dark / Light / System）。
    /// </summary>
    public static void ApplyTheme(Window window, string themeMode)
    {
        bool isLight = false;
        if (string.Equals(themeMode, ThemeLight, StringComparison.OrdinalIgnoreCase))
        {
            isLight = true;
        }
        else if (string.Equals(themeMode, ThemeDark, StringComparison.OrdinalIgnoreCase))
        {
            isLight = false;
        }
        else
        {
            // System
            isLight = IsSystemLightTheme();
        }

        // 1. 同步 WPF-UI 核心控件主题
        var appTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        try
        {
            ApplicationThemeManager.Apply(appTheme);
        }
        catch
        {
            // 忽略非 UI 线程或异常
        }

        // 2. 动态刷新全窗口画刷（通过修改已有 SolidColorBrush.Color 触发 WPF 界面热重载）
        UpdateBrush(window, "WindowBgBrush", isLight ? Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFA, 0x18, 0x18, 0x24));
        UpdateBrush(window, "CardBgBrush", isLight ? Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC) : Color.FromArgb(0xEE, 0x22, 0x22, 0x33));
        UpdateBrush(window, "CardSubtleBgBrush", isLight ? Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9) : Color.FromArgb(0x77, 0x1B, 0x1B, 0x2A));
        UpdateBrush(window, "BorderLightBrush", isLight ? Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        UpdateBrush(window, "BorderSubtleBrush", isLight ? Color.FromArgb(0xFF, 0xEB, 0xF0, 0xF5) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        UpdateBrush(window, "TextPrimaryBrush", isLight ? Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A) : Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6));
        UpdateBrush(window, "TextSecondaryBrush", isLight ? Color.FromArgb(0xFF, 0x47, 0x55, 0x69) : Color.FromArgb(0xFF, 0xA0, 0xA0, 0xB8));
        UpdateBrush(window, "AccentBrush", isLight ? Color.FromArgb(0xFF, 0x25, 0x63, 0xEB) : Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));
        UpdateBrush(window, "PrimaryAccentBrush", isLight ? Color.FromArgb(0xFF, 0x25, 0x63, 0xEB) : Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6));
        UpdateBrush(window, "AccentHoverBrush", isLight ? Color.FromArgb(0xFF, 0x1D, 0x4E, 0xD8) : Color.FromArgb(0xFF, 0x25, 0x63, 0xEB));
        UpdateBrush(window, "GreenBrush", isLight ? Color.FromArgb(0xFF, 0x05, 0x96, 0x69) : Color.FromArgb(0xFF, 0x10, 0xB9, 0x81));
        UpdateBrush(window, "PurpleBrush", isLight ? Color.FromArgb(0xFF, 0x7C, 0x3A, 0xED) : Color.FromArgb(0xFF, 0x8B, 0x5C, 0xF6));

        UpdateBrush(window, "TabBarBgBrush", isLight ? Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0) : Color.FromArgb(0xFF, 0x1C, 0x1C, 0x2A));
        UpdateBrush(window, "TabItemTextBrush", isLight ? Color.FromArgb(0xFF, 0x47, 0x55, 0x69) : Color.FromArgb(0xFF, 0xA0, 0xA0, 0xB8));
        UpdateBrush(window, "DiagBgBrush", isLight ? Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9) : Color.FromArgb(0xFF, 0x1A, 0x1A, 0x28));

        UpdateBrush(window, "ButtonBgBrush", isLight ? Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        UpdateBrush(window, "ButtonHoverBrush", isLight ? Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1) : Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        UpdateBrush(window, "ButtonPressedBrush", isLight ? Color.FromArgb(0xFF, 0x94, 0xA3, 0xB8) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        UpdateBrush(window, "SubBorderCheckedBrush", isLight ? Color.FromArgb(0xFF, 0xDB, 0xEA, 0xFE) : Color.FromArgb(0xFF, 0x2D, 0x2B, 0x4E));
        UpdateBrush(window, "SubBorderCheckedTextBrush", isLight ? Color.FromArgb(0xFF, 0x1D, 0x4E, 0xD8) : Color.FromArgb(0xFF, 0x60, 0xA5, 0xFA));
        UpdateBrush(window, "InputBgBrush", isLight ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x1A, 0x1A, 0x2B));

        UpdateBrush(window, "KeyCapBgBrush", isLight ? Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0) : Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        UpdateBrush(window, "KeyCapBorderBrush", isLight ? Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1) : Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        UpdateBrush(window, "KeyCapTextBrush", isLight ? Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        UpdateBrush(window, "IntentCardBgBrush", isLight ? Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC) : Color.FromArgb(0xFF, 0x1C, 0x21, 0x36));
        UpdateBrush(window, "IntentCardBorderBrush", isLight ? Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1) : Color.FromArgb(0xFF, 0x3B, 0x4B, 0x75));
        UpdateBrush(window, "IntentQuoteBgBrush", isLight ? Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9) : Color.FromArgb(0xFF, 0x2A, 0x33, 0x52));
        UpdateBrush(window, "IntentQuoteTextBrush", isLight ? Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A) : Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6));
        UpdateBrush(window, "IntentSubtextBgBrush", isLight ? Color.FromArgb(0xFF, 0xF5, 0xF3, 0xFF) : Color.FromArgb(0xFF, 0x23, 0x1F, 0x3D));
        UpdateBrush(window, "IntentSubtextBorderBrush", isLight ? Color.FromArgb(0xFF, 0xDD, 0xD6, 0xFE) : Color.FromArgb(0x55, 0x8B, 0x5C, 0xF6));
        UpdateBrush(window, "IntentSubtextTextBrush", isLight ? Color.FromArgb(0xFF, 0x5B, 0x21, 0xB6) : Color.FromArgb(0xFF, 0xED, 0xE9, 0xFE));
        UpdateBrush(window, "IntentDemandBgBrush", isLight ? Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9) : Color.FromArgb(0xFF, 0x1E, 0x29, 0x3B));
        UpdateBrush(window, "IntentDemandTextBrush", isLight ? Color.FromArgb(0xFF, 0x33, 0x41, 0x55) : Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0));
        UpdateBrush(window, "IntentStrategyBgBrush", isLight ? Color.FromArgb(0xFF, 0xEC, 0xFD, 0xF5) : Color.FromArgb(0xFF, 0x18, 0x2E, 0x2B));
        UpdateBrush(window, "IntentStrategyBorderBrush", isLight ? Color.FromArgb(0xFF, 0xA7, 0xF3, 0xD0) : Color.FromArgb(0x33, 0x10, 0xB9, 0x81));
        UpdateBrush(window, "IntentStrategyTextBrush", isLight ? Color.FromArgb(0xFF, 0x06, 0x5F, 0x46) : Color.FromArgb(0xFF, 0xD1, 0xFA, 0xE5));
        UpdateBrush(window, "IntentFlowBgBrush", isLight ? Color.FromArgb(0xFF, 0xF0, 0xF9, 0xFF) : Color.FromArgb(0xFF, 0x1B, 0x2B, 0x3E));
        UpdateBrush(window, "IntentFlowBorderBrush", isLight ? Color.FromArgb(0xFF, 0x7D, 0xD3, 0xFC) : Color.FromArgb(0xFF, 0x38, 0xBD, 0xF8));
        UpdateBrush(window, "IntentFlowTextBrush", isLight ? Color.FromArgb(0xFF, 0x03, 0x69, 0xA1) : Color.FromArgb(0xFF, 0xE0, 0xF2, 0xFE));
    }

    private static void UpdateBrush(Window window, string key, Color color)
    {
        if (window?.Resources == null) return;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        window.Resources[key] = brush;
    }

    /// <summary>
    /// 检测 Windows 注册表中是否启用了浅色模式。
    /// </summary>
    public static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var val = key?.GetValue("AppsUseLightTheme");
            if (val is int i && i == 1)
            {
                return true;
            }
        }
        catch
        {
            // 忽略权限或读取错误
        }
        return false;
    }
}

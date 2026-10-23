using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Markup;

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

    public static string CurrentThemeMode { get; private set; } = ThemeDark;
    public static bool IsLightMode { get; private set; } = false;

    /// <summary>
    /// 将当前已应用的主题同步应用至指定窗口。
    /// </summary>
    public static void ApplyCurrentTheme(Window window)
    {
        ApplyTheme(window, CurrentThemeMode);
    }

    /// <summary>
    /// 应用指定的主题模式（Dark / Light / System）。
    /// </summary>
    public static void ApplyTheme(Window? window, string themeMode)
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

        CurrentThemeMode = themeMode;
        IsLightMode = isLight;

        // 1. 同步 WPF-UI 核心控件主题（仅更新资源字典，绝不调用有 HWND 篡改副作用的 Apply 方法）
        var appTheme = isLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        try
        {
            if (Application.Current != null)
            {
                foreach (var dict in Application.Current.Resources.MergedDictionaries)
                {
                    if (dict is ThemesDictionary themesDict)
                    {
                        themesDict.Theme = appTheme;
                        break;
                    }
                }
            }
        }
        catch
        {
            // 忽略非 UI 线程或异常
        }

        // 确保 AllowsTransparency 窗口完全透明，绝无任何系统底色泄漏
        if (Application.Current != null)
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w.AllowsTransparency)
                {
                    w.Background = Brushes.Transparent;
                    if (PresentationSource.FromVisual(w) is HwndSource source && source.CompositionTarget != null)
                    {
                        source.CompositionTarget.BackgroundColor = Colors.Transparent;
                    }
                }
            }
        }
        else if (window != null && window.AllowsTransparency)
        {
            window.Background = Brushes.Transparent;
            if (PresentationSource.FromVisual(window) is HwndSource source && source.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
        }

        // 2. 动态刷新全窗口及全局画刷（支持 OverlayWindow, PersonaAnalyticsWindow, AdviceAnalyticsWindow 等）
        Color windowBg = isLight ? Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFA, 0x18, 0x18, 0x24);
        Color cardBg = isLight ? Color.FromArgb(0xFF, 0xF8, 0xFA, 0xFC) : Color.FromArgb(0xEE, 0x22, 0x22, 0x33);
        Color cardSubtleBg = isLight ? Color.FromArgb(0xFF, 0xF1, 0xF5, 0xF9) : Color.FromArgb(0x77, 0x1B, 0x1B, 0x2A);
        Color borderLight = isLight ? Color.FromArgb(0xFF, 0xE2, 0xE8, 0xF0) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        Color borderSubtle = isLight ? Color.FromArgb(0xFF, 0xEB, 0xF0, 0xF5) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
        Color textPrimary = isLight ? Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A) : Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6);
        Color textSecondary = isLight ? Color.FromArgb(0xFF, 0x47, 0x55, 0x69) : Color.FromArgb(0xFF, 0xA0, 0xA0, 0xB8);
        Color accent = isLight ? Color.FromArgb(0xFF, 0x25, 0x63, 0xEB) : Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);

        UpdateBrush(window, "WindowBgBrush", windowBg);
        UpdateBrush(window, "WindowBg", windowBg);
        UpdateBrush(window, "CardBgBrush", cardBg);
        UpdateBrush(window, "CardBg", cardBg);
        UpdateBrush(window, "CardSubtleBgBrush", cardSubtleBg);
        UpdateBrush(window, "CardSubtleBg", cardSubtleBg);
        UpdateBrush(window, "BorderLightBrush", borderLight);
        UpdateBrush(window, "BorderColor", borderLight);
        UpdateBrush(window, "BorderSubtleBrush", borderSubtle);
        UpdateBrush(window, "TextPrimaryBrush", textPrimary);
        UpdateBrush(window, "TextPrimary", textPrimary);
        UpdateBrush(window, "TextSecondaryBrush", textSecondary);
        UpdateBrush(window, "TextSecondary", textSecondary);
        UpdateBrush(window, "AccentBrush", accent);
        UpdateBrush(window, "PrimaryAccentBrush", accent);
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
        UpdateBrush(window, "InputBg", isLight ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x22, 0x1A, 0x1A, 0x2B));
        UpdateBrush(window, "SendBtnInactiveBgBrush", isLight ? Color.FromArgb(0xFF, 0xE5, 0xE7, 0xEB) : Color.FromArgb(0xFF, 0x2E, 0x33, 0x42));
        UpdateBrush(window, "SendBtnInactiveFgBrush", isLight ? Color.FromArgb(0xFF, 0x71, 0x71, 0x7A) : Color.FromArgb(0xFF, 0x9C, 0xA3, 0xAF));

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

        // 滚动条现代 Fluent 样式画刷
        Color scrollThumb = isLight ? Color.FromArgb(0x40, 0x00, 0x00, 0x00) : Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF);
        Color scrollThumbHover = isLight ? Color.FromArgb(0x70, 0x00, 0x00, 0x00) : Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF);
        Color scrollThumbPressed = isLight ? Color.FromArgb(0x95, 0x00, 0x00, 0x00) : Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF);
        UpdateBrush(window, "ScrollBarThumbBrush", scrollThumb);
        UpdateBrush(window, "ScrollBarThumbHoverBrush", scrollThumbHover);
        UpdateBrush(window, "ScrollBarThumbPressedBrush", scrollThumbPressed);

        // 顶部指标胶囊高对比度画刷 (战略目的 / 技能体系 / 意图导向)
        Color pillGoalBg = isLight ? Color.FromArgb(0x18, 0x10, 0xB9, 0x81) : Color.FromArgb(0x22, 0x10, 0xB9, 0x81);
        Color pillGoalBorder = isLight ? Color.FromArgb(0x40, 0x10, 0xB9, 0x81) : Color.FromArgb(0x55, 0x10, 0xB9, 0x81);
        Color pillGoalLabel = isLight ? Color.FromArgb(0xFF, 0x04, 0x78, 0x57) : Color.FromArgb(0xFF, 0x34, 0xD3, 0x99);
        Color pillGoalText = isLight ? Color.FromArgb(0xFF, 0x06, 0x5F, 0x46) : Color.FromArgb(0xFF, 0x6E, 0xE7, 0xB7);

        Color pillSkillBg = isLight ? Color.FromArgb(0x18, 0x8B, 0x5C, 0xF6) : Color.FromArgb(0x22, 0x8B, 0x5C, 0xF6);
        Color pillSkillBorder = isLight ? Color.FromArgb(0x40, 0x8B, 0x5C, 0xF6) : Color.FromArgb(0x55, 0x8B, 0x5C, 0xF6);
        Color pillSkillLabel = isLight ? Color.FromArgb(0xFF, 0x6D, 0x28, 0xD9) : Color.FromArgb(0xFF, 0xA7, 0x8B, 0xFA);
        Color pillSkillText = isLight ? Color.FromArgb(0xFF, 0x5B, 0x21, 0xB6) : Color.FromArgb(0xFF, 0xDD, 0xD6, 0xFE);

        Color pillIntentBg = isLight ? Color.FromArgb(0x18, 0xF5, 0x9E, 0x0B) : Color.FromArgb(0x22, 0xF5, 0x9E, 0x0B);
        Color pillIntentBorder = isLight ? Color.FromArgb(0x40, 0xF5, 0x9E, 0x0B) : Color.FromArgb(0x55, 0xF5, 0x9E, 0x0B);
        Color pillIntentLabel = isLight ? Color.FromArgb(0xFF, 0xB4, 0x53, 0x09) : Color.FromArgb(0xFF, 0xFB, 0xBF, 0x24);
        Color pillIntentText = isLight ? Color.FromArgb(0xFF, 0x92, 0x40, 0x0E) : Color.FromArgb(0xFF, 0xFD, 0xE6, 0x8A);

        UpdateBrush(window, "PillGoalBgBrush", pillGoalBg);
        UpdateBrush(window, "PillGoalBorderBrush", pillGoalBorder);
        UpdateBrush(window, "PillGoalLabelBrush", pillGoalLabel);
        UpdateBrush(window, "PillGoalTextBrush", pillGoalText);

        UpdateBrush(window, "PillSkillBgBrush", pillSkillBg);
        UpdateBrush(window, "PillSkillBorderBrush", pillSkillBorder);
        UpdateBrush(window, "PillSkillLabelBrush", pillSkillLabel);
        UpdateBrush(window, "PillSkillTextBrush", pillSkillText);

        UpdateBrush(window, "PillIntentBgBrush", pillIntentBg);
        UpdateBrush(window, "PillIntentBorderBrush", pillIntentBorder);
        UpdateBrush(window, "PillIntentLabelBrush", pillIntentLabel);
        UpdateBrush(window, "PillIntentTextBrush", pillIntentText);

        // 聊天气泡深浅色主题自适应画刷 (对方 / 我)
        Color chatIncomingBg = isLight ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x27, 0x27, 0x3A);
        Color chatIncomingBorder = isLight ? Color.FromArgb(0xFF, 0xCB, 0xD5, 0xE1) : Color.FromArgb(0xFF, 0x3D, 0x3D, 0x58);
        Color chatIncomingBadge = isLight ? Color.FromArgb(0xFF, 0x25, 0x63, 0xEB) : Color.FromArgb(0xFF, 0x93, 0xC5, 0xFD);
        Color chatIncomingText = isLight ? Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A) : Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6);

        Color chatOutgoingBg = isLight ? Color.FromArgb(0xFF, 0x95, 0xEC, 0x69) : Color.FromArgb(0xFF, 0x1B, 0x43, 0x32);
        Color chatOutgoingBorder = isLight ? Color.FromArgb(0xFF, 0x82, 0xDA, 0x58) : Color.FromArgb(0xFF, 0x2D, 0x6A, 0x4F);
        Color chatOutgoingBadge = isLight ? Color.FromArgb(0xFF, 0x04, 0x78, 0x57) : Color.FromArgb(0xFF, 0x6E, 0xE7, 0xB7);
        Color chatOutgoingText = isLight ? Color.FromArgb(0xFF, 0x0F, 0x17, 0x2A) : Color.FromArgb(0xFF, 0xF3, 0xF4, 0xF6);

        UpdateBrush(window, "ChatIncomingBgBrush", chatIncomingBg);
        UpdateBrush(window, "ChatIncomingBorderBrush", chatIncomingBorder);
        UpdateBrush(window, "ChatIncomingBadgeBrush", chatIncomingBadge);
        UpdateBrush(window, "ChatIncomingTextBrush", chatIncomingText);

        UpdateBrush(window, "ChatOutgoingBgBrush", chatOutgoingBg);
        UpdateBrush(window, "ChatOutgoingBorderBrush", chatOutgoingBorder);
        UpdateBrush(window, "ChatOutgoingBadgeBrush", chatOutgoingBadge);
        UpdateBrush(window, "ChatOutgoingTextBrush", chatOutgoingText);

        NotifyWindowsThemeChanged(window);
    }

    private static void NotifyWindowsThemeChanged(Window? targetWindow)
    {
        var visited = new HashSet<Window>();
        if (targetWindow != null)
        {
            visited.Add(targetWindow);
            DispatchThemeChanged(targetWindow);
        }

        if (Application.Current != null)
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (visited.Add(w))
                {
                    DispatchThemeChanged(w);
                }
            }
        }
    }

    private static void DispatchThemeChanged(Window w)
    {
        switch (w)
        {
            case OverlayWindow overlay:
                overlay.OnThemeChanged();
                break;
            case PersonaAnalyticsWindow personaWin:
                personaWin.OnThemeChanged();
                break;
            case AdviceAnalyticsWindow adviceWin:
                adviceWin.OnThemeChanged();
                break;
        }
    }

    private static void UpdateBrush(Window? window, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        if (window?.Resources != null)
        {
            window.Resources[key] = brush;
        }
        if (Application.Current != null)
        {
            Application.Current.Resources[key] = brush;
            foreach (Window w in Application.Current.Windows)
            {
                if (w != window && w.Resources != null)
                {
                    w.Resources[key] = brush;
                }
            }
        }
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

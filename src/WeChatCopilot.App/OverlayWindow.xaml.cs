using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WeChatCopilot.AI;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Diagnostics;
using WeChatCopilot.Core.Layout;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Core.Parsing;
using WeChatCopilot.Data;
using WeChatCopilot.Data.Security;
using WeChatCopilot.Windows;

namespace WeChatCopilot.App;

public partial class OverlayWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_RELOCATE = 2;
    private const int HOTKEY_READ = 4;
    private const HotkeyModifiers HotkeyMods = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private const uint VK_W = 0x57;
    private const uint VK_E = 0x45;
    private const uint VK_D = 0x44;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const double ResizeBorder = 7.0;

    private readonly IWeChatWindowLocator _locator = new WeChatWindowLocator();
    private readonly IWindowTracker _tracker = new PollingWindowTracker();
    private readonly WeChatHeaderDetector _headerDetector = new();
    private readonly ConversationBuffer _conversation = new();
    private readonly List<HistoryMessage> _history = new();
    private readonly SqliteStorage _sqliteStorage = new();
    private readonly PersonaStore _personaStore;
    private readonly SkillStore _skillStore = new();
    private readonly SettingsStore _settingsStore = new();

    private CancellationTokenSource? _settingTestCts;
    private CancellationTokenSource? _adviceCts;
    private CancellationTokenSource? _distillCts;

    private readonly DispatcherTimer _relocateTimer;
    private readonly DispatcherTimer _autoReadTimer;
    private readonly DispatcherTimer _saveSizeDebounceTimer;

    private IHotkeyManager? _hotkeys;
    private System.Windows.Forms.NotifyIcon? _tray;
    private nint _hwnd;
    private bool _pinned;
    private bool _manualHidden;
    private bool _autoRead = true;
    private bool _reading;
    private bool _autoSyncContact = true;
    private bool _isSyncingPersonaSelection;
    private bool _isSyncingRecentSessionSelection;
    private bool _isSyncingSkillSelection;
    private string _currentChatContact = string.Empty;
    private IReadOnlyList<HistoryMessage> _currentHistoryMessages = Array.Empty<HistoryMessage>();
    private string _historyContactName = string.Empty;
    private string? _mySelfNickname;
    private Persona? _currentLoadedPersona;
    private DistillSkill _currentSkill = DistillSkillPresets.Intimacy;
    private const int MaxRadarDimensions = 6;
    private bool _showAllRadarDimensions = false;

    private UnifiedAiAdvice? _lastUnifiedAdvice;
    private string? _lastAdviceContactName;
    private DistillSkill? _lastAdviceSkill;
    private string? _lastAdviceGoal;
    private string? _lastAdviceIntent;

    public OverlayWindow()
    {
        InitializeComponent();

        _personaStore = new PersonaStore(null, _sqliteStorage);

        _tracker.TargetChanged += OnTargetChanged;

        _relocateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _relocateTimer.Tick += (_, _) =>
        {
            var cur = _tracker.GetCurrent();
            if (!_pinned && (cur is null || !cur.IsVisible))
            {
                TryLocate();
            }
        };

        _autoReadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoReadTimer.Tick += async (_, _) => await AutoReadTickAsync();

        _saveSizeDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveSizeDebounceTimer.Tick += (_, _) =>
        {
            _saveSizeDebounceTimer.Stop();
            if (IsLoaded && WindowState == WindowState.Normal && ActualWidth >= MinWidth && ActualHeight >= MinHeight)
            {
                var cur = _settingsStore.Load();
                if (Math.Abs(cur.WindowWidth - ActualWidth) > 1 || Math.Abs(cur.WindowHeight - ActualHeight) > 1)
                {
                    cur.WindowWidth = Math.Round(ActualWidth);
                    cur.WindowHeight = Math.Round(ActualHeight);
                    _settingsStore.Save(cur);
                }
            }
        };

        SizeChanged += (_, _) =>
        {
            if (IsLoaded && WindowState == WindowState.Normal)
            {
                _saveSizeDebounceTimer.Stop();
                _saveSizeDebounceTimer.Start();
            }
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget != null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget != null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
        Background = Brushes.Transparent;

        var savedSettings = _settingsStore.Load();
        if (savedSettings.WindowWidth >= MinWidth && savedSettings.WindowWidth <= MaxWidth)
        {
            Width = savedSettings.WindowWidth;
        }
        if (savedSettings.WindowHeight >= MinHeight && savedSettings.WindowHeight <= MaxHeight)
        {
            Height = savedSettings.WindowHeight;
        }

        if (savedSettings.WindowOpacity >= 0.4 && savedSettings.WindowOpacity <= 1.0)
        {
            Opacity = savedSettings.WindowOpacity;
        }
        if (!string.IsNullOrWhiteSpace(savedSettings.SelfNickname))
        {
            _mySelfNickname = savedSettings.SelfNickname.Trim();
        }

        ThemeManager.ApplyTheme(this, savedSettings.ThemeMode ?? "Light");

        if (AiContextCountCombo != null)
        {
            int savedCount = savedSettings.ContextMessageCount > 0 ? savedSettings.ContextMessageCount : 10;
            foreach (ComboBoxItem item in AiContextCountCombo.Items)
            {
                if (item.Tag is string tagStr && int.TryParse(tagStr, out int c) && c == savedCount)
                {
                    AiContextCountCombo.SelectedItem = item;
                    break;
                }
            }
        }

        InitHotkeys();
        InitTray();

        RefreshSavedPersonasCombo();
        InitSkillCombo();
        UpdateAutoSyncToggleUi();
        TryLocate();
        _relocateTimer.Start();
        _autoReadTimer.Start();
        _ = InitializeActiveSessionOnStartupAsync();
    }

    private async Task InitializeActiveSessionOnStartupAsync()
    {
        await RefreshRecentSessionsAsync(autoSelectActive: true);
        await ReadConversationAsync(silent: true, forceAutoDetect: true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tracker.Dispose();
        _relocateTimer.Stop();
        _autoReadTimer.Stop();
        _saveSizeDebounceTimer.Stop();
        _hotkeys?.Dispose();
        _settingTestCts?.Cancel();
        _settingTestCts?.Dispose();
        _adviceCts?.Cancel();
        _adviceCts?.Dispose();
        _distillCts?.Cancel();
        _distillCts?.Dispose();
        _qaCts?.Cancel();
        _qaCts?.Dispose();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            _hotkeys?.OnHotkeyMessage(wParam.ToInt32());
            handled = true;
        }
        else if (msg == WM_NCHITTEST)
        {
            if (WindowState == WindowState.Normal)
            {
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                try
                {
                    Point pt = PointFromScreen(new Point(x, y));
                    double w = ActualWidth;
                    double h = ActualHeight;
                    if (w > 0 && h > 0)
                    {
                        bool left = pt.X <= ResizeBorder;
                        bool right = pt.X >= w - ResizeBorder;
                        bool top = pt.Y <= ResizeBorder;
                        bool bottom = pt.Y >= h - ResizeBorder;

                        if (top && left) { handled = true; return (nint)HTTOPLEFT; }
                        if (top && right) { handled = true; return (nint)HTTOPRIGHT; }
                        if (bottom && left) { handled = true; return (nint)HTBOTTOMLEFT; }
                        if (bottom && right) { handled = true; return (nint)HTBOTTOMRIGHT; }
                        if (left) { handled = true; return (nint)HTLEFT; }
                        if (right) { handled = true; return (nint)HTRIGHT; }
                        if (top) { handled = true; return (nint)HTTOP; }
                        if (bottom) { handled = true; return (nint)HTBOTTOM; }
                    }
                }
                catch
                {
                    // 忽略销毁或未就绪时的坐标转换异常
                }
            }
        }

        return nint.Zero;
    }

    private void InitHotkeys()
    {
        _hotkeys = new HotkeyManager(_hwnd);
        _hotkeys.Register(HOTKEY_TOGGLE, HotkeyMods, VK_W, ToggleVisibility);
        _hotkeys.Register(HOTKEY_RELOCATE, HotkeyMods, VK_E, TryLocate);
        _hotkeys.Register(HOTKEY_READ, HotkeyMods, VK_D, () => _ = ReadConversationAsync());
    }

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "微信 Copilot · 聊天副驾"
        };

        _tray.DoubleClick += (_, _) => ToggleVisibility();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => ToggleVisibility());
        menu.Items.Add("重新对齐微信", null, (_, _) => TryLocate());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出助手", null, (_, _) => Application.Current.Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        // 1. 尝试从应用运行目录直接加载 app.ico
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string directIco = System.IO.Path.Combine(baseDir, "app.ico");
            if (File.Exists(directIco))
            {
                return new System.Drawing.Icon(directIco);
            }
        }
        catch
        {
            // 继续尝试后续方案
        }

        // 2. 尝试从 WPF 嵌入资源读取 app.ico (支持 pack://application:,,,/app.ico 或带有 AssemblyName 的 Uri)
        try
        {
            var uri = new Uri("pack://application:,,,/WeChatCopilot;component/app.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri)
                ?? Application.GetResourceStream(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                return new System.Drawing.Icon(ms);
            }
        }
        catch
        {
            // 继续尝试后续方案
        }

        // 3. 尝试从当前主进程/可执行文件中提取关联图标 (因项目配置了 <ApplicationIcon>app.ico</ApplicationIcon>)
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // 继续尝试后续方案
        }

        // 4. 尝试从开发环境源码相对路径查找 (调试阶段运行环境)
        try
        {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            var dir = new DirectoryInfo(currentDir);
            while (dir != null)
            {
                string devIco = System.IO.Path.Combine(dir.FullName, "src", "WeChatCopilot.App", "app.ico");
                if (File.Exists(devIco))
                {
                    return new System.Drawing.Icon(devIco);
                }
                dir = dir.Parent;
            }
        }
        catch
        {
            // 兜底
        }

        return System.Drawing.SystemIcons.Application;
    }

    public void ToggleVisibility()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            _manualHidden = false;
            Show();
            Activate();
        }
        else if (IsVisible)
        {
            _manualHidden = true;
            Hide();
        }
        else
        {
            _manualHidden = false;
            Show();
            Activate();
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Normal)
        {
            _manualHidden = false;
            TryLocate();
        }
    }

    // ===================== 顶部控制与诊断 =====================

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        _pinned = PinButton.IsChecked == true;
        PinButton.Content = _pinned ? "🔓 已固定" : "📌 吸附中";
        HeaderTitlePanel.Cursor = _pinned ? Cursors.SizeAll : Cursors.Arrow;

        if (_pinned)
        {
            Topmost = true;
            WindowPositioner.MoveToTopmost(_hwnd, (int)Left, (int)Top);
        }
        else
        {
            Topmost = false;
            TryLocate();
        }
    }

    private void HeaderTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pinned && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void RelocateButton_Click(object sender, RoutedEventArgs e) => TryLocate();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        _manualHidden = true;
        Hide();
        if (_tray != null)
        {
            _tray.Visible = true;
            _tray.ShowBalloonTip(
                1500,
                "微信 Copilot · 已最小化到托盘",
                "助手已缩至右下角系统托盘后台运行，双击托盘图标或使用快捷键 Ctrl+Alt+W 可重新唤醒。",
                System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private void DiagToggle_Changed(object sender, RoutedEventArgs e)
    {
        DiagPanel.Visibility = DiagToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiagToggle.Content = DiagToggle.IsChecked == true ? "诊断 ▴" : "诊断 ▾";
    }

    private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 4;
        SettingSubTabAi.IsChecked = true;
        SwitchSettingSubTab(0);
    }

    private void NavigateToSettings_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 4;
    }

    // ===================== TAB 标签切换 =====================

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewChat is null || ViewAi is null || ViewPersona is null || ViewQa is null || ViewSettings is null)
        {
            return;
        }

        int index = MainTabControl.SelectedIndex;
        ViewChat.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewAi.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewPersona.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewQa.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 0)
        {
            _ = RefreshRecentSessionsAsync(autoSelectActive: false);
        }
        else if (index == 1)
        {
            UpdateAiTargetInfo();
        }
        else if (index == 2)
        {
            RefreshSavedPersonasCombo();
            _ = RefreshRecentSessionsAsync(autoSelectActive: false);
            string target = ContactBox.Text.Trim();
            if (!string.IsNullOrEmpty(target))
            {
                CheckAndLoadPersona(target);
            }
        }
        else if (index == 3)
        {
            if (_currentQaSession == null)
            {
                InitQaSessions();
            }
            UpdateQaContextDisplay();
        }
        else if (index == 4)
        {
            LoadSettingsView();
        }
    }


    // ===================== 微信窗口定位、对齐与跟随 =====================

    private void TryLocate()
    {
        var info = _locator.FindWeChatMainWindow();
        if (info is null)
        {
            _tracker.Stop();
            StatusText.Text = "未找到微信窗口（请确认 Weixin.exe 已运行）";
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            BoundsText.Text = "等待微信主窗口启动并可见...";
            if (!_pinned && IsVisible)
            {
                Hide();
            }
            return;
        }

        StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");
        _tracker.Start(info);
        UpdatePlacement(info);
    }

    private void OnTargetChanged(WindowInfo? info)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (info is null)
            {
                StatusText.Text = "微信窗口已最小化或失去连接...";
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                if (!_pinned && IsVisible)
                {
                    Hide();
                }
                TryLocate();
                return;
            }

            UpdatePlacement(info);
        });
    }

    private void UpdatePlacement(WindowInfo info)
    {
        if (info.ClassName == "ChatWnd")
        {
            // 独立单聊窗口：标题即为联系人姓名
            string contact = CleanContactName(info.Title);
            if (!string.IsNullOrEmpty(contact) && contact != _mySelfNickname)
            {
                bool changed = !string.Equals(_currentChatContact, contact, StringComparison.OrdinalIgnoreCase);
                _currentChatContact = contact;
                StatusText.Text = $"已连接：{contact}";
                StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");

                if (_autoSyncContact && changed)
                {
                    _ = SwitchActiveSessionContactAsync(contact, "微信独立聊天窗口");
                }
            }
        }
        else
        {
            // 微信主窗口：标题通常为登录用户自己的微信昵称或“微信”
            string selfOrWeChat = CleanContactName(info.Title);
            if (!string.IsNullOrEmpty(selfOrWeChat) && selfOrWeChat != "微信" && selfOrWeChat != "WeChat")
            {
                _mySelfNickname = selfOrWeChat;
                StatusText.Text = $"微信已就绪 ({_mySelfNickname})";
            }
            else
            {
                StatusText.Text = "微信已就绪（主界面）";
            }

            StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");
        }

        BoundsText.Text =
            $"句柄: {info.Handle} | 标题: {info.Title}\n" +
            $"类名: {info.ClassName} | 模式: {(_pinned ? "已固定" : "跟随中")}\n" +
            $"坐标: ({info.Bounds.X}, {info.Bounds.Y}) | 尺寸: {info.Bounds.Width}x{info.Bounds.Height}";

        if (_pinned)
        {
            if (!Topmost)
            {
                Topmost = true;
            }
            return;
        }

        if (WindowState == WindowState.Minimized || _manualHidden)
        {
            return;
        }

        if (info.IsMinimized || !info.IsVisible)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible && !_manualHidden)
        {
            Show();
        }

        if (Topmost)
        {
            Topmost = false;
        }

        var overlay = new OverlaySize((int)ActualWidth, (int)ActualHeight);
        var (x, y) = OverlayLayout.ComputeSnapPosition(
            info.Bounds, overlay, SnapSide.Right, gap: 0, alignTop: true);

        // 智能同层级跟随：副驾自身聚焦时维持 Z 轴，未聚焦时紧随微信同一层级，绝不在其他程序前置顶
        WindowPositioner.MoveCompanion(_hwnd, info.Handle, x, y);
    }

    private static string CleanContactName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        title = title.Trim();
        if (title.Equals("微信", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("WeChat", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var match = Regex.Match(title, @"^(.*?)\s*[\(（]\d+[\)）]$");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return title;
    }
}

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
using System.Windows.Threading;
using System.Windows.Shapes;
using Microsoft.Win32;
using WeChatCopilot.AI;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Layout;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Core.Parsing;
using WeChatCopilot.Data;
using WeChatCopilot.Data.Security;
using WeChatCopilot.Ocr;
using WeChatCopilot.Windows;

namespace WeChatCopilot.App;

/// <summary>
/// 悬浮窗主界面：现代 Fluent 风格 UI，支持对话识读、多候选回复建议、六维潜台词意图、
/// 以及基于微信当前会话的 TraceMemo 历史拉取与多维人格画像（Persona）蒸馏闭环。
/// </summary>
public partial class OverlayWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_RELOCATE = 2;
    private const int HOTKEY_OCR = 3;
    private const int HOTKEY_READ = 4;
    private const HotkeyModifiers HotkeyMods = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    private const uint VK_W = 0x57;
    private const uint VK_E = 0x45;
    private const uint VK_O = 0x4F;
    private const uint VK_D = 0x44;
    private int _ocrUpscaleFactor = 3;

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
    private readonly IOcrEngine _ocrEngine = new WindowsMediaOcrEngine();
    private readonly MessageSegmenter _segmenter = new();
    private readonly ConversationBuffer _conversation = new();
    private readonly List<HistoryMessage> _history = new();
    private readonly SqliteStorage _sqliteStorage = new();
    private readonly PersonaStore _personaStore;
    private readonly SkillStore _skillStore = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly ChatRegionOptions _cropOptions = new();

    private CancellationTokenSource? _settingTestCts;

    private readonly DispatcherTimer _relocateTimer;
    private readonly DispatcherTimer _autoReadTimer;
    private readonly DispatcherTimer _saveSizeDebounceTimer;

    private IHotkeyManager? _hotkeys;
    private System.Windows.Forms.NotifyIcon? _tray;
    private nint _hwnd;
    private bool _pinned;
    private bool _manualHidden;
    private bool _autoRead;
    private bool _reading;
    private bool _autoSyncContact = true;
    private bool _isSyncingPersonaSelection;
    private bool _isSyncingSkillSelection;
    private string _currentChatContact = string.Empty;
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

        _autoReadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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

        ThemeManager.ApplyTheme(this, savedSettings.ThemeMode ?? "Dark");

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
        TryLocate();
        _relocateTimer.Start();
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
        _hotkeys.Register(HOTKEY_OCR, HotkeyMods, VK_O, () => _ = RunOcrAsync());
        _hotkeys.Register(HOTKEY_READ, HotkeyMods, VK_D, () => _ = ReadConversationAsync());
    }

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
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

    private void QuitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void DiagToggle_Changed(object sender, RoutedEventArgs e)
    {
        DiagPanel.Visibility = DiagToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiagToggle.Content = DiagToggle.IsChecked == true ? "诊断 ▴" : "诊断 ▾";
    }

    private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 3;
        SettingSubTabAi.IsChecked = true;
        SwitchSettingSubTab(0);
    }

    private void NavigateToSettings_Click(object sender, RoutedEventArgs e)
    {
        MainTabControl.SelectedIndex = 3;
    }

    // ===================== TAB 标签切换 =====================

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewChat is null || ViewAi is null || ViewPersona is null || ViewSettings is null)
        {
            return;
        }

        int index = MainTabControl.SelectedIndex;
        ViewChat.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewAi.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewPersona.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 1)
        {
            UpdateAiTargetInfo();
        }
        else if (index == 2)
        {
            RefreshSavedPersonasCombo();
            string target = ContactBox.Text.Trim();
            if (!string.IsNullOrEmpty(target))
            {
                CheckAndLoadPersona(target);
            }
        }
        else if (index == 3)
        {
            LoadSettingsView();
        }
    }

    // ===================== VIEW 0: 对话 TAB =====================

    private async void ReadButton_Click(object sender, RoutedEventArgs e) => await ReadConversationAsync();

    private void AutoReadButton_Changed(object sender, RoutedEventArgs e)
    {
        _autoRead = AutoReadButton.IsChecked == true;
        AutoReadButton.Content = _autoRead ? "自动读取: 开" : "自动读取: 关";

        if (_autoRead)
        {
            _autoReadTimer.Start();
        }
        else
        {
            _autoReadTimer.Stop();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _conversation.Clear();
        RefreshChatView();
    }

    private async void OcrButton_Click(object sender, RoutedEventArgs e) => await RunOcrAsync();

    private async Task AutoReadTickAsync()
    {
        if (!_autoRead || _reading || _tracker.GetCurrent() is null)
        {
            return;
        }

        await ReadConversationAsync(silent: true);
    }

    private async Task ReadConversationAsync(bool silent = false)
    {
        var info = _tracker.GetCurrent();
        if (info is null)
        {
            if (!silent)
            {
                StatusText.Text = "尚未定位到微信窗口，请先点“定位”";
            }

            return;
        }

        if (_reading)
        {
            return;
        }

        _reading = true;
        if (!silent)
        {
            ReadButton.IsEnabled = false;
        }

        try
        {
            // 策略 1: 优先尝试从 TraceMemo 本地消息流直读真实数据（100% 精度，零 OCR 误差，表情包与分享卡片精准解析）
            string contact = !string.IsNullOrWhiteSpace(ContactBox.Text) ? ContactBox.Text.Trim() : _currentChatContact;
            bool tmSynced = false;

            if (!string.IsNullOrEmpty(contact))
            {
                try
                {
                    var settings = new SettingsStore().Load();
                    using var tmClient = new TraceMemoClient(settings.TraceMemoBaseUrl, null);
                    var history = await tmClient.FetchHistoryAsync(contact);
                    if (history.Success && history.Messages.Count > 0)
                    {
                        var recent = history.Messages.TakeLast(25).Select(m => new ChatMessage(m.Role, m.Text)).ToList();
                        _conversation.Clear();
                        _conversation.Ingest(recent);
                        RefreshChatView();
                        tmSynced = true;

                        // 异步持久化至本地 SQLite 数据库
                        _ = Task.Run(() => _sqliteStorage.SaveMessages(contact, history.Messages));

                        if (!silent)
                        {
                            StatusText.Text = $"已从 TraceMemo 精准同步最新 {recent.Count} 条记录 (零乱码)";
                            StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");
                        }
                    }
                }
                catch
                {
                    // TraceMemo 未运行或异常，静默降级到屏幕视觉 OCR
                }
            }

            // 策略 2: 若 TraceMemo 未接入，则执行高保真屏幕截屏 + OCR 识别 + 智能降噪
            if (!tmSynced)
            {
                var crop = info.ClassName == "ChatWnd"
                    ? new ChatRegionOptions { TrimLeftRatio = 0.02, TrimTopRatio = 0.08, TrimBottomRatio = 0.25 }
                    : _cropOptions;
                var region = ChatRegionCropper.ComputeChatRegion(info.Bounds, crop);
                var image = ScreenCapture.CaptureRegion(region);
                if (image is null)
                {
                    return;
                }

                image = image.ScaleNearest(_ocrUpscaleFactor);
                var ocr = await _ocrEngine.RecognizeAsync(image);
                var frame = _segmenter.Segment(ocr, image.Width);
                int added = _conversation.Ingest(frame);

                if (!string.IsNullOrEmpty(contact) && frame.Count > 0)
                {
                    // 屏幕 OCR 气泡无确切时间戳，传 null 使 msg_hash 基于内容幂等去重
                    var ocrHistory = frame.Select(m => new HistoryMessage(m.Role, m.Text, null)).ToList();
                    _ = Task.Run(() => _sqliteStorage.SaveMessages(contact, ocrHistory));
                }

                if (!silent || added > 0)
                {
                    RefreshChatView();
                    if (!silent)
                    {
                        StatusText.Text = "屏幕视觉 OCR 读取完成 (已过滤表情包与符号乱码)";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                OcrOutput.Visibility = Visibility.Visible;
                ChatScrollViewer.Visibility = Visibility.Collapsed;
                ChatEmptyPlaceholder.Visibility = Visibility.Collapsed;
                OcrOutput.Text = "读取对话失败：" + ex.Message;
            }
        }
        finally
        {
            _reading = false;
            if (!silent)
            {
                ReadButton.IsEnabled = true;
            }
        }
    }

    private void RefreshChatView()
    {
        OcrOutput.Visibility = Visibility.Collapsed;
        if (_conversation.Count == 0)
        {
            ChatEmptyPlaceholder.Visibility = Visibility.Visible;
            ChatScrollViewer.Visibility = Visibility.Collapsed;
            ChatBubbleList.ItemsSource = null;
        }
        else
        {
            ChatEmptyPlaceholder.Visibility = Visibility.Collapsed;
            ChatScrollViewer.Visibility = Visibility.Visible;
            ChatBubbleList.ItemsSource = _conversation.Messages.ToList();
            ChatScrollViewer.ScrollToEnd();
        }
    }

    private async Task RunOcrAsync()
    {
        var info = _tracker.GetCurrent();
        if (info is null)
        {
            OcrOutput.Visibility = Visibility.Visible;
            OcrOutput.Text = "微信未连接，无法执行 OCR。";
            return;
        }

        OcrButton.IsEnabled = false;
        try
        {
            var image = ScreenCapture.CaptureRegion(info.Bounds);
            if (image is null)
            {
                OcrOutput.Visibility = Visibility.Visible;
                OcrOutput.Text = "截图失败。";
                return;
            }

            image = image.ScaleNearest(_ocrUpscaleFactor);
            var result = await _ocrEngine.RecognizeAsync(image);

            var sb = new StringBuilder();
            sb.AppendLine($"[整窗 OCR 原文识别结果] 耗时={result.Elapsed.TotalMilliseconds:F0}ms 行数={result.LineCount}");
            sb.AppendLine(new string('-', 35));
            sb.AppendLine(result.Text);

            ChatEmptyPlaceholder.Visibility = Visibility.Collapsed;
            ChatScrollViewer.Visibility = Visibility.Collapsed;
            OcrOutput.Visibility = Visibility.Visible;
            OcrOutput.Text = sb.ToString();
        }
        catch (Exception ex)
        {
            OcrOutput.Visibility = Visibility.Visible;
            OcrOutput.Text = "OCR 失败：" + ex.Message;
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
    }

    // ===================== VIEW 1: AI 建议 TAB =====================

    private void UpdateAiTargetInfo()
    {
        string target = ContactBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            target = _currentChatContact;
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            AiTargetContactText.Text = target;
            AiTargetContactText.ToolTip = $"当前分析目标：{target}";
            var persona = _personaStore.Load(target);
            if (persona is { Traits.Count: > 0 })
            {
                AiPersonaBadge.Visibility = Visibility.Visible;
                AiPersonaBadgeText.Text = $"已融入画像 ({persona.Traits.Count}项)";
                AiPersonaBadge.ToolTip = $"已加载「{target}」的人格特质画像（共 {persona.Traits.Count} 个维度），分析时将自动代入其思维模型与沟通偏好";
            }
            else
            {
                AiPersonaBadge.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(persona?.UltimateGoal))
            {
                AiGoalBadge.Visibility = Visibility.Visible;
                AiGoalBadgeText.Text = $"🚩 目的: {persona.UltimateGoal}";
                AiGoalBadge.ToolTip = $"已为「{target}」配置终极战略目的：{persona.UltimateGoal}\n所有生成的回复都将以此战略目的为最高导向！可在【设置 -> 画像目的】中修改。";
                AiNoGoalPromptText.Visibility = Visibility.Collapsed;
            }
            else
            {
                AiGoalBadge.Visibility = Visibility.Collapsed;
                AiNoGoalPromptText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            AiTargetContactText.Text = "当前对话好友";
            AiTargetContactText.ToolTip = "未指定特定联系人，将依据当前读取到的对话上下文进行智能分析";
            AiPersonaBadge.Visibility = Visibility.Collapsed;
            AiGoalBadge.Visibility = Visibility.Collapsed;
            AiNoGoalPromptText.Visibility = Visibility.Visible;
        }
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation.Count == 0)
        {
            SetAiStatus("对话缓冲为空，请先在「当前对话」点击【⚡ 读取当前对话】。", isError: true);
            return;
        }

        int count = CountBox.SelectedIndex == 1 ? 5 : 3;
        DistillSkill activeSkill = _currentSkill ?? DistillSkillPresets.Intimacy;

        GenerateButton.IsEnabled = false;

        string target = ContactBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            target = _currentChatContact;
        }

        Persona? personaContext = null;
        if (IncludePersonaCheck.IsChecked == true && !string.IsNullOrEmpty(target))
        {
            personaContext = _personaStore.Load(target);
        }

        string userIntent = AiIntentBox.Text.Trim();
        string? ultimateGoal = personaContext?.UltimateGoal;

        string goalHint = !string.IsNullOrWhiteSpace(ultimateGoal) ? $"，战略导向【{ultimateGoal}】" : string.Empty;
        SetAiStatus($"AI 正在结合【{activeSkill.Icon} {activeSkill.Name}】策略与对方画像{goalHint}，深度剖析意图并生成 {count} 条针对性回复建议...", isError: false);

        try
        {
            var settings = new SettingsStore().Load();
            string key = DpapiProtector.Unprotect(settings.EncryptedApiKey);
            int timeoutSec = Math.Max(settings.TimeoutSeconds, 120);
            using var provider = new OpenAiCompatibleProvider(settings.Endpoint, () => key, timeout: TimeSpan.FromSeconds(timeoutSec));

            var latestIncoming = _conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Incoming) ?? _conversation.Messages.LastOrDefault();
            string targetStatement = latestIncoming?.Text ?? string.Empty;

            int contextCount = 10;
            if (AiContextCountCombo?.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Tag?.ToString(), out int parsed) && parsed > 0)
            {
                contextCount = parsed;
            }
            else if (settings.ContextMessageCount > 0)
            {
                contextCount = settings.ContextMessageCount;
            }

            AiRequest req = PromptBuilder.BuildUnifiedAdvice(
                settings,
                _conversation.Messages,
                count,
                personaContext,
                target,
                relationship: null,
                activeSkill,
                userIntent: userIntent,
                ultimateGoal: ultimateGoal,
                contextCount: contextCount);

            AiReply reply = await provider.CompleteAsync(req);
            AiStatusBanner.Visibility = Visibility.Collapsed;

            if (!reply.Success)
            {
                SetAiStatus("AI 生成失败：" + reply.Error, isError: true);
                return;
            }

            var advice = AiOutputParser.ParseUnifiedAdvice(reply.Text, targetStatement);
            ShowUnifiedAdvice(advice, target, activeSkill);
        }
        catch (Exception ex)
        {
            AiStatusBanner.Visibility = Visibility.Collapsed;
            SetAiStatus("调用异常：" + ex.Message, isError: true);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
            GenerateButton.Content = "🚀 智能分析回复建议";
        }
    }

    private void AiContextCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AiContextCountCombo?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr && int.TryParse(tagStr, out int count) && count > 0)
        {
            var s = _settingsStore.Load();
            if (s.ContextMessageCount != count)
            {
                s.ContextMessageCount = count;
                _settingsStore.Save(s);
            }
        }
    }

    private void ShowUnifiedAdvice(UnifiedAiAdvice advice, string? contactName, DistillSkill? activeSkill = null)
    {
        AiEmptyPlaceholder.Visibility = Visibility.Collapsed;
        AiResultPanel.Visibility = Visibility.Visible;

        var skill = activeSkill ?? _currentSkill;
        if (skill != null)
        {
            AiAppliedSkillText.Text = $"{skill.Icon} {skill.Name}";
            SubtextStrategyTitle.Text = $"⚡ {skill.Name} 实战破局策略与避坑指南";
        }

        string targetAuthor = !string.IsNullOrWhiteSpace(contactName) ? $"{contactName} 原话" : "对方最新发言";
        SubtextTargetAuthor.Text = targetAuthor;

        if (!string.IsNullOrWhiteSpace(advice.ContextFlowSummary))
        {
            ContextFlowBorder.Visibility = Visibility.Visible;
            ContextFlowText.Text = advice.ContextFlowSummary;
        }
        else
        {
            ContextFlowBorder.Visibility = Visibility.Collapsed;
        }

        SubtextTargetText.Text = !string.IsNullOrWhiteSpace(advice.TargetQuote)
            ? $"“{advice.TargetQuote}”"
            : "（无具体原话）";

        SubtextSubtextText.Text = !string.IsNullOrWhiteSpace(advice.Subtext)
            ? advice.Subtext
            : (!string.IsNullOrWhiteSpace(advice.Literal) ? advice.Literal : "通过语气分析，对方此时较为平静务实。");

        SubtextIntentText.Text = !string.IsNullOrWhiteSpace(advice.Intent)
            ? advice.Intent
            : "希望就当前事项目标取得共识或明确后续推进要求。";

        SubtextStrategyText.Text = !string.IsNullOrWhiteSpace(advice.Strategy)
            ? advice.Strategy
            : "建议保持积极客气、就事论事的态度，明确时间与具体要求。";

        SuggestionCards.ItemsSource = advice.Suggestions;
        UnifiedAiScroll.ScrollToTop();

        _lastUnifiedAdvice = advice;
        _lastAdviceContactName = contactName;
        _lastAdviceSkill = activeSkill ?? _currentSkill;
        _lastAdviceGoal = _currentLoadedPersona?.UltimateGoal ?? (AiGoalBadge?.Visibility == Visibility.Visible ? AiGoalBadgeText?.Text : null);
        _lastAdviceIntent = AiIntentBox?.Text?.Trim();
    }

    private void SetAiStatus(string text, bool isError = false)
    {
        AiStatusBanner.Visibility = Visibility.Visible;
        AiStatusText.Text = text;
        if (isError)
        {
            AiStatusProgress.Visibility = Visibility.Collapsed;
            AiStatusSpinner.Visibility = Visibility.Collapsed;
            AiStatusIcon.Visibility = Visibility.Visible;
            AiStatusIcon.Text = "⚠️";
            AiStatusBanner.Background = new SolidColorBrush(Color.FromArgb(36, 239, 68, 68));
            AiStatusBanner.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 239, 68, 68));
            AiStatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            AiStatusDismissBtn.Visibility = Visibility.Visible;
            GenerateButton.Content = "🚀 智能分析回复建议";
            GenerateButton.IsEnabled = true;
        }
        else
        {
            AiStatusProgress.Visibility = Visibility.Visible;
            AiStatusSpinner.Visibility = Visibility.Visible;
            AiStatusIcon.Visibility = Visibility.Collapsed;
            AiStatusBanner.Background = new SolidColorBrush(Color.FromArgb(34, 59, 130, 246));
            AiStatusBanner.BorderBrush = new SolidColorBrush(Color.FromArgb(85, 59, 130, 246));
            AiStatusText.Foreground = (SolidColorBrush)FindResource("TextPrimaryBrush");
            AiStatusDismissBtn.Visibility = Visibility.Collapsed;
            GenerateButton.Content = "⏳ 分析建议中...";
            GenerateButton.IsEnabled = false;
        }
    }

    private void AiStatusDismiss_Click(object sender, RoutedEventArgs e)
    {
        AiStatusBanner.Visibility = Visibility.Collapsed;
    }

    private async void CopySuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string text)
        {
            try
            {
                Clipboard.SetText(text);
                btn.Content = "✔ 已复制";
                btn.Foreground = (SolidColorBrush)FindResource("GreenBrush");
                await Task.Delay(1500);
                btn.Content = "📋 复制";
                btn.ClearValue(ForegroundProperty);
            }
            catch
            {
                // ignored
            }
        }
    }

    // ===================== VIEW 2: 人格画像 TAB (FR-3/FR-8 闭环) =====================

    private void ContactBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingPersonaSelection)
        {
            return;
        }

        string name = ContactBox.Text.Trim();
        if (_autoSyncContact && name != _currentChatContact)
        {
            _autoSyncContact = false;
        }

        // 切换联系人时，彻底清空上一人的历史缓存，杜绝数据污染与残留
        if (!string.Equals(_historyContactName, name, StringComparison.OrdinalIgnoreCase))
        {
            _history.Clear();
            _historyContactName = string.Empty;
        }

        if (!string.IsNullOrEmpty(name))
        {
            _isSyncingPersonaSelection = true;
            try
            {
                if (SavedPersonasCombo.ItemsSource is IEnumerable<string> list && list.Contains(name))
                {
                    SavedPersonasCombo.SelectedItem = name;
                }
                else
                {
                    SavedPersonasCombo.SelectedIndex = -1;
                }
            }
            finally
            {
                _isSyncingPersonaSelection = false;
            }

            CheckAndLoadPersona(name);
            UpdateAiTargetInfo();
        }
        else
        {
            _currentLoadedPersona = null;
            PersonaScroll.Visibility = Visibility.Collapsed;
            PersonaEmptyCard.Visibility = Visibility.Visible;
            PersonaEmptyTitle.Text = "请选择或输入联系人";
            RadarCanvas.Children.Clear();
            UpdateAiTargetInfo();
        }
    }

    /// <summary>
    /// 尝试通过微信窗口探测当前聊天好友名称：
    /// 1. 若为独立聊天窗口（ChatWnd），直接从窗口标题获取；
    /// 2. 若为微信主窗口，通过截取聊天窗顶端标题栏区域进行轻量 OCR 识别。
    /// </summary>
    private async Task<string?> DetectContactFromWeChatHeaderAsync()
    {
        var info = _tracker.GetCurrent();
        if (info is null)
        {
            return null;
        }

        if (info.ClassName == "ChatWnd")
        {
            string contact = CleanContactName(info.Title);
            if (!string.IsNullOrEmpty(contact) && contact != _mySelfNickname && !TraceMemoClient.IsSystemContact(null, contact))
            {
                return contact;
            }
        }

        // 针对微信主界面：截取聊天面板上方中央的联系人/群聊标题区域（横向约 28%~72%，纵向约 0%~8%）
        try
        {
            int hX = info.Bounds.X + (int)(info.Bounds.Width * 0.28);
            int hY = info.Bounds.Y + (int)(info.Bounds.Height * 0.02);
            int hW = (int)(info.Bounds.Width * 0.45);
            int hH = (int)(info.Bounds.Height * 0.07);

            var headerBounds = new WindowBounds(hX, hY, Math.Max(10, hW), Math.Max(10, hH));
            var headerImg = ScreenCapture.CaptureRegion(headerBounds);
            if (headerImg is not null)
            {
                headerImg = headerImg.ScaleNearest(_ocrUpscaleFactor);
                var ocr = await _ocrEngine.RecognizeAsync(headerImg);
                foreach (var line in ocr.Lines)
                {
                    string text = CleanContactName(line.Text);
                    if (text.Length >= 2 && text != "微信" && text != "WeChat" && text != _mySelfNickname && !TraceMemoClient.IsSystemContact(null, text))
                    {
                        return text;
                    }
                }
            }
        }
        catch
        {
            // 忽略图像或 OCR 探测异常
        }

        return null;
    }

    private async void SyncContactButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSyncContact = true;
        SyncContactButton.IsEnabled = false;
        SetPersonaStatus("正在探测当前对话好友...", isBusy: true);

        try
        {
            string? detectedName = null;

            // 1. 优先尝试从微信窗口探测当前好友（独立窗口标题或主窗口顶部标题区 OCR）
            detectedName = await DetectContactFromWeChatHeaderAsync();

            // 2. 若窗口探测未成功，尝试从 TraceMemo 获取当前活跃单聊好友（已强力过滤服务通知与公众号）
            if (string.IsNullOrWhiteSpace(detectedName))
            {
                var settings = new SettingsStore().Load();
                if (!string.IsNullOrWhiteSpace(settings.TraceMemoBaseUrl))
                {
                    try
                    {
                        using var client = new TraceMemoClient(settings.TraceMemoBaseUrl);
                        detectedName = await client.GetActiveContactNameAsync();
                    }
                    catch
                    {
                        // 忽略网络或服务不可用异常
                    }
                }
            }

            // 3. 检查已记录的当前非本人联系人
            if (string.IsNullOrWhiteSpace(detectedName) && !string.IsNullOrEmpty(_currentChatContact) && _currentChatContact != _mySelfNickname && !TraceMemoClient.IsSystemContact(null, _currentChatContact))
            {
                detectedName = _currentChatContact;
            }

            if (!string.IsNullOrEmpty(detectedName) && detectedName != _mySelfNickname && !TraceMemoClient.IsSystemContact(null, detectedName))
            {
                _currentChatContact = detectedName;
                ContactBox.Text = detectedName;
                _history.Clear();
                _historyContactName = string.Empty;
                CheckAndLoadPersona(detectedName);
                UpdateAiTargetInfo();
                SetPersonaStatus($"✅ 已同步当前微信聊天对象：「{detectedName}」", isBusy: false);
            }
            else
            {
                SetPersonaStatus("未能自动识别到好友。可在上方直接输入好友昵称/备注，或在下方「已存档案」中直接选取。", isBusy: false);
            }
        }
        catch (Exception ex)
        {
            SetPersonaStatus("同步聊天对象失败：" + ex.Message, isBusy: false);
        }
        finally
        {
            SyncContactButton.IsEnabled = true;
        }
    }

    private void SavedPersonasCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingPersonaSelection)
        {
            return;
        }

        if (SavedPersonasCombo.SelectedItem is string name && !string.IsNullOrWhiteSpace(name))
        {
            _isSyncingPersonaSelection = true;
            try
            {
                ContactBox.Text = name;
                _history.Clear();
                _historyContactName = string.Empty;
            }
            finally
            {
                _isSyncingPersonaSelection = false;
            }

            CheckAndLoadPersona(name);
            UpdateAiTargetInfo();
        }
    }

    private void InitSkillCombo()
    {
        var allSkills = _skillStore.GetAllSkills();
        string activeId = _skillStore.GetActiveSkillId();
        _currentSkill = allSkills.FirstOrDefault(s => string.Equals(s.Id, activeId, StringComparison.OrdinalIgnoreCase))
            ?? allSkills.FirstOrDefault()
            ?? DistillSkillPresets.Intimacy;

        UpdateSkillBadge();
        RefreshSettingsSkillList();
    }

    private void UpdateSkillBadge()
    {
        if (_currentSkill == null) return;

        if (RadarSkillBadgeText != null)
        {
            RadarSkillBadgeText.Text = $"{_currentSkill.Icon} {_currentSkill.Name}";
            RadarSkillBadgeText.ToolTip = CreateSkillToolTip(_currentSkill);
        }
        if (AiAppliedSkillText != null)
        {
            AiAppliedSkillText.Text = $"{_currentSkill.Icon} {_currentSkill.Name}";
            AiAppliedSkillText.ToolTip = CreateSkillToolTip(_currentSkill);
        }
        if (AiTopSkillText != null)
        {
            AiTopSkillText.Text = $"{_currentSkill.Icon} {_currentSkill.Name}";
            AiTopSkillText.ToolTip = CreateSkillToolTip(_currentSkill);
        }
        if (SubtextStrategyTitle != null)
        {
            SubtextStrategyTitle.Text = $"⚡ {_currentSkill.Name} 实战破局策略与避坑指南";
        }
        if (_currentLoadedPersona == null && RadarCanvas != null && RadarCanvas.Children.Count > 0)
        {
            DrawRadarChart(null);
        }
    }

    /// <summary>
    /// 为认知/蒸馏技能生成现代卡片式高质感 ToolTip，包含名称、官方/扩展徽章、详细说明、核心评估维度与破局风格。
    /// </summary>
    private static ToolTip CreateSkillToolTip(DistillSkill skill)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            MaxWidth = 360
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderLightBrush");

        var sp = new StackPanel();

        // 1. 顶部标题栏：图标 + 名称 + 预设分类胶囊徽章
        var headerDock = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

        var tagBorder = new Border
        {
            Background = skill.IsBuiltIn
                ? new SolidColorBrush(Color.FromArgb(40, 59, 130, 246))
                : new SolidColorBrush(Color.FromArgb(40, 168, 85, 247)),
            BorderBrush = skill.IsBuiltIn
                ? new SolidColorBrush(Color.FromArgb(90, 59, 130, 246))
                : new SolidColorBrush(Color.FromArgb(90, 168, 85, 247)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(tagBorder, Dock.Right);

        tagBorder.Child = new TextBlock
        {
            Text = skill.IsBuiltIn ? "官方预设" : "工作区技能",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = skill.IsBuiltIn
                ? new SolidColorBrush(Color.FromRgb(96, 165, 250))
                : new SolidColorBrush(Color.FromRgb(192, 132, 252))
        };
        headerDock.Children.Add(tagBorder);

        var titleBlock = new TextBlock
        {
            Text = $"{skill.Icon} {skill.Name}",
            FontWeight = FontWeights.Bold,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        headerDock.Children.Add(titleBlock);
        sp.Children.Add(headerDock);

        // 分割线
        var divider = new Rectangle
        {
            Height = 1,
            Margin = new Thickness(0, 2, 0, 8)
        };
        divider.SetResourceReference(Shape.FillProperty, "BorderLightBrush");
        sp.Children.Add(divider);

        // 2. 技能详细定位与介绍说明
        string desc = !string.IsNullOrWhiteSpace(skill.Description) && skill.Description != skill.Name
            ? skill.Description.Trim()
            : "基于该技能设定的专属提示词与方法论，深度提炼目标联系人的聊天行为与心智模式。";

        var descBlock = new TextBlock
        {
            Text = desc,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16.5,
            Margin = new Thickness(0, 0, 0, 8)
        };
        descBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        sp.Children.Add(descBlock);

        // 3. 核心评估维度
        if (skill.Dimensions is { Count: > 0 })
        {
            var dimBlock = new TextBlock
            {
                Text = $"• 核心评估维度：{string.Join(" · ", skill.Dimensions)}",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 15,
                Margin = new Thickness(0, 0, 0, 4)
            };
            dimBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(dimBlock);
        }

        // 4. 实战破局风格 / 破局心法
        if (skill.SuggestedTones is { Count: > 0 })
        {
            var toneBlock = new TextBlock
            {
                Text = $"• 破局实战风格：{string.Join(" / ", skill.SuggestedTones)}",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            toneBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(toneBlock);
        }
        else if (!string.IsNullOrWhiteSpace(skill.AdviceGuideline))
        {
            string firstLine = skill.AdviceGuideline.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            var guideBlock = new TextBlock
            {
                Text = $"• 破局策略：{firstLine}",
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            guideBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            sp.Children.Add(guideBlock);
        }

        // 5. 底部操作提示
        var hintBlock = new TextBlock
        {
            Text = "💡 选择此技能后，一键蒸馏画像与实战建议将自动代入此方法论视角",
            FontSize = 9.5,
            Margin = new Thickness(0, 4, 0, 0)
        };
        hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        sp.Children.Add(hintBlock);

        border.Child = sp;

        return new ToolTip
        {
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HasDropShadow = true,
            Content = border
        };
    }

    private void SettingScanFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? selectedPath = null;
        try
        {
            var folderDlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择技能库根文件夹（包含各技能 SKILL.md 子目录）",
                Multiselect = false
            };
            if (folderDlg.ShowDialog() == true)
            {
                selectedPath = folderDlg.FolderName;
            }
        }
        catch
        {
            using var fbd = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择技能库根文件夹（包含各技能 SKILL.md 子目录）",
                UseDescriptionForTitle = true
            };
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                selectedPath = fbd.SelectedPath;
            }
        }

        if (string.IsNullOrWhiteSpace(selectedPath)) return;

        try
        {
            _skillStore.SetBoundSkillsDirectory(selectedPath);
            var imported = _skillStore.ScanAndImportFolder(selectedPath);
            MainTabControl.SelectedIndex = 3;
            SwitchSettingSubTab(1);
            InitSkillCombo();
            RefreshSettingsSkillList();

            if (imported.Count > 0)
            {
                string skillTitles = string.Join("\n", imported.Select(s => $"• {s.Icon} {s.Name} ({s.Id})"));
                MessageBox.Show($"✅ 成功绑定技能目录并导入 {imported.Count} 个技能：\n\n{skillTitles}\n\n所有技能已立即生效并加入技能库！", "绑定成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"✅ 已成功绑定技能目录：\n{selectedPath}\n\n该目录下目前未发现新增的有效 SKILL.md 文件。", "绑定提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("绑定技能目录失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SettingRescanFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string boundDir = _skillStore.GetBoundSkillsDirectory();
            if (Directory.Exists(boundDir))
            {
                _skillStore.ScanAndImportFolder(boundDir);
            }
            InitSkillCombo();
            RefreshSettingsSkillList();
            MessageBox.Show("✅ 已完成技能目录重新扫描与状态同步！", "刷新成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("重新扫描失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SettingOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string dir = _skillStore.GetBoundSkillsDirectory();
        if (!Directory.Exists(dir))
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开文件夹失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void FetchHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        string name = ContactBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetPersonaStatus("请先填写或同步联系人姓名（用于在 TraceMemo 中搜索历史）", isBusy: false);
            return;
        }

        var settings = new SettingsStore().Load();
        FetchHistoryButton.IsEnabled = false;
        FetchHistoryButton.Content = "⏳ 刷新中...";
        SetPersonaStatus($"正在连接 TraceMemo (6131) 并解析「{name}」历史记录...", isBusy: true);

        try
        {
            bool tmSuccess = false;
            if (!string.IsNullOrWhiteSpace(settings.TraceMemoBaseUrl))
            {
                try
                {
                    using var client = new TraceMemoClient(settings.TraceMemoBaseUrl);
                    var res = await client.FetchHistoryAsync(name);

                    if (res.Success)
                    {
                        tmSuccess = true;
                        _history.Clear();
                        _history.AddRange(res.Messages);
                        _historyContactName = name;

                        // 同步持久化至本地 SQLite 数据库
                        int newInserted = await Task.Run(() => _sqliteStorage.SaveMessages(name, res.Messages));
                        int totalLocal = await Task.Run(() => _sqliteStorage.GetMessageCount(name));

                        if (res.Messages.Count > 0)
                        {
                            SetPersonaStatus($"✅ 已成功从 TraceMemo 拉取「{name}」共 {res.Messages.Count} 条历史消息并存入本地 SQLite（本次入库 {newInserted} 条，本地累计 {totalLocal} 条）。可点击【✨ 一键蒸馏画像】！", isBusy: false);
                        }
                        else
                        {
                            SetPersonaStatus($"ℹ️ TraceMemo 中找到了联系人「{name}」，但该会话暂无聊天消息（本地累计 {totalLocal} 条）", isBusy: false);
                        }
                    }
                }
                catch
                {
                    tmSuccess = false;
                }
            }

            if (!tmSuccess)
            {
                // TraceMemo 未运行或离线：自动回退从本地 SQLite 数据库秒级加载
                var localMessages = await Task.Run(() => _sqliteStorage.GetMessages(name));
                if (localMessages.Count > 0)
                {
                    _history.Clear();
                    _history.AddRange(localMessages);
                    _historyContactName = name;
                    SetPersonaStatus($"⚠️ TraceMemo 未启动或连接失败，已自动从本地 SQLite 数据库秒级加载离线历史（共 {localMessages.Count} 条）。可直接点击【✨ 一键蒸馏画像】！", isBusy: false);
                }
                else
                {
                    SetPersonaStatus($"❌ TraceMemo 未启动且本地 SQLite 暂无「{name}」的历史记录。\n（请启动 TraceMemo 或使用「📥 导入 CSV」导入历史）", isBusy: false);
                }
            }
        }
        catch (Exception ex)
        {
            SetPersonaStatus("历史刷新异常：" + ex.Message, isBusy: false);
        }
        finally
        {
            FetchHistoryButton.IsEnabled = true;
            FetchHistoryButton.Content = "⚡ 刷新历史";
        }
    }

    private async void DistillButton_Click(object sender, RoutedEventArgs e)
    {
        string name = ContactBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetPersonaStatus("请先输入或同步联系人姓名。", isBusy: false);
            return;
        }

        // 1. 若当前历史为空，或联系人发生变动，自动直连 TraceMemo 读取最新记录，离线时自动回退本地 SQLite
        if (_history.Count == 0 || !string.Equals(_historyContactName, name, StringComparison.OrdinalIgnoreCase))
        {
            var settings = new SettingsStore().Load();
            SetPersonaStatus($"未检测到「{name}」缓存历史，正在拉取历史记录...", isBusy: true);

            if (!string.IsNullOrWhiteSpace(settings.TraceMemoBaseUrl))
            {
                try
                {
                    using var client = new TraceMemoClient(settings.TraceMemoBaseUrl);
                    var fetchRes = await client.FetchHistoryAsync(name);
                    if (fetchRes.Success && fetchRes.Messages.Count > 0)
                    {
                        _history.Clear();
                        _history.AddRange(fetchRes.Messages);
                        _historyContactName = name;
                        _ = Task.Run(() => _sqliteStorage.SaveMessages(name, fetchRes.Messages));
                    }
                }
                catch
                {
                    // 忽略自动拉取异常
                }
            }

            if (_history.Count == 0)
            {
                var cached = await Task.Run(() => _sqliteStorage.GetMessages(name));
                if (cached.Count > 0)
                {
                    _history.Clear();
                    _history.AddRange(cached);
                    _historyContactName = name;
                }
            }

            if (_history.Count == 0)
            {
                SetPersonaStatus($"历史记录为空：TraceMemo 未启动且本地数据库无「{name}」的记录。可点击【⚡ 刷新历史】重试或使用【📥 导入CSV】。", isBusy: false);
                return;
            }
        }

        DistillButton.IsEnabled = false;
        DistillButton.Content = "⏳ 蒸馏中...";
        SetPersonaStatus($"🧬 正在调用【{_currentSkill.Icon} {_currentSkill.Name}】蒸馏视角，分析 {name} 的 {_history.Count} 条历史...", isBusy: true);

        try
        {
            var settings = new SettingsStore().Load();
            string key = DpapiProtector.Unprotect(settings.EncryptedApiKey);
            int timeoutSec = Math.Max(settings.TimeoutSeconds, 300);
            using var provider = new OpenAiCompatibleProvider(settings.Endpoint, () => key, timeout: TimeSpan.FromSeconds(timeoutSec));

            Persona? existing = _personaStore.Load(name);
            var distillResult = await PersonaDistiller.DistillDetailedAsync(
                provider,
                settings,
                name,
                _history,
                existing,
                _mySelfNickname,
                _currentSkill,
                freshDistill: true);

            if (!distillResult.Success || distillResult.Persona.Traits.Count == 0)
            {
                string err = distillResult.ErrorMessage ?? "大模型未按约定格式输出，请重试或在设置中更换模型。";
                SetPersonaStatus($"❌ 蒸馏失败：{err}", isBusy: false);
                return;
            }

            Persona persona = distillResult.Persona;
            _personaStore.Save(persona);
            ShowPersona(persona);
            RefreshSavedPersonasCombo();
            SetPersonaStatus($"✅「{name}」画像蒸馏成功并已保存！（应用【{_currentSkill.Icon} {_currentSkill.Name}】，提炼 {persona.Traits.Count} 项特质，基于 {persona.SourceMessageCount} 条历史）", isBusy: false);
        }
        catch (Exception ex)
        {
            SetPersonaStatus("画像蒸馏异常：" + ex.Message, isBusy: false);
        }
        finally
        {
            DistillButton.IsEnabled = true;
            DistillButton.Content = "✨ 一键蒸馏画像";
        }
    }

    private void ImportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "聊天记录 CSV (*.csv)|*.csv|所有文件 (*.*)|*.*",
            Title = "导入微信历史记录 CSV"
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            string csv = File.ReadAllText(dlg.FileName, Encoding.UTF8);
            var messages = CsvHistoryImporter.Parse(csv);
            _history.Clear();
            _history.AddRange(messages);
            _historyContactName = ContactBox.Text.Trim();
            if (!string.IsNullOrEmpty(_historyContactName) && messages.Count > 0)
            {
                _ = Task.Run(() => _sqliteStorage.SaveMessages(_historyContactName, messages));
            }
            SetPersonaStatus($"✅ 成功导入 CSV 历史 {messages.Count} 条并存入本地 SQLite，可点击【✨ 一键蒸馏画像】。", isBusy: false);
        }
        catch (Exception ex)
        {
            SetPersonaStatus("导入 CSV 失败：" + ex.Message, isBusy: false);
        }
    }

    private void DeletePersonaButton_Click(object sender, RoutedEventArgs e)
    {
        string name = ContactBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        _personaStore.Delete(name);

        _currentLoadedPersona = null;
        PersonaScroll.Visibility = Visibility.Collapsed;
        PersonaEmptyCard.Visibility = Visibility.Visible;
        PersonaEmptyTitle.Text = $"已清除「{name}」的画像";
        RefreshSavedPersonasCombo();
        SetPersonaStatus($"已删除「{name}」的本地画像文件与数据库记录。", isBusy: false);
    }

    private void CheckAndLoadPersona(string name)
    {
        try
        {
            Persona? p = _personaStore.Load(name);
            if (p is not null && p.Traits.Count > 0)
            {
                ShowPersona(p);
            }
            else
            {
                _currentLoadedPersona = null;
                PersonaScroll.Visibility = Visibility.Collapsed;
                PersonaEmptyCard.Visibility = Visibility.Visible;
                PersonaEmptyTitle.Text = $"暂无「{name}」的画像";
                RadarCanvas.Children.Clear();
            }
        }
        catch (Exception ex)
        {
            _currentLoadedPersona = null;
            PersonaScroll.Visibility = Visibility.Collapsed;
            PersonaEmptyCard.Visibility = Visibility.Visible;
            PersonaEmptyTitle.Text = $"读取「{name}」画像失败：{ex.Message}";
            RadarCanvas.Children.Clear();
        }
    }

    private void ShowPersona(Persona p)
    {
        _currentLoadedPersona = p;
        PersonaEmptyCard.Visibility = Visibility.Collapsed;
        PersonaScroll.Visibility = Visibility.Visible;

        PersonaHeaderName.Text = $"👤 {p.ContactName} 的人格特质画像";
        PersonaHeaderMeta.Text = $"更新时间: {p.UpdatedAt:yyyy-MM-dd HH:mm}  |  基于历史: {p.SourceMessageCount} 条消息";

        if (!string.IsNullOrWhiteSpace(p.UltimateGoal))
        {
            PersonaHeaderGoalBadge.Visibility = Visibility.Visible;
            PersonaHeaderGoalText.Text = $"🚩 最终战略目的: {p.UltimateGoal}";
            PersonaHeaderGoalBadge.ToolTip = $"已为该好友设定最终战略目的：{p.UltimateGoal}\n（可在【设置 -> 画像目的】中修改）";
        }
        else
        {
            PersonaHeaderGoalBadge.Visibility = Visibility.Collapsed;
        }

        // 彻底归一化去重并过滤己方原话
        var cleanTraits = PersonaDistiller.DeduplicateByDimension(p.Traits, _history);
        PersonaCards.ItemsSource = cleanTraits;

        // 呈现该画像生成时选用的技能，但不篡改用户当前活动蒸馏技能设置
        DistillSkill displaySkill = _currentSkill;
        if (!string.IsNullOrWhiteSpace(p.SkillId))
        {
            var matchedSkill = _skillStore.GetSkill(p.SkillId);
            if (matchedSkill != null)
            {
                displaySkill = matchedSkill;
            }
        }

        DrawRadarChart(cleanTraits, displaySkill);
        UpdatePersonaSkillBadge(displaySkill);
    }

    private void UpdatePersonaSkillBadge(DistillSkill displaySkill)
    {
        if (RadarSkillBadgeText != null)
        {
            RadarSkillBadgeText.Text = $"{displaySkill.Icon} {displaySkill.Name}";
            RadarSkillBadgeText.ToolTip = CreateSkillToolTip(displaySkill);
        }
    }

    private void OpenPersonaAnalyticsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenPersonaAnalyticsDashboard();
    }

    private void RadarCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        OpenPersonaAnalyticsDashboard();
    }

    private void OpenPersonaAnalyticsDashboard()
    {
        if (_currentLoadedPersona == null || _currentLoadedPersona.Traits.Count == 0)
        {
            MessageBox.Show("当前尚未加载或生成联系人画像数据。\n请先选择联系人并点击【✨ 一键蒸馏画像】。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var skill = !string.IsNullOrWhiteSpace(_currentLoadedPersona.SkillId)
            ? _skillStore.GetSkill(_currentLoadedPersona.SkillId)
            : _currentSkill;

        var win = new PersonaAnalyticsWindow(_currentLoadedPersona, skill, _history)
        {
            Owner = this
        };
        try
        {
            var s = _settingsStore.Load();
            ThemeManager.ApplyTheme(win, s.ThemeMode ?? "Dark");
        }
        catch
        {
        }
        win.Show();
    }

    private void OpenAdviceAnalyticsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenAdviceAnalyticsDashboard();
    }

    private void OpenAdviceAnalyticsDashboard()
    {
        if (_lastUnifiedAdvice == null)
        {
            MessageBox.Show("当前尚未生成建议数据。\n请先选择联系人并点击【⚡ 智能分析回复建议】生成战局研判与回复方案。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var skill = _lastAdviceSkill ?? _currentSkill;
        var persona = _currentLoadedPersona;
        string contact = !string.IsNullOrWhiteSpace(_lastAdviceContactName)
            ? _lastAdviceContactName
            : (persona?.ContactName ?? (_currentChatContact.Length > 0 ? _currentChatContact : "当前好友"));
        string goal = !string.IsNullOrWhiteSpace(_lastAdviceGoal)
            ? _lastAdviceGoal
            : (_currentLoadedPersona?.UltimateGoal ?? (AiGoalBadge?.Visibility == Visibility.Visible ? AiGoalBadgeText?.Text : null) ?? "推进深度交流与共识达成");
        string intent = !string.IsNullOrWhiteSpace(_lastAdviceIntent)
            ? _lastAdviceIntent
            : (AiIntentBox?.Text?.Trim() ?? string.Empty);

        var win = new AdviceAnalyticsWindow(_lastUnifiedAdvice, persona, skill, contact, goal, intent)
        {
            Owner = this
        };

        try
        {
            var s = _settingsStore.Load();
            ThemeManager.ApplyTheme(win, s.ThemeMode ?? "Dark");
        }
        catch
        {
        }

        win.Show();
    }

    private void RadarToggleDimButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllRadarDimensions = !_showAllRadarDimensions;
        if (_currentLoadedPersona != null)
        {
            var cleanTraits = PersonaDistiller.DeduplicateByDimension(_currentLoadedPersona.Traits, _history);
            DistillSkill displaySkill = !string.IsNullOrWhiteSpace(_currentLoadedPersona.SkillId)
                ? _skillStore.GetSkill(_currentLoadedPersona.SkillId)
                : _currentSkill;
            DrawRadarChart(cleanTraits, displaySkill);
        }
    }

    private void DrawRadarChart(IReadOnlyList<PersonaTrait>? traits, DistillSkill? skill = null)
    {
        RadarCanvas.Children.Clear();
        skill ??= _currentSkill ?? DistillSkillPresets.Intimacy;

        double cx = RadarCanvas.Width / 2.0;
        double cy = RadarCanvas.Height / 2.0;
        double maxR = 60.0;

        var data = new List<(string Label, double Score)>();
        if (traits is { Count: > 0 })
        {
            IReadOnlyList<PersonaTrait> displayTraits;
            if (_showAllRadarDimensions || traits.Count <= 8)
            {
                displayTraits = traits;
            }
            else
            {
                // 若超过 8 维且未开全量，在小窗内优先挑选代表性的前 8 维；点击【🔍 放大看板】可查看全景
                var selected = new List<PersonaTrait>();
                if (skill.Dimensions is { Count: > 0 } skillDims)
                {
                    foreach (string d in skillDims)
                    {
                        var match = traits.FirstOrDefault(t =>
                            string.Equals(t.Dimension, d, StringComparison.OrdinalIgnoreCase) ||
                            t.Dimension.Contains(d, StringComparison.OrdinalIgnoreCase) ||
                            d.Contains(t.Dimension, StringComparison.OrdinalIgnoreCase));
                        if (match != null && !selected.Contains(match))
                        {
                            selected.Add(match);
                            if (selected.Count >= 8) break;
                        }
                    }
                }

                if (selected.Count < 8)
                {
                    var remaining = traits
                        .Except(selected)
                        .OrderByDescending(t => (t.Confidence * 100) + Math.Abs(t.Score - 50) + (t.Evidence.Count * 10))
                        .Take(8 - selected.Count);
                    selected.AddRange(remaining);
                }

                displayTraits = selected.Count >= 3 ? selected : traits.Take(8).ToList();
            }

            foreach (var t in displayTraits)
            {
                string label = t.Dimension;
                if (label.Length > 6)
                {
                    int mid = (label.Length + 1) / 2;
                    label = label[..mid] + "\n" + label[mid..];
                }
                data.Add((label, Math.Clamp(t.Score, 10, 100)));
            }

            // 更新切换按钮与维度数量说明
            if (RadarToggleDimButton != null)
            {
                if (traits.Count > 8)
                {
                    RadarToggleDimButton.Visibility = Visibility.Visible;
                    RadarToggleDimButton.Content = _showAllRadarDimensions ? "🎯 精选视角" : $"🌐 全部({traits.Count})";
                    RadarToggleDimButton.ToolTip = _showAllRadarDimensions
                        ? "当前显示全量特质，点击切换为精简视角；也可点击【🔍 放大看板】查看大图"
                        : $"当前精简显示前 8 维，点击展开全部 {traits.Count} 项；也可点击【🔍 放大看板】";
                }
                else
                {
                    RadarToggleDimButton.Visibility = Visibility.Collapsed;
                }
            }

            if (RadarDimensionCountText != null)
            {
                RadarDimensionCountText.Text = traits.Count > 8 && !_showAllRadarDimensions
                    ? $"(8维精选 / 共{traits.Count}维)"
                    : $"({traits.Count}维)";
            }
        }
        else
        {
            if (RadarToggleDimButton != null) RadarToggleDimButton.Visibility = Visibility.Collapsed;
            if (RadarDimensionCountText != null) RadarDimensionCountText.Text = string.Empty;

            // 无画像数据时，使用当前激活 Skill 的预设维度展示中性底图 (50分基准)
            var defaultDims = skill.Dimensions is { Count: >= 3 }
                ? skill.Dimensions
                : new[] { "沟通风格", "性格能量", "决策模式", "情绪阈值", "价值锚点", "隐形雷区" };

            foreach (var d in defaultDims)
            {
                string label = d;
                if (label.Length > 6)
                {
                    int mid = (label.Length + 1) / 2;
                    label = label[..mid] + "\n" + label[mid..];
                }
                data.Add((label, 50.0));
            }
        }

        int count = data.Count;
        if (count < 3)
        {
            return;
        }
        double angleStep = 2 * Math.PI / count;
        double startAngle = -Math.PI / 2.0;

        // 1. 同心网格层 (4圈: 25%, 50%, 75%, 100%)
        for (int level = 1; level <= 4; level++)
        {
            double rLevel = maxR * (level / 4.0);
            var gridPoints = new PointCollection();
            for (int i = 0; i < count; i++)
            {
                double angle = startAngle + i * angleStep;
                gridPoints.Add(new Point(cx + rLevel * Math.Cos(angle), cy + rLevel * Math.Sin(angle)));
            }

            var gridPoly = new Polygon
            {
                Points = gridPoints,
                Stroke = new SolidColorBrush(Color.FromArgb(level == 4 ? (byte)90 : (byte)40, 148, 163, 184)),
                StrokeThickness = 1,
                StrokeDashArray = level == 4 ? null : new DoubleCollection { 2, 2 }
            };
            RadarCanvas.Children.Add(gridPoly);
        }

        // 2. 轴线层
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + i * angleStep;
            var axis = new Line
            {
                X1 = cx,
                Y1 = cy,
                X2 = cx + maxR * Math.Cos(angle),
                Y2 = cy + maxR * Math.Sin(angle),
                Stroke = new SolidColorBrush(Color.FromArgb(45, 148, 163, 184)),
                StrokeThickness = 1
            };
            RadarCanvas.Children.Add(axis);
        }

        // 3. 数据发光多边形层
        var dataPoints = new PointCollection();
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + i * angleStep;
            double rVal = maxR * (Math.Clamp(data[i].Score, 10, 100) / 100.0);
            dataPoints.Add(new Point(cx + rVal * Math.Cos(angle), cy + rVal * Math.Sin(angle)));
        }

        var dataPoly = new Polygon
        {
            Points = dataPoints,
            Stroke = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(traits is { Count: > 0 } ? (byte)55 : (byte)20, 56, 189, 248))
        };
        RadarCanvas.Children.Add(dataPoly);

        // 4. 数据点高光圆点
        if (traits is { Count: > 0 })
        {
            foreach (Point p in dataPoints)
            {
                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5
                };
                Canvas.SetLeft(dot, p.X - 3);
                Canvas.SetTop(dot, p.Y - 3);
                RadarCanvas.Children.Add(dot);
            }
        }

        // 5. 外周维度标签与评分 (居中对齐，杜绝重叠)
        double labelR = maxR + 18;
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + i * angleStep;
            double lx = cx + labelR * Math.Cos(angle);
            double ly = cy + labelR * Math.Sin(angle);

            string scoreText = traits is { Count: > 0 } ? $"{(int)data[i].Score}分" : "待分析";
            var tb = new TextBlock
            {
                Text = $"{data[i].Label}\n{scoreText}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                TextAlignment = TextAlignment.Center,
                Width = 70
            };

            Canvas.SetLeft(tb, lx - 35);
            Canvas.SetTop(tb, ly - 13);
            RadarCanvas.Children.Add(tb);
        }
    }

    private void SetPersonaStatus(string text, bool isBusy)
    {
        PersonaStatusText.Text = text;
        PersonaProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        PersonaSpinner.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (text.StartsWith("❌") || text.Contains("失败") || text.Contains("异常"))
        {
            PersonaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            PersonaStatusCard.Background = new SolidColorBrush(Color.FromArgb(36, 239, 68, 68));
            PersonaStatusCard.BorderBrush = new SolidColorBrush(Color.FromArgb(85, 239, 68, 68));
        }
        else if (text.StartsWith("✅") || text.Contains("成功"))
        {
            PersonaStatusText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            PersonaStatusCard.Background = new SolidColorBrush(Color.FromArgb(30, 16, 185, 129));
            PersonaStatusCard.BorderBrush = new SolidColorBrush(Color.FromArgb(70, 16, 185, 129));
        }
        else if (isBusy)
        {
            PersonaStatusText.Foreground = (Brush)FindResource("TextPrimaryBrush");
            PersonaStatusCard.Background = new SolidColorBrush(Color.FromArgb(34, 59, 130, 246));
            PersonaStatusCard.BorderBrush = new SolidColorBrush(Color.FromArgb(85, 59, 130, 246));
        }
        else
        {
            PersonaStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            PersonaStatusCard.Background = (Brush)FindResource("CardSubtleBgBrush");
            PersonaStatusCard.BorderBrush = (Brush)FindResource("BorderSubtleBrush");
        }
    }

    private void RefreshSavedPersonasCombo()
    {
        var list = _personaStore.List();
        SavedPersonasCombo.ItemsSource = list;
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
                _currentChatContact = contact;
                StatusText.Text = $"已连接：{contact}";
                StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");

                if (_autoSyncContact)
                {
                    ContactBox.Text = contact;
                    CheckAndLoadPersona(contact);
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

    // ===================== VIEW 4: 设置 TAB 业务逻辑 =====================

    private void LoadSettingsView()
    {
        var s = _settingsStore.Load();
        SettingEndpointBox.Text = s.Endpoint;
        SettingModelBox.Text = s.Model;
        SettingTempBox.Text = s.Temperature.ToString(CultureInfo.InvariantCulture);
        SettingTimeoutBox.Text = s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        SettingKeyBox.Password = DpapiProtector.Unprotect(s.EncryptedApiKey);
        SettingTraceMemoBox.Text = s.TraceMemoBaseUrl;

        SettingSelfNicknameBox.Text = _mySelfNickname ?? string.Empty;

        // 外观主题
        string currentTheme = s.ThemeMode ?? "Dark";
        if (SettingThemeCombo != null)
        {
            foreach (ComboBoxItem item in SettingThemeCombo.Items)
            {
                if (string.Equals(item.Tag as string, currentTheme, StringComparison.OrdinalIgnoreCase))
                {
                    SettingThemeCombo.SelectedItem = item;
                    break;
                }
            }
        }

        // 对话语境分析跨度
        int currentContextCount = s.ContextMessageCount > 0 ? s.ContextMessageCount : 10;
        if (SettingContextCountCombo != null)
        {
            foreach (ComboBoxItem item in SettingContextCountCombo.Items)
            {
                if (item.Tag is string tagStr && int.TryParse(tagStr, out int c) && c == currentContextCount)
                {
                    SettingContextCountCombo.SelectedItem = item;
                    break;
                }
            }
        }

        // 窗口透明度
        double currentOpacity = this.Opacity;
        foreach (ComboBoxItem item in SettingOpacityCombo.Items)
        {
            if (item.Tag is string tagStr && double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                if (Math.Abs(val - currentOpacity) < 0.02)
                {
                    SettingOpacityCombo.SelectedItem = item;
                    break;
                }
            }
        }

        // OCR 放大倍率
        foreach (ComboBoxItem item in SettingOcrScaleCombo.Items)
        {
            if (item.Tag is string tagStr && double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                if (Math.Abs(val - _ocrUpscaleFactor) < 0.5)
                {
                    SettingOcrScaleCombo.SelectedItem = item;
                    break;
                }
            }
        }

        RefreshSettingsSkillList();
    }

    private void SettingThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingThemeCombo?.SelectedItem is ComboBoxItem item && item.Tag is string theme)
        {
            var s = _settingsStore.Load();
            if (!string.Equals(s.ThemeMode, theme, StringComparison.OrdinalIgnoreCase))
            {
                s.ThemeMode = theme;
                _settingsStore.Save(s);
            }
            ThemeManager.ApplyTheme(this, theme);
        }
    }

    private void SettingContextCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingContextCountCombo?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr && int.TryParse(tagStr, out int count) && count > 0)
        {
            var s = _settingsStore.Load();
            if (s.ContextMessageCount != count)
            {
                s.ContextMessageCount = count;
                _settingsStore.Save(s);
            }

            if (AiContextCountCombo != null)
            {
                foreach (ComboBoxItem aiItem in AiContextCountCombo.Items)
                {
                    if (aiItem.Tag is string aiTag && int.TryParse(aiTag, out int c) && c == count)
                    {
                        AiContextCountCombo.SelectedItem = aiItem;
                        break;
                    }
                }
            }
        }
    }

    private void SettingNavigateGoalBtn_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabControl != null)
        {
            MainTabControl.SelectedIndex = 3;
        }
        SwitchSettingSubTab(2);
    }

    private void SettingSubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radio && radio.Tag is string tagStr && int.TryParse(tagStr, out int idx))
        {
            SwitchSettingSubTab(idx);
        }
    }

    private void SwitchSettingSubTab(int index)
    {
        if (SettingViewAi == null || SettingViewSkill == null || SettingViewGoals == null || SettingViewPref == null || SettingViewHelp == null)
        {
            return;
        }

        SettingViewAi.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewSkill.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewGoals.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewPref.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewHelp.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 0 && SettingSubTabAi != null) SettingSubTabAi.IsChecked = true;
        else if (index == 1 && SettingSubTabSkill != null) SettingSubTabSkill.IsChecked = true;
        else if (index == 2 && SettingSubTabGoals != null)
        {
            SettingSubTabGoals.IsChecked = true;
            RefreshSettingsPersonaGoalsList();
        }
        else if (index == 3 && SettingSubTabPref != null) SettingSubTabPref.IsChecked = true;
        else if (index == 4 && SettingSubTabHelp != null) SettingSubTabHelp.IsChecked = true;
    }

    private void RefreshSettingsPersonaGoalsList()
    {
        if (SavedPersonasGoalsList == null || NoSavedPersonasPlaceholder == null) return;

        var allPersonas = _personaStore.GetAllPersonas();
        if (allPersonas.Count == 0)
        {
            NoSavedPersonasPlaceholder.Visibility = Visibility.Visible;
            SavedPersonasGoalsList.ItemsSource = null;
            return;
        }

        NoSavedPersonasPlaceholder.Visibility = Visibility.Collapsed;
        var viewModels = allPersonas.Select(p => new PersonaGoalItemViewModel
        {
            ContactName = p.ContactName,
            TraitCountText = $"{p.Traits.Count}项心智特质",
            UpdatedTimeText = $"⏱ {p.UpdatedAt:yyyy-MM-dd HH:mm}",
            UltimateGoal = p.UltimateGoal ?? string.Empty
        }).ToList();

        SavedPersonasGoalsList.ItemsSource = viewModels;
    }

    private void SavePersonaGoal_Click(object sender, RoutedEventArgs e)
    {
        string contactName = string.Empty;
        string goal = string.Empty;

        if (sender is Button btn)
        {
            if (btn.Tag is PersonaGoalItemViewModel vm)
            {
                contactName = vm.ContactName;
                goal = vm.UltimateGoal;
            }
            else if (btn.DataContext is PersonaGoalItemViewModel dvm)
            {
                contactName = dvm.ContactName;
                goal = dvm.UltimateGoal;
            }

            if (string.IsNullOrWhiteSpace(goal) && btn.Parent is Grid grid)
            {
                var tb = grid.Children.OfType<TextBox>().FirstOrDefault();
                if (tb != null) goal = tb.Text;
            }
        }

        if (string.IsNullOrWhiteSpace(contactName)) return;

        bool updated = _personaStore.UpdateUltimateGoal(contactName, goal);
        if (updated)
        {
            MessageBox.Show($"已成功保存「{contactName}」的最终战略目的：\n{(string.IsNullOrWhiteSpace(goal) ? "（已清空目的）" : goal)}\n\n在「建议」中生成回复时，AI 将全程以此目的为最高导向！", "目的已保存", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateAiTargetInfo();

            if (_currentLoadedPersona != null && _currentLoadedPersona.ContactName == contactName)
            {
                _currentLoadedPersona = _personaStore.Load(contactName);
                if (_currentLoadedPersona != null) ShowPersona(_currentLoadedPersona);
            }
        }
    }

    private void RefreshSavedGoalsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSettingsPersonaGoalsList();
    }

    private void RefreshSettingsSkillList()
    {
        if (SettingSkillList == null) return;

        _isSyncingSkillSelection = true;
        try
        {
            var allSkills = _skillStore.GetAllSkills();
            string activeId = _skillStore.GetActiveSkillId();

            if (SettingActiveSkillCombo != null) SettingActiveSkillCombo.Items.Clear();
            if (PersonaActiveSkillCombo != null) PersonaActiveSkillCombo.Items.Clear();

            ComboBoxItem? selectedSettingCombo = null;
            ComboBoxItem? selectedPersonaCombo = null;

            var items = new List<SettingSkillItem>();
            foreach (var skill in allSkills)
            {
                if (SettingActiveSkillCombo != null)
                {
                    var cItem = new ComboBoxItem
                    {
                        Content = $"{skill.Icon} {skill.Name}",
                        Tag = skill,
                        ToolTip = CreateSkillToolTip(skill)
                    };
                    ToolTipService.SetInitialShowDelay(cItem, 100);
                    ToolTipService.SetBetweenShowDelay(cItem, 50);
                    ToolTipService.SetShowDuration(cItem, 20000);
                    SettingActiveSkillCombo.Items.Add(cItem);
                    if (string.Equals(skill.Id, activeId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedSettingCombo = cItem;
                    }
                }

                if (PersonaActiveSkillCombo != null)
                {
                    var pItem = new ComboBoxItem
                    {
                        Content = $"{skill.Icon} {skill.Name}",
                        Tag = skill,
                        ToolTip = CreateSkillToolTip(skill)
                    };
                    ToolTipService.SetInitialShowDelay(pItem, 100);
                    ToolTipService.SetBetweenShowDelay(pItem, 50);
                    ToolTipService.SetShowDuration(pItem, 20000);
                    PersonaActiveSkillCombo.Items.Add(pItem);
                    if (string.Equals(skill.Id, activeId, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPersonaCombo = pItem;
                    }
                }

                items.Add(new SettingSkillItem(
                    Id: skill.Id,
                    DisplayTitle: $"{skill.Icon} {skill.Name}",
                    Description: skill.Description,
                    TagText: skill.IsBuiltIn ? "官方预设" : "自定义Skill",
                    DeleteVisibility: skill.IsBuiltIn ? Visibility.Collapsed : Visibility.Visible
                ));
            }

            if (selectedSettingCombo != null && SettingActiveSkillCombo != null)
            {
                SettingActiveSkillCombo.SelectedItem = selectedSettingCombo;
                _currentSkill = (DistillSkill)selectedSettingCombo.Tag;
                SettingActiveSkillCombo.ToolTip = CreateSkillToolTip(_currentSkill);
            }
            else if (SettingActiveSkillCombo is { Items.Count: > 0 })
            {
                SettingActiveSkillCombo.SelectedIndex = 0;
                _currentSkill = (DistillSkill)((ComboBoxItem)SettingActiveSkillCombo.Items[0]).Tag;
                SettingActiveSkillCombo.ToolTip = CreateSkillToolTip(_currentSkill);
            }

            if (selectedPersonaCombo != null && PersonaActiveSkillCombo != null)
            {
                PersonaActiveSkillCombo.SelectedItem = selectedPersonaCombo;
                PersonaActiveSkillCombo.ToolTip = CreateSkillToolTip((DistillSkill)selectedPersonaCombo.Tag);
            }
            else if (PersonaActiveSkillCombo is { Items.Count: > 0 })
            {
                PersonaActiveSkillCombo.SelectedIndex = 0;
                PersonaActiveSkillCombo.ToolTip = CreateSkillToolTip((DistillSkill)((ComboBoxItem)PersonaActiveSkillCombo.Items[0]).Tag);
            }

            SettingSkillList.ItemsSource = items;
            if (SettingBoundPathText != null)
            {
                SettingBoundPathText.Text = _skillStore.GetBoundSkillsDirectory();
            }
            UpdateSkillBadge();
        }
        finally
        {
            _isSyncingSkillSelection = false;
        }
    }

    private void PersonaActiveSkillCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSkillSelection) return;

        if (PersonaActiveSkillCombo?.SelectedItem is ComboBoxItem item && item.Tag is DistillSkill skill)
        {
            _isSyncingSkillSelection = true;
            try
            {
                _skillStore.SetActiveSkillId(skill.Id);
                _currentSkill = skill;
                PersonaActiveSkillCombo.ToolTip = CreateSkillToolTip(skill);

                if (SettingActiveSkillCombo != null)
                {
                    foreach (ComboBoxItem sItem in SettingActiveSkillCombo.Items)
                    {
                        if (sItem.Tag is DistillSkill s && string.Equals(s.Id, skill.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            SettingActiveSkillCombo.SelectedItem = sItem;
                            SettingActiveSkillCombo.ToolTip = CreateSkillToolTip(skill);
                            break;
                        }
                    }
                }

                UpdateSkillBadge();
                if (_currentLoadedPersona == null)
                {
                    DrawRadarChart(null, _currentSkill);
                }
            }
            finally
            {
                _isSyncingSkillSelection = false;
            }
        }
    }

    private void SettingActiveSkillCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingSkillSelection) return;

        if (SettingActiveSkillCombo?.SelectedItem is ComboBoxItem item && item.Tag is DistillSkill skill)
        {
            _isSyncingSkillSelection = true;
            try
            {
                _skillStore.SetActiveSkillId(skill.Id);
                _currentSkill = skill;
                SettingActiveSkillCombo.ToolTip = CreateSkillToolTip(skill);

                if (PersonaActiveSkillCombo != null)
                {
                    foreach (ComboBoxItem pItem in PersonaActiveSkillCombo.Items)
                    {
                        if (pItem.Tag is DistillSkill s && string.Equals(s.Id, skill.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            PersonaActiveSkillCombo.SelectedItem = pItem;
                            PersonaActiveSkillCombo.ToolTip = CreateSkillToolTip(skill);
                            break;
                        }
                    }
                }

                UpdateSkillBadge();
                if (_currentLoadedPersona == null)
                {
                    DrawRadarChart(null, _currentSkill);
                }
            }
            finally
            {
                _isSyncingSkillSelection = false;
            }
        }
    }

    private async void SettingTestAiButton_Click(object sender, RoutedEventArgs e)
    {
        string endpoint = SettingEndpointBox.Text.Trim();
        string model = SettingModelBox.Text.Trim();
        string key = SettingKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key))
        {
            key = DpapiProtector.Unprotect(_settingsStore.Load().EncryptedApiKey);
        }

        double temp = double.TryParse(SettingTempBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
            ? t
            : 0.7;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            ShowSettingTestResult(false, "缺少接口基址", "请先输入接口基址（例如 https://api.openai.com/v1）。", 0);
            return;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            ShowSettingTestResult(false, "缺少模型名", "请先输入模型名称（例如 gpt-4o、deepseek-chat 等）。", 0);
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            ShowSettingTestResult(false, "缺少 API Key", "请先输入 API Key。", 0);
            return;
        }

        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            ShowSettingTestResult(false, "接口地址格式错误", "接口基址必须以 http:// 或 https:// 开头。", 0);
            return;
        }

        SettingTestAiButton.IsEnabled = false;
        SettingTestAiButton.Content = "⏳ 测试中...";
        ShowSettingTestingStatus(endpoint, model);

        _settingTestCts?.Cancel();
        _settingTestCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = _settingTestCts.Token;

        var sw = Stopwatch.StartNew();
        try
        {
            using var provider = new OpenAiCompatibleProvider(endpoint, () => key);
            var req = new AiRequest(
                SystemPrompt: "你是一个智能测试助手。请只回复一句话：“连接成功，AI接口与模型正常运作中！”。不要有多余的文字。",
                UserPrompt: "测试当前 AI 接口是否可用，请回复确认信息。",
                Model: model,
                Temperature: temp,
                MaxTokens: 80);

            AiReply reply = await provider.CompleteAsync(req, token);
            sw.Stop();

            if (reply.Success)
            {
                string text = reply.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    ShowSettingTestResult(false, "模型返回内容为空",
                        "接口返回成功状态码，但模型回复内容为空。\n可能原因：该模型不支持当前请求格式，或中转服务未正确透传模型回复。",
                        sw.ElapsedMilliseconds);
                }
                else
                {
                    ShowSettingTestResult(true, "测试成功！AI 正常响应",
                        $"模型回复：\n{text}",
                        sw.ElapsedMilliseconds);
                }
            }
            else
            {
                string diagnosis = SettingsWindowDiagnoseError(reply.Error ?? "未知错误", endpoint, model);
                ShowSettingTestResult(false, "测试失败：无法获取 AI 响应",
                    $"{reply.Error}\n\n{diagnosis}",
                    sw.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            ShowSettingTestResult(false, "请求超时",
                $"在 25 秒内未收到服务商响应。\n💡 排查建议：\n1. 请检查网络连接或系统代理是否正常。\n2. 检查接口域名是否被防火墙阻拦，或服务商服务器响应缓慢。",
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            string diagnosis = SettingsWindowDiagnoseError(ex.Message, endpoint, model);
            ShowSettingTestResult(false, "连接发生异常",
                $"{ex.Message}\n\n{diagnosis}",
                sw.ElapsedMilliseconds);
        }
        finally
        {
            SettingTestAiButton.IsEnabled = true;
            SettingTestAiButton.Content = "⚡ 测试 AI 响应";
        }
    }

    private void ShowSettingTestingStatus(string endpoint, string model)
    {
        SettingTestResultBorder.Visibility = Visibility.Visible;
        SettingTestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        SettingTestStatusIcon.Text = "⏳";
        SettingTestStatusTitle.Text = "正在请求 AI 接口...";
        SettingTestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        SettingTestLatencyText.Text = string.Empty;
        SettingTestDetailBox.Text = $"正在向 [{endpoint}] 发送测试请求，模型：[{model}]，请稍候...";
    }

    private void ShowSettingTestResult(bool success, string title, string detail, long elapsedMs)
    {
        SettingTestResultBorder.Visibility = Visibility.Visible;
        if (success)
        {
            SettingTestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            SettingTestStatusIcon.Text = "✅";
            SettingTestStatusTitle.Text = title;
            SettingTestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            SettingTestLatencyText.Text = elapsedMs > 0 ? $"(耗时 {elapsedMs} ms)" : string.Empty;
        }
        else
        {
            SettingTestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            SettingTestStatusIcon.Text = "❌";
            SettingTestStatusTitle.Text = title;
            SettingTestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            SettingTestLatencyText.Text = elapsedMs > 0 ? $"(耗时 {elapsedMs} ms)" : string.Empty;
        }

        SettingTestDetailBox.Text = detail;
    }

    private static string SettingsWindowDiagnoseError(string error, string endpoint, string model)
    {
        var sb = new StringBuilder("💡 诊断与排查建议：\n");

        if (error.Contains("401") || error.Contains("Unauthorized"))
        {
            sb.AppendLine("• 身份验证失败 (401)：API Key 错误、已过期或余额受限，请确认 Key 是否复制完整。");
        }
        else if (error.Contains("404") || error.Contains("Not Found"))
        {
            sb.AppendLine("• 路径或模型未找到 (404)：");
            if (!endpoint.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  - 当前接口基址为「{endpoint}」，未以 /v1 结尾。OpenAI 兼容接口通常需以 /v1 结尾（例如 {endpoint.TrimEnd('/')}/v1）。");
            }
            sb.AppendLine($"  - 服务商可能不支持当前模型名「{model}」，请到服务商官网确认模型名拼写。");
        }
        else if (error.Contains("429"))
        {
            sb.AppendLine("• 请求超限或欠费 (429)：API 额度已用尽、账户欠费，或触发了服务商的请求频率限制。");
        }
        else if (error.Contains("400") || error.Contains("Bad Request"))
        {
            sb.AppendLine($"• 请求无效 (400)：模型名「{model}」可能不被支持或请求参数与此模型不兼容。");
        }
        else if (error.Contains("refused") || error.Contains("No such host") || error.Contains("积极拒绝") || error.Contains("远程主机强迫关闭"))
        {
            sb.AppendLine("• 无法建立网络连接：请检查接口网址是否正确，以及本机网络或代理（VPN）设置。");
        }
        else
        {
            sb.AppendLine("• 请检查接口地址、模型名以及 API Key 是否与服务商提供的信息一致。");
        }

        return sb.ToString().TrimEnd();
    }

    private void SettingSaveAiButton_Click(object sender, RoutedEventArgs e)
    {
        _settingTestCts?.Cancel();

        double temp = double.TryParse(SettingTempBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
            ? t
            : 0.7;

        int timeoutSec = int.TryParse(SettingTimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tSec) && tSec > 0
            ? tSec
            : 180;

        var existing = _settingsStore.Load();
        string encrypted = string.IsNullOrEmpty(SettingKeyBox.Password)
            ? existing.EncryptedApiKey
            : DpapiProtector.Protect(SettingKeyBox.Password);

        var saved = new AiSettings
        {
            Endpoint = SettingEndpointBox.Text.Trim(),
            Model = SettingModelBox.Text.Trim(),
            Temperature = temp,
            TimeoutSeconds = timeoutSec,
            EncryptedApiKey = encrypted,
            TraceMemoBaseUrl = SettingTraceMemoBox.Text.Trim(),
            ContextMessageCount = existing.ContextMessageCount,
            ThemeMode = existing.ThemeMode,
            WindowWidth = existing.WindowWidth,
            WindowHeight = existing.WindowHeight
        };

        _settingsStore.Save(saved);

        ShowSettingTestResult(true, "保存成功", "AI 设置与密钥已加密保存至本地。", 0);
    }

    private void DeleteCustomSkillFromSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string skillId)
        {
            var res = MessageBox.Show("确定要删除该技能吗？若为本地绑定的技能文件，将同步从磁盘目录中安全删除，防止重新加载。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                bool deleted = _skillStore.DeleteCustomSkill(skillId);
                InitSkillCombo();
                RefreshSettingsSkillList();
                if (deleted)
                {
                    MessageBox.Show("✅ 技能已成功删除并同步清理！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }

    private void SettingOpacityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingOpacityCombo?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double opacity))
        {
            this.Opacity = opacity;
        }
    }

    private void SettingOcrScaleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingOcrScaleCombo?.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            _ocrUpscaleFactor = (int)Math.Round(scale);
        }
    }

    private void SettingSelfNicknameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string text = SettingSelfNicknameBox.Text.Trim();
        _mySelfNickname = string.IsNullOrEmpty(text) ? null : text;
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        string dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot");
        Directory.CreateDirectory(dir);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开文件夹失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearChatHistoryCache_Click(object sender, RoutedEventArgs e)
    {
        _conversation.Clear();
        RefreshChatView();
        StatusText.Text = "当前对话气泡与识别缓存已清空";
        MessageBox.Show("当前对话气泡及识别缓存已清空。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenTraceMemoUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/Wxw-Gu/TraceMemo",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开浏览器失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyTraceMemoUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("https://github.com/Wxw-Gu/TraceMemo");
            MessageBox.Show("TraceMemo 项目地址已复制到剪贴板：\nhttps://github.com/Wxw-Gu/TraceMemo", "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制失败: " + ex.Message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

public sealed record SettingSkillItem(
    string Id,
    string DisplayTitle,
    string Description,
    string TagText,
    Visibility DeleteVisibility);

public sealed class PersonaGoalItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _ultimateGoal = string.Empty;

    public string ContactName { get; init; } = string.Empty;
    public string TraitCountText { get; init; } = string.Empty;
    public string UpdatedTimeText { get; init; } = string.Empty;

    public string UltimateGoal
    {
        get => _ultimateGoal;
        set
        {
            if (_ultimateGoal != value)
            {
                _ultimateGoal = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(UltimateGoal)));
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}


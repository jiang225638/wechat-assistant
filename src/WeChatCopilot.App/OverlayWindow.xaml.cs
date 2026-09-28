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
    private const int OcrUpscaleFactor = 2;

    private readonly IWeChatWindowLocator _locator = new WeChatWindowLocator();
    private readonly IWindowTracker _tracker = new PollingWindowTracker();
    private readonly IOcrEngine _ocrEngine = new WindowsMediaOcrEngine();
    private readonly MessageSegmenter _segmenter = new();
    private readonly ConversationBuffer _conversation = new();
    private readonly List<HistoryMessage> _history = new();
    private readonly PersonaStore _personaStore = new();
    private readonly ChatRegionOptions _cropOptions = new();

    private readonly DispatcherTimer _relocateTimer;
    private readonly DispatcherTimer _autoReadTimer;

    private IHotkeyManager? _hotkeys;
    private System.Windows.Forms.NotifyIcon? _tray;
    private nint _hwnd;
    private bool _pinned;
    private bool _manualHidden;
    private bool _autoRead;
    private bool _reading;
    private bool _autoSyncContact = true;
    private bool _isSyncingPersonaSelection;
    private string _currentChatContact = string.Empty;
    private string? _mySelfNickname;
    private Persona? _currentLoadedPersona;

    public OverlayWindow()
    {
        InitializeComponent();

        _tracker.TargetChanged += OnTargetChanged;

        _relocateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _relocateTimer.Tick += (_, _) =>
        {
            if (!_pinned && _tracker.GetCurrent() is null)
            {
                TryLocate();
            }
        };

        _autoReadTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoReadTimer.Tick += async (_, _) => await AutoReadTickAsync();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        InitHotkeys();
        InitTray();

        RefreshSavedPersonasCombo();
        TryLocate();
        _relocateTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tracker.Dispose();
        _relocateTimer.Stop();
        _autoReadTimer.Stop();
        _hotkeys?.Dispose();

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
        if (IsVisible)
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

    // ===================== 顶部控制与诊断 =====================

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        _pinned = PinButton.IsChecked == true;
        PinButton.Content = _pinned ? "🔓 已固定" : "📌 吸附中";
        HeaderTitlePanel.Cursor = _pinned ? Cursors.SizeAll : Cursors.Arrow;

        if (!_pinned)
        {
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

    private void QuitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void DiagToggle_Changed(object sender, RoutedEventArgs e)
    {
        DiagPanel.Visibility = DiagToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DiagToggle.Content = DiagToggle.IsChecked == true ? "诊断 ▴" : "诊断 ▾";
    }

    private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        if (win.ShowDialog() == true)
        {
            SetPersonaStatus("已更新 AI 与 TraceMemo 设置。", isBusy: false);
        }
    }

    // ===================== TAB 标签切换 =====================

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewChat is null || ViewAi is null || ViewPersona is null || ViewHelp is null)
        {
            return;
        }

        int index = MainTabControl.SelectedIndex;
        ViewChat.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewAi.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewPersona.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewHelp.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 2)
        {
            RefreshSavedPersonasCombo();
            string target = ContactBox.Text.Trim();
            if (!string.IsNullOrEmpty(target))
            {
                CheckAndLoadPersona(target);
            }
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
            var crop = info.ClassName == "ChatWnd"
                ? new ChatRegionOptions { TrimLeftRatio = 0.02, TrimTopRatio = 0.08, TrimBottomRatio = 0.25 }
                : _cropOptions;
            var region = ChatRegionCropper.ComputeChatRegion(info.Bounds, crop);
            var image = ScreenCapture.CaptureRegion(region);
            if (image is null)
            {
                return;
            }

            image = image.ScaleNearest(OcrUpscaleFactor);
            var ocr = await _ocrEngine.RecognizeAsync(image);
            var frame = _segmenter.Segment(ocr, image.Width);
            int added = _conversation.Ingest(frame);

            if (!silent || added > 0)
            {
                RefreshChatView();
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

            image = image.ScaleNearest(OcrUpscaleFactor);
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

    private void ModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (SuggestionScroll is null || SubtextScroll is null)
        {
            return;
        }

        bool subtext = SubtextModeRadio.IsChecked == true;
        SuggestionScroll.Visibility = subtext ? Visibility.Collapsed : Visibility.Visible;
        SubtextScroll.Visibility = subtext ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation.Count == 0)
        {
            SetAiStatus("对话缓冲为空，请先在「当前对话」点【读取当前对话】。", isError: true);
            return;
        }

        bool subtext = SubtextModeRadio.IsChecked == true;
        int count = CountBox.SelectedIndex == 1 ? 5 : 3;

        GenerateButton.IsEnabled = false;
        AiStatusBanner.Visibility = Visibility.Visible;
        AiStatusText.Text = subtext ? "AI 正在深度分析潜台词与真实意图..." : $"AI 正在生成 {count} 种风格回复建议...";

        try
        {
            var settings = new SettingsStore().Load();
            string key = DpapiProtector.Unprotect(settings.EncryptedApiKey);
            using var provider = new OpenAiCompatibleProvider(settings.Endpoint, () => key);

            // 融入当前联系人画像
            Persona? personaContext = null;
            if (IncludePersonaCheck.IsChecked == true)
            {
                string target = ContactBox.Text.Trim();
                if (!string.IsNullOrEmpty(target))
                {
                    personaContext = _personaStore.Load(target);
                }
            }

            AiRequest req = subtext
                ? PromptBuilder.BuildSubtextAnalysis(settings, _conversation.Messages, personaContext)
                : PromptBuilder.BuildReplySuggestions(settings, _conversation.Messages, count, personaContext);

            AiReply reply = await provider.CompleteAsync(req);
            AiStatusBanner.Visibility = Visibility.Collapsed;

            if (!reply.Success)
            {
                SetAiStatus("AI 生成失败：" + reply.Error, isError: true);
                return;
            }

            if (subtext)
            {
                var parsedSubtext = AiOutputParser.ParseSubtext(reply.Text);
                ShowSubtextAnalysis(parsedSubtext);
            }
            else
            {
                var suggestions = AiOutputParser.ParseReplySuggestions(reply.Text);
                SuggestionCards.ItemsSource = suggestions;
                SuggestionScroll.Visibility = Visibility.Visible;
                SubtextScroll.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            AiStatusBanner.Visibility = Visibility.Collapsed;
            SetAiStatus("调用异常：" + ex.Message, isError: true);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    private void ShowSubtextAnalysis(SubtextAnalysis s)
    {
        var targetMsg = _conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Incoming) ?? _conversation.Messages.LastOrDefault();
        string targetText = targetMsg?.Text ?? "（未找到对方具体发言）";
        SubtextTargetText.Text = $"“{targetText}”";

        SubtextLiteralText.Text = string.IsNullOrWhiteSpace(s.Literal) ? "（未解析）" : s.Literal;
        SubtextSubtextText.Text = string.IsNullOrWhiteSpace(s.Subtext) ? "（未解析）" : s.Subtext;
        SubtextEmotionText.Text = string.IsNullOrWhiteSpace(s.Emotion) ? "（未解析）" : s.Emotion;
        SubtextIntentText.Text = string.IsNullOrWhiteSpace(s.Intent) ? "（未解析）" : s.Intent;
        SubtextDesiredResponseText.Text = string.IsNullOrWhiteSpace(s.DesiredResponse) ? "（未解析）" : s.DesiredResponse;
        SubtextStrategyText.Text = string.IsNullOrWhiteSpace(s.Strategy) ? "（未解析）" : s.Strategy;

        SuggestionScroll.Visibility = Visibility.Collapsed;
        SubtextScroll.Visibility = Visibility.Visible;
    }

    private void SetAiStatus(string text, bool isError = false)
    {
        AiStatusBanner.Visibility = Visibility.Visible;
        AiStatusText.Text = text;
        AiStatusText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(248, 113, 113)) : (SolidColorBrush)FindResource("TextPrimaryBrush");
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
        }
        else
        {
            _currentLoadedPersona = null;
            PersonaScroll.Visibility = Visibility.Collapsed;
            PersonaEmptyCard.Visibility = Visibility.Visible;
            PersonaEmptyTitle.Text = "请选择或输入联系人";
            RadarCanvas.Children.Clear();
        }
    }

    private async void SyncContactButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSyncContact = true;
        SyncContactButton.IsEnabled = false;
        SetPersonaStatus("正在从微信与 TraceMemo 探测当前对话好友...", isBusy: true);

        try
        {
            string? detectedName = null;

            // 1. 优先尝试从 TraceMemo 获取当前活跃单聊好友（排除群聊和系统号）
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

            // 2. 如果当前窗口是独立聊天窗口 (ChatWnd)，取窗口标题
            if (string.IsNullOrWhiteSpace(detectedName))
            {
                var info = _tracker.GetCurrent();
                if (info is not null && info.ClassName == "ChatWnd")
                {
                    string titleContact = CleanContactName(info.Title);
                    if (!string.IsNullOrEmpty(titleContact) && titleContact != _mySelfNickname)
                    {
                        detectedName = titleContact;
                    }
                }
            }

            // 3. 检查已记录的当前非本人联系人
            if (string.IsNullOrWhiteSpace(detectedName) && !string.IsNullOrEmpty(_currentChatContact) && _currentChatContact != _mySelfNickname)
            {
                detectedName = _currentChatContact;
            }

            if (!string.IsNullOrEmpty(detectedName) && detectedName != _mySelfNickname)
            {
                _currentChatContact = detectedName;
                ContactBox.Text = detectedName;
                CheckAndLoadPersona(detectedName);
                SetPersonaStatus($"✅ 已同步当前微信聊天对象：「{detectedName}」", isBusy: false);
            }
            else
            {
                SetPersonaStatus("未能从 TraceMemo 识别到活跃会话。如为独立聊天窗口请先点击激活，或在上方直接输入好友昵称/备注。", isBusy: false);
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
            }
            finally
            {
                _isSyncingPersonaSelection = false;
            }

            CheckAndLoadPersona(name);
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
        if (string.IsNullOrWhiteSpace(settings.TraceMemoBaseUrl))
        {
            SetPersonaStatus("未配置 TraceMemo 地址，请在「AI 设置」中填写", isBusy: false);
            return;
        }

        FetchHistoryButton.IsEnabled = false;
        SetPersonaStatus($"正在连接 TraceMemo (6131) 并解析「{name}」历史记录...", isBusy: true);

        try
        {
            using var client = new TraceMemoClient(settings.TraceMemoBaseUrl);
            var res = await client.FetchHistoryAsync(name);

            if (!res.Success)
            {
                SetPersonaStatus($"❌ TraceMemo 拉取失败：{res.Error}\n（请确认 TraceMemo 正在运行并已同步微信；也可使用「导入 CSV」）", isBusy: false);
                return;
            }

            _history.Clear();
            _history.AddRange(res.Messages);

            if (res.Messages.Count > 0)
            {
                SetPersonaStatus($"✅ 已成功从 TraceMemo 拉取「{name}」共 {res.Messages.Count} 条历史消息。可点击【✨ 蒸馏/更新画像】生成多维画像！", isBusy: false);
            }
            else
            {
                SetPersonaStatus($"ℹ️ TraceMemo 中找到了联系人「{name}」，但本地数据库暂无该会话的聊天消息", isBusy: false);
            }
        }
        catch (Exception ex)
        {
            SetPersonaStatus("TraceMemo 拉取异常：" + ex.Message, isBusy: false);
        }
        finally
        {
            FetchHistoryButton.IsEnabled = true;
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

        // 若历史为空，尝试自动拉取一次 TraceMemo
        if (_history.Count == 0)
        {
            var settings = new SettingsStore().Load();
            SetPersonaStatus($"未检测到缓存历史，正在自动连接 TraceMemo 拉取「{name}」...", isBusy: true);

            try
            {
                using var client = new TraceMemoClient(settings.TraceMemoBaseUrl);
                var fetchRes = await client.FetchHistoryAsync(name);
                if (fetchRes.Success && fetchRes.Messages.Count > 0)
                {
                    _history.Clear();
                    _history.AddRange(fetchRes.Messages);
                }
            }
            catch
            {
                // 忽略，继续判断
            }

            if (_history.Count == 0)
            {
                SetPersonaStatus("历史记录为空：请先点击【⚡ 拉取 TraceMemo】或使用【📥 导入CSV】导入聊天记录。", isBusy: false);
                return;
            }
        }

        DistillButton.IsEnabled = false;
        SetPersonaStatus($"🧠 AI 正在深度分析 {name} 的 {_history.Count} 条历史消息，蒸馏七维特质画像（约需数秒）...", isBusy: true);

        try
        {
            var settings = new SettingsStore().Load();
            string key = DpapiProtector.Unprotect(settings.EncryptedApiKey);
            using var provider = new OpenAiCompatibleProvider(settings.Endpoint, () => key);

            Persona? existing = _personaStore.Load(name);
            var distillResult = await PersonaDistiller.DistillDetailedAsync(provider, settings, name, _history, existing);

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
            SetPersonaStatus($"✅「{name}」画像蒸馏成功并已持久化保存！（含 {persona.Traits.Count} 项特质，基于 {persona.SourceMessageCount} 条历史）", isBusy: false);
        }
        catch (Exception ex)
        {
            SetPersonaStatus("画像蒸馏异常：" + ex.Message, isBusy: false);
        }
        finally
        {
            DistillButton.IsEnabled = true;
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
            SetPersonaStatus($"✅ 成功导入 CSV 历史 {messages.Count} 条，可点击【✨ 蒸馏/更新画像】。", isBusy: false);
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

        string path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot", "personas", name + ".json");

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        _currentLoadedPersona = null;
        PersonaScroll.Visibility = Visibility.Collapsed;
        PersonaEmptyCard.Visibility = Visibility.Visible;
        PersonaEmptyTitle.Text = $"已清除「{name}」的画像";
        RefreshSavedPersonasCombo();
        SetPersonaStatus($"已删除「{name}」的本地画像文件。", isBusy: false);
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
        PersonaCards.ItemsSource = p.Traits;

        DrawRadarChart(p.Traits);
    }

    private void DrawRadarChart(IReadOnlyList<PersonaTrait>? traits)
    {
        RadarCanvas.Children.Clear();

        double cx = RadarCanvas.Width / 2.0;
        double cy = RadarCanvas.Height / 2.0;
        double maxR = 64.0;

        // 整理六维数据：优先匹配专业六维，缺失时自适应填充
        var standardDims = new[]
        {
            ("沟通风格", 75.0),
            ("性格能量", 70.0),
            ("决策模式", 65.0),
            ("情绪阈值", 80.0),
            ("价值锚点", 85.0),
            ("隐形雷区", 60.0)
        };

        var data = new List<(string Label, double Score)>();
        if (traits != null && traits.Count >= 3)
        {
            foreach (var t in traits)
            {
                double score = t.Score > 0 ? t.Score : Math.Clamp(t.Confidence * 100, 20, 95);
                data.Add((t.Dimension, score));
            }
        }
        else if (traits != null && traits.Count > 0)
        {
            foreach (var (dim, defScore) in standardDims)
            {
                var matched = traits.FirstOrDefault(t => t.Dimension.Contains(dim) || dim.Contains(t.Dimension));
                double score = matched != null ? (matched.Score > 0 ? matched.Score : Math.Clamp(matched.Confidence * 100, 20, 95)) : defScore;
                data.Add((dim, score));
            }
        }
        else
        {
            foreach (var (dim, defScore) in standardDims)
            {
                data.Add((dim, defScore));
            }
        }

        int count = data.Count;
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
            Fill = new SolidColorBrush(Color.FromArgb(50, 56, 189, 248))
        };
        RadarCanvas.Children.Add(dataPoly);

        // 4. 数据点高光圆点
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

        // 5. 外周维度标签与评分 (居中对齐)
        double labelR = maxR + 15;
        for (int i = 0; i < count; i++)
        {
            double angle = startAngle + i * angleStep;
            double lx = cx + labelR * Math.Cos(angle);
            double ly = cy + labelR * Math.Sin(angle);

            var tb = new TextBlock
            {
                Text = $"{data[i].Label}\n{(int)data[i].Score}分",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208)),
                TextAlignment = TextAlignment.Center,
                Width = 60
            };

            Canvas.SetLeft(tb, lx - 30);
            Canvas.SetTop(tb, ly - 13);
            RadarCanvas.Children.Add(tb);
        }
    }

    private void SetPersonaStatus(string text, bool isBusy)
    {
        PersonaStatusText.Text = text;
        PersonaProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
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
            return;
        }

        if (info.IsMinimized)
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

        var overlay = new OverlaySize((int)ActualWidth, (int)ActualHeight);
        var (x, y) = OverlayLayout.ComputeSnapPosition(
            info.Bounds, overlay, SnapSide.Right, gap: 0, alignTop: true);

        WindowPositioner.MoveToTopmost(_hwnd, x, y);
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

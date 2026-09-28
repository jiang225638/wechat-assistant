using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Ai;
using WeChatCopilot.Core.Layout;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Core.Parsing;
using WeChatCopilot.AI;
using WeChatCopilot.Data;
using WeChatCopilot.Data.Security;
using WeChatCopilot.Ocr;
using WeChatCopilot.Windows;

namespace WeChatCopilot.App;

/// <summary>
/// 悬浮窗主界面（M1 外壳 + M2 读取 + M4 AI 面板）：定位微信主窗口并贴附右缘实时跟随；
/// Tab 分区＝对话(读取/自动读取/OCR)、AI(多候选回复卡片/潜台词面板/设置)、帮助(热键)；
/// 支持全局热键显隐/重定位/读取、「固定/跟随」切换（固定后可拖动标题栏）与系统托盘图标。
/// </summary>
public partial class OverlayWindow : Window
{
    // WM_HOTKEY（与 Windows.Interop.NativeMethods.WM_HOTKEY 一致；后者为 internal，此处本地定义）
    private const int WM_HOTKEY = 0x0312;

    // 热键 id
    private const int HOTKEY_TOGGLE = 1;
    private const int HOTKEY_RELOCATE = 2;
    private const int HOTKEY_OCR = 3;
    private const int HOTKEY_READ = 4;

    // 虚拟键码
    private const uint VK_W = 0x57;
    private const uint VK_E = 0x45;
    private const uint VK_O = 0x4F;
    private const uint VK_D = 0x44;

    private const HotkeyModifiers HotkeyMods =
        HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat;

    private readonly IWeChatWindowLocator _locator = new WeChatWindowLocator();
    private readonly PollingWindowTracker _tracker = new(intervalMs: 60);
    private readonly IOcrEngine _ocrEngine = new WindowsMediaOcrEngine();

    // OCR 前对截图做整数倍放大，提升小字中文识别率（1=不放大）
    private const int OcrUpscaleFactor = 2;
    private readonly MessageSegmenter _segmenter = new();
    private readonly ConversationBuffer _conversation = new();
    private readonly List<HistoryMessage> _history = new();  // 冷链路：当前联系人历史（CSV 导入/TraceMemo 拉取）
    private readonly PersonaStore _personaStore = new();
    private readonly ChatRegionOptions _cropOptions = new();
    private readonly DispatcherTimer _relocateTimer;
    private readonly DispatcherTimer _autoReadTimer;

    private IHotkeyManager? _hotkeys;
    private System.Windows.Forms.NotifyIcon? _tray;
    private nint _hwnd;
    private bool _pinned;        // true=已固定（停止跟随，可拖动）；false=跟随微信
    private bool _manualHidden;  // 用户主动隐藏，避免跟随逻辑又把它 Show 出来
    private bool _autoRead;      // 自动读取开关：轮询检测新消息并入缓冲
    private bool _reading;       // 防重入：自动轮询与手动读取互斥

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

        // 自动读取轮询：周期性静默读取对话，检测到新消息才刷新输出
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

    // ---- 消息钩子：分发 WM_HOTKEY ----

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            _hotkeys?.OnHotkeyMessage(wParam.ToInt32());
            handled = true;
        }

        return nint.Zero;
    }

    // ---- 全局热键 ----

    private void InitHotkeys()
    {
        _hotkeys = new HotkeyManager(_hwnd);

        var failed = new List<string>();
        if (!_hotkeys.Register(HOTKEY_TOGGLE, HotkeyMods, VK_W, ToggleVisibility))
        {
            failed.Add("Ctrl+Alt+W");
        }

        if (!_hotkeys.Register(HOTKEY_RELOCATE, HotkeyMods, VK_E, TryLocate))
        {
            failed.Add("Ctrl+Alt+E");
        }

        if (!_hotkeys.Register(HOTKEY_OCR, HotkeyMods, VK_O, () => _ = RunOcrAsync()))
        {
            failed.Add("Ctrl+Alt+O");
        }

        if (!_hotkeys.Register(HOTKEY_READ, HotkeyMods, VK_D, () => _ = ReadConversationAsync()))
        {
            failed.Add("Ctrl+Alt+D");
        }

        if (failed.Count > 0)
        {
            HintText.Text = "部分热键注册失败（可能被占用）：" + string.Join("、", failed) +
                            "\n切到「已固定」后可拖动标题栏自由摆放";
        }
    }

    private void ToggleVisibility()
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

    // ---- 托盘图标 ----

    private void InitTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "WeChat Copilot"
        };

        _tray.DoubleClick += (_, _) => ToggleVisibility();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示/隐藏", null, (_, _) => ToggleVisibility());
        menu.Items.Add("重新定位", null, (_, _) => TryLocate());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Application.Current.Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    // ---- 固定 / 拖拽 ----

    private void PinButton_Changed(object sender, RoutedEventArgs e)
    {
        _pinned = PinButton.IsChecked == true;
        PinButton.Content = _pinned ? "已固定" : "跟随中";
        HeaderTitle.Cursor = _pinned ? Cursors.SizeAll : Cursors.Arrow;

        // 取消固定时立即重新贴回微信
        if (!_pinned)
        {
            TryLocate();
        }
    }

    private void HeaderTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 仅在「已固定」时允许拖动；跟随状态下会被吸附逻辑拉回，拖动无意义
        if (_pinned && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    // ---- 按钮 ----

    private void RelocateButton_Click(object sender, RoutedEventArgs e) => TryLocate();

    private void QuitButton_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private async void OcrButton_Click(object sender, RoutedEventArgs e) => await RunOcrAsync();

    private async void ReadButton_Click(object sender, RoutedEventArgs e) => await ReadConversationAsync();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _conversation.Clear();
        ShowResultText("已清空对话缓冲。");
    }

    private void AutoReadButton_Changed(object sender, RoutedEventArgs e)
    {
        _autoRead = AutoReadButton.IsChecked == true;
        AutoReadButton.Content = _autoRead ? "自动读取:开" : "自动读取:关";

        if (_autoRead)
        {
            _autoReadTimer.Start();
        }
        else
        {
            _autoReadTimer.Stop();
        }
    }

    /// <summary>自动读取轮询回调：仅在开关打开且无重入时静默读取一次。</summary>
    private async Task AutoReadTickAsync()
    {
        if (!_autoRead || _reading || _tracker.GetCurrent() is null)
        {
            return;
        }

        await ReadConversationAsync(silent: true);
    }

    // ---- M4 AI 生成：多候选回复卡片 / 六维潜台词面板 ----

    private async void GenerateButton_Click(object sender, RoutedEventArgs e) =>
        await RunAiAsync(subtext: SubtextModeRadio.IsChecked == true,
                         count: CountBox.SelectedIndex == 1 ? 5 : 3);

    /// <summary>模式切换：候选数仅对回复建议模式有意义。</summary>
    private void ModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (CountBox is not null)
        {
            CountBox.IsEnabled = ReplyModeRadio.IsChecked == true;
        }
    }

    private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow { Owner = this };
        win.ShowDialog();  // 保存结果落盘；下次 AI 调用重新 Load
    }

    /// <summary>用当前对话缓冲调用 AI：回复建议解析为候选卡片，潜台词解析为六维意图面板。</summary>
    private async Task RunAiAsync(bool subtext, int count)
    {
        if (_conversation.Count == 0)
        {
            ShowResultText("对话缓冲为空，请先在「对话」页点「读取对话」或开启「自动读取」。");
            return;
        }

        GenerateButton.IsEnabled = false;
        try
        {
            var settings = new SettingsStore().Load();
            string key = DpapiProtector.Unprotect(settings.EncryptedApiKey);
            using var provider = new OpenAiCompatibleProvider(settings.Endpoint, () => key);

            AiRequest req = subtext
                ? PromptBuilder.BuildSubtextAnalysis(settings, _conversation.Messages)
                : PromptBuilder.BuildReplySuggestions(settings, _conversation.Messages, count);

            AiReply reply = await provider.CompleteAsync(req);
            if (!reply.Success)
            {
                ShowResultText("AI 调用失败：" + reply.Error);
                return;
            }

            if (subtext)
            {
                ShowResultText(AiOutputParser.ParseSubtext(reply.Text).Format());
            }
            else
            {
                var suggestions = AiOutputParser.ParseReplySuggestions(reply.Text);
                if (suggestions.Count == 0)
                {
                    ShowResultText(reply.Text);  // 模型未按格式输出：回退展示原文
                }
                else
                {
                    ShowSuggestions(suggestions);
                }
            }
        }
        catch (Exception ex)
        {
            ShowResultText("AI 调用异常：" + ex.Message);
        }
        finally
        {
            GenerateButton.IsEnabled = true;
        }
    }

    /// <summary>复制单条候选回复到剪贴板，并短暂显示"已复制"反馈。</summary>
    private void CopySuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string text || text.Length == 0)
        {
            return;
        }

        Clipboard.SetText(text);
        btn.Content = "已复制";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            btn.Content = "复制";
        };
        timer.Start();
    }

    // ---- 输出区切换：文本输出与建议卡片互斥显示 ----

    /// <summary>展示纯文本结果（同时收起建议卡片与画像卡片）。</summary>
    private void ShowResultText(string text)
    {
        OcrOutput.Text = text;
        OcrOutput.Visibility = Visibility.Visible;
        SuggestionScroll.Visibility = Visibility.Collapsed;
        PersonaScroll.Visibility = Visibility.Collapsed;
    }

    /// <summary>展示回复建议候选卡片（同时隐藏文本输出与画像卡片）。</summary>
    private void ShowSuggestions(IReadOnlyList<ReplySuggestion> suggestions)
    {
        SuggestionCards.ItemsSource = suggestions;
        SuggestionScroll.Visibility = Visibility.Visible;
        OcrOutput.Visibility = Visibility.Collapsed;
        PersonaScroll.Visibility = Visibility.Collapsed;
    }

    /// <summary>展示画像特质卡片（同时隐藏文本输出与建议卡片）。</summary>
    private void ShowPersona(Persona persona)
    {
        PersonaCards.ItemsSource = persona.Traits;
        PersonaScroll.Visibility = Visibility.Visible;
        SuggestionScroll.Visibility = Visibility.Collapsed;
        OcrOutput.Visibility = Visibility.Collapsed;
    }

    // ---- M5 冷链路：历史导入/拉取 → 人格蒸馏 → 画像卡片 ----

    private void ImportCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            Title = "导入聊天历史 CSV（role,text,ts）"
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var msgs = CsvHistoryImporter.Parse(System.IO.File.ReadAllText(dlg.FileName));
            _history.Clear();
            _history.AddRange(msgs);
            ShowResultText($"已导入 {msgs.Count} 条历史（{System.IO.Path.GetFileName(dlg.FileName)}）。填联系人名后可「蒸馏/更新画像」。");
        }
        catch (Exception ex)
        {
            ShowResultText("CSV 导入失败：" + ex.Message);
        }
    }

    private async void FetchHistoryButton_Click(object sender, RoutedEventArgs e) => await FetchHistoryAsync();

    /// <summary>从 TraceMemo Local API 拉取指定联系人完整历史；失败降级提示，不影响热链路。</summary>
    private async Task FetchHistoryAsync()
    {
        string name = ContactBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowResultText("请先填写联系人名（用于在 TraceMemo 中查历史）。");
            return;
        }

        var settings = new SettingsStore().Load();
        if (string.IsNullOrWhiteSpace(settings.TraceMemoBaseUrl))
        {
            ShowResultText("未配置 TraceMemo 地址，请在「AI 设置」填写。");
            return;
        }

        FetchHistoryButton.IsEnabled = false;
        try
        {
            using var client = new TraceMemoClient(settings.TraceMemoBaseUrl);
            var res = await client.FetchHistoryAsync(name);
            if (!res.Success)
            {
                ShowResultText("TraceMemo 拉取失败：" + res.Error +
                               "\n请确认 TraceMemo 已运行且端口正确（AI 设置可改）；或改用「导入CSV」。");
                return;
            }

            _history.Clear();
            _history.AddRange(res.Messages);
            ShowResultText($"已从 TraceMemo 拉取「{name}」历史 {res.Messages.Count} 条。可「蒸馏/更新画像」。");
        }
        catch (Exception ex)
        {
            ShowResultText("TraceMemo 拉取异常：" + ex.Message);
        }
        finally
        {
            FetchHistoryButton.IsEnabled = true;
        }
    }

    private async void DistillButton_Click(object sender, RoutedEventArgs e) => await DistillPersonaAsync();

    /// <summary>蒸馏/增量更新画像：分块 map → reduce 合并（含已有画像）→ 落盘 → 卡片展示。</summary>
    private async Task DistillPersonaAsync()
    {
        string name = ContactBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowResultText("请先填写联系人名。");
            return;
        }

        if (_history.Count == 0)
        {
            ShowResultText("历史为空：请先「导入CSV」或「拉取TraceMemo」。");
            return;
        }

        DistillButton.IsEnabled = false;
        try
        {
            var settings = new SettingsStore().Load();
            string key = DpapiProtector.Unprotect(settings.EncryptedApiKey);
            using var provider = new OpenAiCompatibleProvider(settings.Endpoint, () => key);

            Persona? existing = _personaStore.Load(name);
            Persona persona = await PersonaDistiller.DistillAsync(provider, settings, name, _history, existing);
            if (persona.Traits.Count == 0)
            {
                ShowResultText("蒸馏结果为空：模型未按约定格式输出。可重试或换模型。");
                return;
            }

            _personaStore.Save(persona);
            ShowPersona(persona);
        }
        catch (Exception ex)
        {
            ShowResultText("画像蒸馏异常：" + ex.Message);
        }
        finally
        {
            DistillButton.IsEnabled = true;
        }
    }

    private void ViewPersonaButton_Click(object sender, RoutedEventArgs e)
    {
        string name = ContactBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowResultText("请先填写联系人名。");
            return;
        }

        Persona? persona = _personaStore.Load(name);
        if (persona is null)
        {
            ShowResultText($"未找到「{name}」的已存画像。已存：" +
                           (_personaStore.List().Count > 0 ? string.Join("、", _personaStore.List()) : "无"));
            return;
        }

        ShowPersona(persona);
    }

    private async Task RunOcrAsync()
    {
        var info = _tracker.GetCurrent();
        if (info is null)
        {
            ShowResultText("尚未定位到微信窗口，请先点“重新定位”。");
            return;
        }

        OcrButton.IsEnabled = false;
        try
        {
            var image = ScreenCapture.CaptureRegion(info.Bounds);
            if (image is null)
            {
                ShowResultText("截图失败：无法从屏幕抓取微信窗口区域（窗口可能最小化或尺寸为 0）。");
                return;
            }

            // 放大后再识别，缓解小字中文误识
            image = image.ScaleNearest(OcrUpscaleFactor);
            var result = await _ocrEngine.RecognizeAsync(image);

            var sb = new StringBuilder();
            sb.AppendLine($"引擎={result.Engine}  截图={image.Width}x{image.Height}(x{OcrUpscaleFactor})  行数={result.LineCount}  耗时={result.Elapsed.TotalMilliseconds:F0}ms");
            sb.AppendLine("可用 OCR 语言: " +
                (WindowsMediaOcrEngine.AvailableLanguages() is { Count: > 0 } langs ? string.Join(", ", langs) : "无"));
            sb.AppendLine(new string('-', 40));
            foreach (var line in result.Lines)
            {
                double x = line.Words.Count > 0 ? line.Words[0].X : 0;
                sb.AppendLine($"[x={x:F0}] {line.Text}");
            }

            sb.AppendLine(new string('-', 40));
            sb.AppendLine("整段文本：");
            sb.AppendLine(result.Text);

            ShowResultText(sb.ToString());
        }
        catch (Exception ex)
        {
            ShowResultText("OCR 失败：" + ex.Message);
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// M2 热链路：裁剪聊天区 -> 截屏 -> OCR -> 切分收发 -> 并入对话缓冲（跨帧去重）-> 展示。
    /// </summary>
    private async Task ReadConversationAsync(bool silent = false)
    {
        var info = _tracker.GetCurrent();
        if (info is null)
        {
            if (!silent)
            {
                ShowResultText("尚未定位到微信窗口，请先点“重新定位”。");
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
            var region = ChatRegionCropper.ComputeChatRegion(info.Bounds, _cropOptions);
            var image = ScreenCapture.CaptureRegion(region);
            if (image is null)
            {
                if (!silent)
                {
                    ShowResultText("截图失败：聊天区域无效或无法抓取（微信可能最小化）。");
                }

                return;
            }

            // 放大后再识别，缓解小字中文误识；切分用放大后的宽度
            image = image.ScaleNearest(OcrUpscaleFactor);
            var ocr = await _ocrEngine.RecognizeAsync(image);
            var frame = _segmenter.Segment(ocr, image.Width);
            int added = _conversation.Ingest(frame);

            var sb = new StringBuilder();
            sb.AppendLine($"聊天区=({region.X},{region.Y}) {region.Width}x{region.Height}  引擎={ocr.Engine}  耗时={ocr.Elapsed.TotalMilliseconds:F0}ms");
            sb.AppendLine($"本帧解析={frame.Count} 条  新增={added} 条  缓冲累计={_conversation.Count} 条");
            sb.AppendLine(new string('-', 40));
            if (_conversation.Count == 0)
            {
                sb.AppendLine("（未解析到消息。请校准裁剪比例或确认聊天区有可见气泡。）");
            }

            foreach (var m in _conversation.Messages)
            {
                string who = m.Role switch
                {
                    MessageRole.Incoming => "对方",
                    MessageRole.Outgoing => "我  ",
                    _ => "?   "
                };
                sb.AppendLine($"[{who}] {m.Text.Replace("\n", " / ")}");
            }

            if (!silent || added > 0)
            {
                ShowResultText(sb.ToString());
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                ShowResultText("读取对话失败：" + ex.Message);
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

    // ---- 定位与跟随 ----

    private void TryLocate()
    {
        var info = _locator.FindWeChatMainWindow();
        if (info is null)
        {
            _tracker.Stop();
            StatusText.Text = "未找到微信窗口。请确认微信 4.x 已登录并运行（进程名 Weixin.exe）。";
            BoundsText.Text = string.Empty;
            return;
        }

        StatusText.Text = $"已定位微信：{info.Title}";
        _tracker.Start(info);
        UpdatePlacement(info);
    }

    private void OnTargetChanged(WindowInfo? info)
    {
        // 回调可能来自线程池线程，切回 UI 线程处理
        Dispatcher.BeginInvoke(() =>
        {
            if (info is null)
            {
                StatusText.Text = "微信窗口已关闭/丢失，正在重新查找...";
                return;
            }

            UpdatePlacement(info);
        });
    }

    private void UpdatePlacement(WindowInfo info)
    {
        BoundsText.Text =
            $"句柄={info.Handle}  类名={info.ClassName}\n" +
            $"位置=({info.Bounds.X},{info.Bounds.Y})  大小={info.Bounds.Width}x{info.Bounds.Height}\n" +
            $"最小化={info.IsMinimized}  模式={(_pinned ? "已固定" : "跟随中")}";

        // 已固定：不跟随、不自动显隐，位置完全由用户拖动决定
        if (_pinned)
        {
            return;
        }

        // 微信最小化：自动隐藏悬浮窗
        if (info.IsMinimized)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        // 恢复显示（除非用户主动隐藏）
        if (!IsVisible && !_manualHidden)
        {
            Show();
        }

        // 右侧贴边 + 顶部对齐：x=目标右缘、y=目标顶部（均为物理像素）
        var overlay = new OverlaySize((int)ActualWidth, (int)ActualHeight);
        var (x, y) = OverlayLayout.ComputeSnapPosition(
            info.Bounds, overlay, SnapSide.Right, gap: 0, alignTop: true);

        WindowPositioner.MoveToTopmost(_hwnd, x, y);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Diagnostics;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;
using WeChatCopilot.Windows;

namespace WeChatCopilot.App;

public partial class OverlayWindow : Window
{
    // ===================== VIEW 0: 对话 TAB & 顶栏当前会话自动识别与快选 =====================

    private readonly Dictionary<string, (DateTime? LastActiveTime, int UnreadCount)> _lastSessionSnapshots =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _lastAutoDetectedContact;
    private string? _lastSyncedMessageSignature;

    private async void ReadButton_Click(object sender, RoutedEventArgs e) =>
        await ReadConversationAsync(silent: false, forceAutoDetect: false);

    private void AutoReadButton_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _autoRead = AutoReadButton.IsChecked == true;
        AutoReadButton.Content = _autoRead ? "自动读取: 开" : "自动读取: 关";

        if (_autoRead)
        {
            _autoReadTimer.Start();
            _ = ReadConversationAsync(silent: true);
        }
        else
        {
            _autoReadTimer.Stop();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _conversation.Clear();
        _lastSyncedMessageSignature = null;
        RefreshChatView();
    }

    private async void RefreshSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRecentSessionsAsync(autoSelectActive: string.IsNullOrWhiteSpace(_currentChatContact));
    }

    private void UpdateAutoSyncToggleUi()
    {
        if (AutoSyncToggleBtn is null)
        {
            return;
        }

        AutoSyncToggleBtn.IsChecked = _autoSyncContact;
        AutoSyncToggleBtn.Content = _autoSyncContact ? "⚡ 自动" : "📌 锁定";
        AutoSyncToggleBtn.ToolTip = _autoSyncContact
            ? "已开启【自动识别当前会话】：切换微信聊天窗口或收发消息时自动跟随。点击可切换为手动锁定模式"
            : "当前处于【手动锁定会话】模式：不会随微信窗口切换而改变目标。点击可立即恢复自动识别";
    }

    private async Task SwitchActiveSessionContactAsync(string contactName, string reason, bool triggerChatRead = true)
    {
        string target = contactName.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        _currentChatContact = target;
        _lastSyncedMessageSignature = null;

        _isSyncingRecentSessionSelection = true;
        try
        {
            ContactBox.Text = target;
            SyncRecentSessionCombosSelection(target);
        }
        finally
        {
            _isSyncingRecentSessionSelection = false;
        }

        StatusText.Text = $"⚡ 已自动识别会话：{target} ({reason})";
        StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");

        if (triggerChatRead)
        {
            await ReadConversationAsync(silent: true, forceAutoDetect: false);
        }
    }

    private async Task ApplySelectedRecentSessionAsync(WeChatSessionSummary session, bool triggerChatRead)
    {
        string target = session.DisplayName.Trim();
        _currentChatContact = target;
        _lastAutoDetectedContact = target;
        _lastSyncedMessageSignature = null;

        _isSyncingRecentSessionSelection = true;
        try
        {
            ContactBox.Text = target;
            SyncRecentSessionCombosSelection(target);
        }
        finally
        {
            _isSyncingRecentSessionSelection = false;
        }

        StatusText.Text = $"已切换当前会话：{target}";
        StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");

        if (triggerChatRead)
        {
            await ReadConversationAsync(silent: false, forceAutoDetect: false);
        }
    }

    private async Task RefreshRecentSessionsAsync(bool autoSelectActive = false)
    {
        try
        {
            var settings = _settingsStore.Load();
            IReadOnlyList<WeChatSessionSummary> sessions;
            using (var provider = CompositeChatHistoryProvider.CreateDefault(settings))
            {
                sessions = await provider.GetRecentSessionsAsync(maxCount: 50);
            }

            if (sessions.Count == 0)
            {
                var fallbackNames = _personaStore.List()
                    .Where(n => !string.IsNullOrWhiteSpace(n) && !TraceMemoClient.IsSystemContact(null, n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .Select(n => new WeChatSessionSummary(n, n, n, n))
                    .ToList();
                sessions = fallbackNames;
            }

            UpdateSessionsUiAndUnreadBadge(sessions);

            string target = !string.IsNullOrWhiteSpace(ContactBox.Text)
                ? ContactBox.Text.Trim()
                : _currentChatContact;

            if (autoSelectActive && string.IsNullOrWhiteSpace(target) && sessions.Count > 0)
            {
                target = sessions[0].DisplayName;
                _currentChatContact = target;
                _isSyncingRecentSessionSelection = true;
                try
                {
                    ContactBox.Text = target;
                }
                finally
                {
                    _isSyncingRecentSessionSelection = false;
                }
            }

            SyncRecentSessionCombosSelection(target);
        }
        catch (Exception ex)
        {
            AppLog.Warn("OverlayWindow.Chat", "刷新最近会话列表异常", ex);
        }
    }

    private void UpdateSessionsUiAndUnreadBadge(IReadOnlyList<WeChatSessionSummary> sessions)
    {
        _isSyncingRecentSessionSelection = true;
        try
        {
            if (HubRecentSessionsCombo != null)
            {
                HubRecentSessionsCombo.ItemsSource = sessions;
            }

            int totalUnread = sessions.Sum(s => s.UnreadCount);
            if (HubUnreadBadge != null && HubUnreadBadgeText != null)
            {
                if (totalUnread > 0)
                {
                    HubUnreadBadge.Visibility = Visibility.Visible;
                    HubUnreadBadgeText.Text = $"🔴 {totalUnread}";
                }
                else
                {
                    HubUnreadBadge.Visibility = Visibility.Collapsed;
                }
            }

            if (HubUnreadList != null && RadarEmptyNotice != null)
            {
                var unreadItems = WeChatAnalyticsService.BuildUnreadRadar(sessions);
                HubUnreadList.ItemsSource = unreadItems;
                RadarEmptyNotice.Visibility = unreadItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        finally
        {
            _isSyncingRecentSessionSelection = false;
        }
    }

    private void SyncRecentSessionCombosSelection(string? contactName)
    {
        string trimmed = (contactName ?? string.Empty).Trim();
        bool prev = _isSyncingRecentSessionSelection;
        _isSyncingRecentSessionSelection = true;
        try
        {
            SyncSingleSessionCombo(HubRecentSessionsCombo, trimmed);
        }
        finally
        {
            _isSyncingRecentSessionSelection = prev;
        }
    }

    private void SyncSingleSessionCombo(ComboBox? combo, string contactName)
    {
        if (combo is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(contactName))
        {
            combo.SelectedIndex = -1;
            if (HubCurrentSessionText != null)
            {
                HubCurrentSessionText.Text = "正在自动识别会话...";
                HubCurrentSessionText.Visibility = Visibility.Visible;
            }
            return;
        }

        if (combo.ItemsSource is IEnumerable<WeChatSessionSummary> items)
        {
            var match = items.FirstOrDefault(s =>
                string.Equals(s.DisplayName, contactName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Remark, contactName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Nickname, contactName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Username, contactName, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                combo.SelectedItem = match;
                if (HubCurrentSessionText != null)
                {
                    HubCurrentSessionText.Text = match.DisplayName;
                    HubCurrentSessionText.Visibility = Visibility.Collapsed;
                }
                return;
            }
        }

        combo.SelectedIndex = -1;
        if (HubCurrentSessionText != null)
        {
            HubCurrentSessionText.Text = contactName;
            HubCurrentSessionText.Visibility = Visibility.Visible;
        }
    }

    private async Task AutoReadTickAsync()
    {
        if (!_autoRead || _reading)
        {
            return;
        }

        await ReadConversationAsync(silent: true, forceAutoDetect: false);
    }

    /// <summary>
    /// 多信号融合自动识别当前活跃微信会话：
    /// ① 微信独立单聊窗口标题；
    /// ② 微信主窗口顶部聊天标题栏视觉识别（结合轻量像素哈希与已知联系人对齐）；
    /// ③ WCDB4 会话数据库实时变动（未读清零代表用户刚点开该会话，或当前会话时间戳推进）。
    /// </summary>
    private async Task<(string? ContactName, string Reason)> DetectBestActiveContactAsync(
        IReadOnlyList<WeChatSessionSummary> sessions,
        IChatHistoryProvider historyProvider,
        bool forceAutoDetect)
    {
        var savedPersonas = _personaStore.List();
        var (candidates, canonicalMap) = WeChatHeaderDetector.BuildKnownContactCandidates(sessions, savedPersonas);

        string ResolveCanonical(string rawName)
        {
            string trimmed = rawName.Trim();
            return canonicalMap.TryGetValue(trimmed, out string? c) ? c : trimmed;
        }

        // 信号 1：当前前景窗口是否为微信独立聊天窗口 (ChatWnd)
        string? fgChatContact = WeChatHeaderDetector.TryGetForegroundChatWindowContact(candidates, _mySelfNickname);
        if (!string.IsNullOrWhiteSpace(fgChatContact))
        {
            string resolved = ResolveCanonical(fgChatContact);
            if (!TraceMemoClient.IsSystemContact(null, resolved))
            {
                bool isNew = !string.Equals(resolved, _lastAutoDetectedContact, StringComparison.OrdinalIgnoreCase);
                _lastAutoDetectedContact = resolved;
                if (forceAutoDetect || isNew)
                {
                    UpdateSessionSnapshots(sessions);
                    return (resolved, "独立聊天窗口");
                }
            }
        }

        // 信号 2：微信主窗口顶部标题栏视觉识别（像素未变时 0.1ms 直接返回，像素变化时 OCR 对齐已知好友）
        WindowBounds? overlayBounds = IsVisible && ActualWidth > 0 && ActualHeight > 0
            ? new WindowBounds((int)Left, (int)Top, (int)ActualWidth, (int)ActualHeight)
            : null;

        var (headerContact, headerChanged) = await _headerDetector.TryDetectMainWindowHeaderContactAsync(
            _tracker.GetCurrent(),
            overlayBounds,
            candidates,
            _mySelfNickname,
            forceOcr: forceAutoDetect);

        if (!string.IsNullOrWhiteSpace(headerContact))
        {
            string resolved = ResolveCanonical(headerContact);
            if (!TraceMemoClient.IsSystemContact(null, resolved))
            {
                bool isNew = headerChanged || !string.Equals(resolved, _lastAutoDetectedContact, StringComparison.OrdinalIgnoreCase);
                _lastAutoDetectedContact = resolved;
                if (forceAutoDetect || isNew)
                {
                    UpdateSessionSnapshots(sessions);
                    return (resolved, "微信主窗口当前会话");
                }
            }
        }

        // 信号 3：WCDB4 本地数据库会话状态增量检测（用户在微信中点开未读会话使未读清零，或在当前会话收发新消息）
        string? dbActivityContact = DetectDatabaseSessionDelta(sessions);
        if (!string.IsNullOrWhiteSpace(dbActivityContact))
        {
            // 若主窗口标题栏已明确识别为另一位好友，且变动会话仍有未读（说明是后台新进消息），不抢占当前焦点
            bool isBackgroundIncoming = !string.IsNullOrWhiteSpace(headerContact) &&
                                        !string.Equals(ResolveCanonical(headerContact), dbActivityContact, StringComparison.OrdinalIgnoreCase) &&
                                        sessions.Any(s => string.Equals(s.DisplayName, dbActivityContact, StringComparison.OrdinalIgnoreCase) && s.UnreadCount > 0);
            if (!isBackgroundIncoming)
            {
                _lastAutoDetectedContact = dbActivityContact;
                return (dbActivityContact, "实时消息同步");
            }
        }

        // 兜底：首次启动尚未选定任何好友，或用户主动点击刷新/自动识别
        if (forceAutoDetect || string.IsNullOrWhiteSpace(_currentChatContact))
        {
            string? fallback = !string.IsNullOrWhiteSpace(_lastAutoDetectedContact)
                ? _lastAutoDetectedContact
                : (sessions.Count > 0 ? sessions[0].DisplayName : await historyProvider.GetActiveContactNameAsync());

            if (!string.IsNullOrWhiteSpace(fallback) &&
                fallback != _mySelfNickname &&
                !TraceMemoClient.IsSystemContact(null, fallback))
            {
                _lastAutoDetectedContact = fallback;
                return (fallback, "最近活跃会话");
            }
        }

        return (null, string.Empty);
    }

    private string? DetectDatabaseSessionDelta(IReadOnlyList<WeChatSessionSummary> sessions)
    {
        if (sessions.Count == 0)
        {
            return null;
        }

        string? newlyActiveContact = null;
        if (_lastSessionSnapshots.Count > 0)
        {
            // 优先检查：原本有未读消息、刚刚在微信里被点开阅读（UnreadCount 从 >0 变为 0）的会话
            foreach (var s in sessions)
            {
                string key = !string.IsNullOrWhiteSpace(s.Username) ? s.Username : s.DisplayName;
                if (_lastSessionSnapshots.TryGetValue(key, out var prev))
                {
                    if (prev.UnreadCount > 0 && s.UnreadCount == 0)
                    {
                        newlyActiveContact = s.DisplayName;
                        break;
                    }
                }
            }

            // 其次检查：最新活跃时间刚推进且 UnreadCount == 0（用户正在该窗口内发送或阅读消息）
            if (newlyActiveContact is null)
            {
                foreach (var s in sessions.Take(10))
                {
                    string key = !string.IsNullOrWhiteSpace(s.Username) ? s.Username : s.DisplayName;
                    if (_lastSessionSnapshots.TryGetValue(key, out var prev))
                    {
                        if (s.LastActiveTime.HasValue &&
                            (!prev.LastActiveTime.HasValue || s.LastActiveTime.Value > prev.LastActiveTime.Value) &&
                            s.UnreadCount == 0)
                        {
                            newlyActiveContact = s.DisplayName;
                            break;
                        }
                    }
                }
            }
        }

        UpdateSessionSnapshots(sessions);
        return newlyActiveContact;
    }

    private void UpdateSessionSnapshots(IReadOnlyList<WeChatSessionSummary> sessions)
    {
        foreach (var s in sessions)
        {
            string key = !string.IsNullOrWhiteSpace(s.Username) ? s.Username : s.DisplayName;
            if (!string.IsNullOrWhiteSpace(key))
            {
                _lastSessionSnapshots[key] = (s.LastActiveTime, s.UnreadCount);
            }
        }
    }

    private async Task ReadConversationAsync(bool silent = false, bool forceAutoDetect = false)
    {
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
            var settings = _settingsStore.Load();
            if (WeChatDbKeyAndRootProvider.TryResolveAccountRoot(settings?.WeChatDbRoot, out string accRoot, out _, out _, out _))
            {
                ChatBubbleHelper.SetAccountRoot(accRoot);
            }

            using var historyProvider = CompositeChatHistoryProvider.CreateDefault(settings);

            var sessions = await historyProvider.GetRecentSessionsAsync(maxCount: 50);
            if (sessions.Count > 0)
            {
                UpdateSessionsUiAndUnreadBadge(sessions);
            }

            string contact = !string.IsNullOrWhiteSpace(ContactBox.Text) ? ContactBox.Text.Trim() : _currentChatContact;
            bool contactSwitchedByAuto = false;
            string autoReason = string.Empty;

            // 若开启了自动识别（或尚未选定联系人、或用户强制触发识别），执行多信号自动识别
            if (_autoSyncContact || string.IsNullOrEmpty(contact) || forceAutoDetect)
            {
                var (detected, reason) = await DetectBestActiveContactAsync(sessions, historyProvider, forceAutoDetect);
                if (!string.IsNullOrWhiteSpace(detected) &&
                    detected != _mySelfNickname &&
                    !TraceMemoClient.IsSystemContact(null, detected))
                {
                    if (!string.Equals(contact, detected, StringComparison.OrdinalIgnoreCase))
                    {
                        contactSwitchedByAuto = true;
                        autoReason = reason;
                        _lastSyncedMessageSignature = null;
                    }

                    contact = detected;
                    _currentChatContact = detected;

                    if (!string.Equals(ContactBox.Text.Trim(), detected, StringComparison.OrdinalIgnoreCase))
                    {
                        _isSyncingRecentSessionSelection = true;
                        try
                        {
                            ContactBox.Text = detected;
                        }
                        finally
                        {
                            _isSyncingRecentSessionSelection = false;
                        }
                    }
                }
            }

            SyncRecentSessionCombosSelection(contact);

            if (string.IsNullOrEmpty(contact))
            {
                if (!silent)
                {
                    StatusText.Text = "请先在上方【👤 会话】下拉框选择好友，或在【设置 -> 💬 微信】配置本地数据库";
                }
                return;
            }

            if (contactSwitchedByAuto)
            {
                StatusText.Text = $"⚡ 已自动识别当前会话：{contact} ({autoReason})";
                StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");
            }

            var history = await historyProvider.FetchHistoryAsync(contact);
            if (history.Success && history.Messages.Count > 0)
            {
                _currentHistoryMessages = history.Messages;
                var recent = history.Messages.TakeLast(25).Select(m => new ChatMessage(m.Role, m.Text, m.Timestamp)).ToList();
                var lastMsg = recent.LastOrDefault();
                string signature = $"{contact}|{history.Messages.Count}|{lastMsg?.Timestamp?.Ticks ?? 0}|{lastMsg?.Text}";

                if (!silent || contactSwitchedByAuto || _conversation.Count == 0 || signature != _lastSyncedMessageSignature)
                {
                    _lastSyncedMessageSignature = signature;
                    _conversation.Clear();
                    _conversation.Ingest(recent);
                    RefreshChatView();
                    RefreshHeatStats(history.Messages);
                    RefreshCommitments(history.Messages);
                    _ = Task.Run(() => _sqliteStorage.SaveMessages(contact, history.Messages));
                }

                if (!silent)
                {
                    StatusText.Text = $"已从 {history.SourceName} 同步「{contact}」最新 {recent.Count} 条记录";
                    StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");
                }
                return;
            }

            // 本地离线 SQLite 缓存兜底
            var cached = _sqliteStorage.GetMessages(contact, limit: 100);
            if (cached.Count > 0)
            {
                _currentHistoryMessages = cached;
                var recent = cached.TakeLast(25).Select(m => new ChatMessage(m.Role, m.Text, m.Timestamp)).ToList();
                var lastMsg = recent.LastOrDefault();
                string signature = $"{contact}|cached|{cached.Count}|{lastMsg?.Timestamp?.Ticks ?? 0}|{lastMsg?.Text}";

                if (!silent || contactSwitchedByAuto || _conversation.Count == 0 || signature != _lastSyncedMessageSignature)
                {
                    _lastSyncedMessageSignature = signature;
                    _conversation.Clear();
                    _conversation.Ingest(recent);
                    RefreshChatView();
                    RefreshHeatStats(cached);
                    RefreshCommitments(cached);
                }

                if (!silent)
                {
                    StatusText.Text = $"已从本地离线缓存加载「{contact}」最近 {recent.Count} 条记录";
                }
                return;
            }

            if (contactSwitchedByAuto)
            {
                _conversation.Clear();
                RefreshChatView();
            }

            if (!silent)
            {
                StatusText.Text = history.Error ?? $"未读取到「{contact}」的聊天记录，请检查【设置 -> 💬 微信】";
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                StatusText.Text = "读取对话失败：" + ex.Message;
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
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.App;

public partial class OverlayWindow : Window
{
    // ===================== VIEW 0: 对话 TAB & 微信最近会话下拉快选 =====================

    private async void ReadButton_Click(object sender, RoutedEventArgs e) => await ReadConversationAsync();

    private void AutoReadButton_Changed(object sender, RoutedEventArgs e)
    {
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
        RefreshChatView();
    }

    private async void RefreshSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRecentSessionsAsync(autoSelectActive: string.IsNullOrWhiteSpace(_currentChatContact));
    }

    private async Task ApplySelectedRecentSessionAsync(WeChatSessionSummary session, bool triggerChatRead)
    {
        string target = session.DisplayName.Trim();
        _currentChatContact = target;
        _autoSyncContact = false;

        _isSyncingRecentSessionSelection = true;
        try
        {
            ContactBox.Text = target;
            SyncRecentSessionCombosSelection(target);
            if (HubCurrentSessionText != null)
            {
                HubCurrentSessionText.Text = $"当前会话：{target}";
            }
        }
        finally
        {
            _isSyncingRecentSessionSelection = false;
        }

        StatusText.Text = $"已切换会话：{target}";
        StatusDot.Fill = (SolidColorBrush)FindResource("GreenBrush");

        if (triggerChatRead)
        {
            await ReadConversationAsync(silent: false);
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
                sessions = await provider.GetRecentSessionsAsync(maxCount: 40);
            }

            if (sessions.Count == 0)
            {
                // 若实时库尚未就绪，使用本地已保存画像联系人作为下拉候选兜底
                var fallbackNames = _personaStore.List()
                    .Where(n => !string.IsNullOrWhiteSpace(n) && !TraceMemoClient.IsSystemContact(null, n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(40)
                    .Select(n => new WeChatSessionSummary(n, n, n, n))
                    .ToList();
                sessions = fallbackNames;
            }

            _isSyncingRecentSessionSelection = true;
            try
            {
                if (HubRecentSessionsCombo != null)
                {
                    HubRecentSessionsCombo.ItemsSource = sessions;
                }

                // 更新中枢未读红点与未读雷达
                int totalUnread = sessions.Sum(s => s.UnreadCount);
                if (HubUnreadBadge != null && HubUnreadBadgeText != null)
                {
                    if (totalUnread > 0)
                    {
                        HubUnreadBadge.Visibility = Visibility.Visible;
                        HubUnreadBadgeText.Text = $"🔴 {totalUnread}未读";
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

                string target = !string.IsNullOrWhiteSpace(ContactBox.Text)
                    ? ContactBox.Text.Trim()
                    : _currentChatContact;

                if (autoSelectActive && string.IsNullOrWhiteSpace(target) && sessions.Count > 0)
                {
                    target = sessions[0].DisplayName;
                    _currentChatContact = target;
                    ContactBox.Text = target;
                }

                if (HubCurrentSessionText != null)
                {
                    HubCurrentSessionText.Text = string.IsNullOrWhiteSpace(target)
                        ? "当前会话：未选定"
                        : $"当前会话：{target}";
                }

                SyncRecentSessionCombosSelection(target);
            }
            finally
            {
                _isSyncingRecentSessionSelection = false;
            }
        }
        catch
        {
            // 静默忽略会话列表刷新异常
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

    private static void SyncSingleSessionCombo(ComboBox? combo, string contactName)
    {
        if (combo is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(contactName) || combo.ItemsSource is not IEnumerable<WeChatSessionSummary> items)
        {
            combo.SelectedIndex = -1;
            return;
        }

        var match = items.FirstOrDefault(s =>
            string.Equals(s.DisplayName, contactName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Remark, contactName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Nickname, contactName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.Username, contactName, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            combo.SelectedItem = match;
        }
        else
        {
            combo.SelectedIndex = -1;
        }
    }

    private async Task AutoReadTickAsync()
    {
        if (!_autoRead || _reading)
        {
            return;
        }

        await ReadConversationAsync(silent: true);
    }

    private async Task ReadConversationAsync(bool silent = false)
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
            using var historyProvider = CompositeChatHistoryProvider.CreateDefault(settings);

            var sessions = await historyProvider.GetRecentSessionsAsync(maxCount: 40);
            if (sessions.Count > 0)
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
                            HubUnreadBadgeText.Text = $"🔴 {totalUnread}未读";
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

            string contact = !string.IsNullOrWhiteSpace(ContactBox.Text) ? ContactBox.Text.Trim() : _currentChatContact;

            // 若开启了自动跟随（或尚未选定联系人），自动跟随最新活跃单聊会话
            if (string.IsNullOrEmpty(contact) || (silent && _autoSyncContact && sessions.Count > 0))
            {
                string? active = sessions.Count > 0
                    ? sessions[0].DisplayName
                    : await historyProvider.GetActiveContactNameAsync();

                if (!string.IsNullOrWhiteSpace(active) &&
                    active != _mySelfNickname &&
                    !TraceMemoClient.IsSystemContact(null, active))
                {
                    contact = active;
                    _currentChatContact = active;
                    if (string.IsNullOrWhiteSpace(ContactBox.Text) || _autoSyncContact)
                    {
                        ContactBox.Text = active;
                    }
                }
            }

            if (HubCurrentSessionText != null)
            {
                HubCurrentSessionText.Text = string.IsNullOrWhiteSpace(contact) ? "当前会话：未选定" : $"当前会话：{contact}";
            }

            SyncRecentSessionCombosSelection(contact);

            if (string.IsNullOrEmpty(contact))
            {
                if (!silent)
                {
                    StatusText.Text = "请先在上方【会话中枢】下拉框选择好友，或在【设置 -> 💬 微信】配置本地数据库";
                }
                return;
            }

            var history = await historyProvider.FetchHistoryAsync(contact);
            if (history.Success && history.Messages.Count > 0)
            {
                _currentHistoryMessages = history.Messages;
                var recent = history.Messages.TakeLast(25).Select(m => new ChatMessage(m.Role, m.Text)).ToList();
                _conversation.Clear();
                _conversation.Ingest(recent);
                RefreshChatView();
                RefreshHeatStats(history.Messages);
                RefreshCommitments(history.Messages);

                // 异步持久化至本地 SQLite 数据库
                _ = Task.Run(() => _sqliteStorage.SaveMessages(contact, history.Messages));

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
                var recent = cached.TakeLast(25).Select(m => new ChatMessage(m.Role, m.Text)).ToList();
                _conversation.Clear();
                _conversation.Ingest(recent);
                RefreshChatView();
                RefreshHeatStats(cached);
                RefreshCommitments(cached);

                if (!silent)
                {
                    StatusText.Text = $"已从本地离线缓存加载「{contact}」最近 {recent.Count} 条记录";
                }
                return;
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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

namespace WeChatCopilot.App;

public partial class OverlayWindow : Window
{
    // ===================== 顶部微信数据中枢 (会话 / 冷热 / 未读雷达 / 约定 / 检索) =====================

    private void HubToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool isExpanded = HubToggle.IsChecked == true;
        HubPanel.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        HubToggle.Content = isExpanded ? "会话中枢 ▴" : "会话中枢 ▾";

        if (isExpanded)
        {
            if (HubSubTabHeat.IsChecked == true)
            {
                RefreshHeatStats();
            }
            else if (HubSubTabCommitments.IsChecked == true)
            {
                RefreshCommitments();
            }
        }
    }

    private void HubSubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag)
        {
            return;
        }

        HubViewRecent.Visibility = Visibility.Collapsed;
        HubViewHeat.Visibility = Visibility.Collapsed;
        HubViewRadar.Visibility = Visibility.Collapsed;
        HubViewCommitments.Visibility = Visibility.Collapsed;
        HubViewSearch.Visibility = Visibility.Collapsed;

        switch (tag)
        {
            case "0":
                HubViewRecent.Visibility = Visibility.Visible;
                break;
            case "1":
                HubViewHeat.Visibility = Visibility.Visible;
                RefreshHeatStats();
                break;
            case "2":
                HubViewRadar.Visibility = Visibility.Visible;
                break;
            case "3":
                HubViewCommitments.Visibility = Visibility.Visible;
                RefreshCommitments();
                break;
            case "4":
                HubViewSearch.Visibility = Visibility.Visible;
                HubSearchBox.Focus();
                break;
        }
    }

    private async void HubRefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        HubRefreshBtn.IsEnabled = false;
        try
        {
            await RefreshRecentSessionsAsync(autoSelectActive: false);
            RefreshHeatStats();
            RefreshCommitments();
        }
        finally
        {
            HubRefreshBtn.IsEnabled = true;
        }
    }

    private async void HubRecentSessionsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingRecentSessionSelection)
        {
            return;
        }

        if (HubRecentSessionsCombo.SelectedItem is WeChatSessionSummary session &&
            !string.IsNullOrWhiteSpace(session.DisplayName))
        {
            await ApplySelectedRecentSessionAsync(session, triggerChatRead: true);
        }
    }

    private void HeatRefreshStatsBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshHeatStats();
    }

    private void RefreshHeatStats(IReadOnlyList<HistoryMessage>? messages = null)
    {
        string target = !string.IsNullOrWhiteSpace(ContactBox?.Text)
            ? ContactBox.Text.Trim()
            : (!string.IsNullOrWhiteSpace(_currentChatContact) ? _currentChatContact : "当前对象");

        HeatTargetNameText.Text = $"当前对象: {target}";

        var msgs = messages ?? _currentHistoryMessages;
        if (msgs.Count == 0 && !string.IsNullOrWhiteSpace(target))
        {
            // 尝试从本地离线 SQLite 中载入该联系人的消息进行分析
            try
            {
                var cached = _sqliteStorage.GetMessages(target, limit: 100);
                if (cached.Count > 0)
                {
                    msgs = cached;
                    _currentHistoryMessages = cached;
                }
            }
            catch
            {
                // 忽略异常
            }
        }

        var stats = WeChatAnalyticsService.AnalyzeRelationship(target, msgs);
        HeatLevelBadgeText.Text = stats.TemperatureLevel;
        HeatTotalMsgText.Text = stats.TotalMessages.ToString();
        HeatAvgReplyText.Text = stats.AverageResponseMinutes > 0 ? $"{stats.AverageResponseMinutes} min" : "--";
        HeatLateNightText.Text = $"{stats.LateNightCount} 次";
        HeatRatioText.Text = $"往来比例: 我方 {stats.OutgoingRatio}% vs 对方 {Math.Round(100.0 - stats.OutgoingRatio, 1)}% ({stats.OutgoingCount}条 / {stats.IncomingCount}条)";
        HeatAdviceText.Text = stats.SummaryAdvice;
    }

    private void RefreshCommitments(IReadOnlyList<HistoryMessage>? messages = null)
    {
        var msgs = messages ?? _currentHistoryMessages;
        if (msgs.Count == 0 && !string.IsNullOrWhiteSpace(_currentChatContact))
        {
            try
            {
                var cached = _sqliteStorage.GetMessages(_currentChatContact, limit: 100);
                if (cached.Count > 0)
                {
                    msgs = cached;
                    _currentHistoryMessages = cached;
                }
            }
            catch
            {
                // 忽略异常
            }
        }

        var commitments = WeChatAnalyticsService.ExtractCommitments(msgs);
        HubCommitmentsList.ItemsSource = commitments;
        CommitmentEmptyNotice.Visibility = commitments.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void UnreadItemJumpBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string targetName && !string.IsNullOrWhiteSpace(targetName))
        {
            string target = targetName.Trim();
            WeChatSessionSummary? match = null;
            if (HubRecentSessionsCombo.ItemsSource is IEnumerable<WeChatSessionSummary> sessions)
            {
                match = sessions.FirstOrDefault(s =>
                    string.Equals(s.DisplayName, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Remark, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Nickname, target, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Username, target, StringComparison.OrdinalIgnoreCase));
            }

            if (match != null)
            {
                await ApplySelectedRecentSessionAsync(match, triggerChatRead: true);
            }
            else
            {
                ContactBox.Text = target;
                _currentChatContact = target;
                await ReadConversationAsync(silent: false);
            }

            // 切换回会话面板
            HubSubTabRecent.IsChecked = true;
            HubSubTab_Click(HubSubTabRecent, new RoutedEventArgs());
        }
    }

    private void HubSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteHubSearch();
        }
    }

    private void HubSearchBtn_Click(object sender, RoutedEventArgs e)
    {
        ExecuteHubSearch();
    }

    private void ExecuteHubSearch()
    {
        string keyword = HubSearchBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keyword))
        {
            SearchEmptyNotice.Text = "请输入关键词后点击检索。";
            SearchEmptyNotice.Visibility = Visibility.Visible;
            HubSearchResultsList.ItemsSource = null;
            return;
        }

        string currentContact = !string.IsNullOrWhiteSpace(_currentChatContact) ? _currentChatContact : "当前会话";
        var msgs = _currentHistoryMessages;
        if (msgs.Count == 0 && !string.IsNullOrWhiteSpace(_currentChatContact))
        {
            try
            {
                var cached = _sqliteStorage.GetMessages(_currentChatContact, limit: 200);
                if (cached.Count > 0)
                {
                    msgs = cached;
                    _currentHistoryMessages = cached;
                }
            }
            catch
            {
                // 忽略异常
            }
        }

        var results = WeChatAnalyticsService.SearchMessages(keyword, msgs, currentContact);
        HubSearchResultsList.ItemsSource = results;

        if (results.Count == 0)
        {
            SearchEmptyNotice.Text = $"未在当前会话记录中检索到包含「{keyword}」的消息。";
            SearchEmptyNotice.Visibility = Visibility.Visible;
        }
        else
        {
            SearchEmptyNotice.Visibility = Visibility.Collapsed;
        }
    }
}

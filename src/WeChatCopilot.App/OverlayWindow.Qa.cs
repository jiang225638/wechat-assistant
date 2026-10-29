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
    // ===================== VIEW 3: AI问答 TAB 业务逻辑 (Antigravity 风格全景对话军师与会话侧边栏) =====================

    private readonly QaSessionStore _qaSessionStore = new();
    private List<QaSession> _qaSessions = new();
    private QaSession? _currentQaSession;
    private readonly List<QaSessionMessage> _qaConversationHistory = new();
    private CancellationTokenSource? _qaCts;

    private void InitQaSessions()
    {
        _qaSessions = _qaSessionStore.LoadAll();
        if (_qaSessions.Count > 0)
        {
            _currentQaSession = _qaSessions.OrderByDescending(s => s.UpdatedAt).First();
            SwitchToSession(_currentQaSession);
        }
        else
        {
            CreateNewSession();
        }
    }

    private void UpdateQaContextDisplay()
    {
        string target = ContactBox.Text.Trim();
        if (string.IsNullOrEmpty(target))
        {
            target = !string.IsNullOrWhiteSpace(_currentQaSession?.ContactName)
                ? _currentQaSession.ContactName
                : (_historyContactName ?? "未指定联系人");
        }
        QaTargetContactText.Text = target;
        QaTargetContactText.ToolTip = $"当前问答目标联系人：{target}";

        int traitCount = 0;
        if (!string.IsNullOrEmpty(target))
        {
            var p = _personaStore.Load(target);
            traitCount = p?.Traits?.Count ?? 0;
        }
        QaPersonaBadgeText.Text = traitCount > 0 ? $"画像: {traitCount}项特质" : "无画像特质";
        QaPersonaBadgeText.ToolTip = traitCount > 0 ? $"已成功关联本地「{target}」的 {traitCount} 个多维特质" : "本地暂无该联系人的画像特质";

        int msgCount = 0;
        if (!string.IsNullOrEmpty(target))
        {
            msgCount = _history.Count > 0 && string.Equals(_historyContactName, target, StringComparison.OrdinalIgnoreCase)
                ? _history.Count
                : _sqliteStorage.GetMessageCount(target);
        }
        QaHistoryBadgeText.Text = msgCount > 0 ? $"记录: {msgCount}条" : "无聊天记录";
        QaHistoryBadgeText.ToolTip = msgCount > 0 ? $"已关联本地历史数据库中 {msgCount} 条真实聊天记录" : "本地暂无聊天记录";
    }

    public void OnThemeChanged()
    {
        if (!IsLoaded) return;
        if (_currentLoadedPersona != null)
        {
            var cleanTraits = PersonaDistiller.DeduplicateByDimension(_currentLoadedPersona.Traits, _history);
            var displaySkill = !string.IsNullOrWhiteSpace(_currentLoadedPersona.SkillId)
                ? (_skillStore.GetSkill(_currentLoadedPersona.SkillId) ?? _currentSkill)
                : _currentSkill;
            DrawRadarChart(cleanTraits, displaySkill);
        }
        else if (RadarCanvas != null && RadarCanvas.Children.Count > 0)
        {
            DrawRadarChart(null, _currentSkill);
        }

        if (QaHistorySidebarOverlay?.Visibility == Visibility.Visible)
        {
            RenderSessionList();
        }
    }

    private void SyncQaSessionForContact(string? contactName)
    {
        if (!IsLoaded) return;
        UpdateQaContextDisplay();

        if (string.IsNullOrWhiteSpace(contactName)) return;

        if (_qaSessions.Count == 0)
        {
            _qaSessions = _qaSessionStore.LoadAll();
        }

        if (_currentQaSession != null &&
            string.Equals(_currentQaSession.ContactName, contactName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var existingForContact = _qaSessions
            .Where(s => string.Equals(s.ContactName, contactName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefault();

        if (existingForContact != null)
        {
            SwitchToSession(existingForContact);
        }
        else if (_currentQaSession != null && _currentQaSession.Messages.Count == 0)
        {
            _currentQaSession.ContactName = contactName;
            _qaSessionStore.SaveAll(_qaSessions);
        }
        else
        {
            var newSession = new QaSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "新会话",
                ContactName = contactName,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                Messages = new()
            };
            _qaSessions.Insert(0, newSession);
            _qaSessionStore.SaveAll(_qaSessions);
            SwitchToSession(newSession);
        }
    }

    // ===================== 会话切换、侧边栏与生命周期管理 =====================

    private void SwitchToSession(QaSession session)
    {
        _qaCts?.Cancel();
        _currentQaSession = session;
        _qaConversationHistory.Clear();

        // 清理界面消息气泡，保留欢迎引导卡与思考中面板
        var toRemove = new List<UIElement>();
        foreach (UIElement child in QaMessagesPanel.Children)
        {
            if (child != QaWelcomeCard && child != QaThinkingPanel)
            {
                toRemove.Add(child);
            }
        }
        foreach (var el in toRemove)
        {
            QaMessagesPanel.Children.Remove(el);
        }

        if (session.Messages.Count > 0)
        {
            QaWelcomeCard.Visibility = Visibility.Collapsed;
            foreach (var msg in session.Messages)
            {
                _qaConversationHistory.Add(msg);
                if (msg.Role == MessageRole.Outgoing)
                {
                    AppendQaUserBubble(msg.Text, msg.Timestamp);
                }
                else
                {
                    AppendQaAiBubble(msg.Text, msg.GroundingInfo ?? "", msg.Timestamp);
                }
            }
        }
        else
        {
            QaWelcomeCard.Visibility = Visibility.Visible;
        }

        QaThinkingPanel.Visibility = Visibility.Collapsed;
        QaSendButton.IsEnabled = true;
        QaMessagesScrollViewer.ScrollToEnd();
    }

    private void CreateNewSession()
    {
        _qaCts?.Cancel();
        string target = ContactBox.Text.Trim();
        if (string.IsNullOrEmpty(target))
        {
            target = _historyContactName ?? "当前联系人";
        }

        var newSession = new QaSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "新会话",
            ContactName = target,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            Messages = new()
        };

        _qaSessions.Insert(0, newSession);
        _qaSessionStore.SaveAll(_qaSessions);

        SwitchToSession(newSession);
        CloseQaSidebar();
        QaInputBox.Focus();
    }

    private void DeleteSession(QaSession session)
    {
        _qaSessions.RemoveAll(s => s.Id == session.Id);
        _qaSessionStore.SaveAll(_qaSessions);

        if (_currentQaSession?.Id == session.Id)
        {
            if (_qaSessions.Count > 0)
            {
                SwitchToSession(_qaSessions[0]);
            }
            else
            {
                CreateNewSession();
            }
        }

        RenderSessionList();
    }

    private void RenderSessionList()
    {
        QaSessionListPanel.Children.Clear();
        QaSessionCountText.Text = $"({_qaSessions.Count})";

        if (_qaSessions.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "暂无历史会话\n点击上方“新建会话”开启对话",
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 11.5,
                TextAlignment = TextAlignment.Center,
                LineHeight = 18,
                Margin = new Thickness(0, 30, 0, 0),
                Opacity = 0.6
            };
            QaSessionListPanel.Children.Add(emptyText);
            return;
        }

        foreach (var session in _qaSessions.OrderByDescending(s => s.UpdatedAt))
        {
            bool isCurrent = session.Id == _currentQaSession?.Id;

            var card = new Border
            {
                BorderThickness = new Thickness(isCurrent ? 1.5 : 1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 8, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                Tag = session
            };
            card.SetResourceReference(Border.BackgroundProperty, isCurrent ? "SubBorderCheckedBrush" : "CardSubtleBgBrush");
            card.SetResourceReference(Border.BorderBrushProperty, isCurrent ? "AccentBrush" : "BorderSubtleBrush");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            var titleText = new TextBlock
            {
                Text = session.Title,
                FontSize = 11.5,
                FontWeight = isCurrent ? FontWeights.Bold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 3)
            };
            titleText.SetResourceReference(TextBlock.ForegroundProperty, isCurrent ? "AccentBrush" : "TextPrimaryBrush");
            infoPanel.Children.Add(titleText);

            var subPanel = new StackPanel { Orientation = Orientation.Horizontal };
            if (!string.IsNullOrWhiteSpace(session.ContactName))
            {
                var contactBadge = new TextBlock
                {
                    Text = $"👤 {session.ContactName}",
                    FontSize = 10,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                contactBadge.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                subPanel.Children.Add(contactBadge);
            }

            var timeText = new TextBlock
            {
                Text = $"{session.UpdatedAt:MM-dd HH:mm} · {session.Messages.Count}条",
                FontSize = 10,
                Opacity = 0.8
            };
            timeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            subPanel.Children.Add(timeText);

            infoPanel.Children.Add(subPanel);
            Grid.SetColumn(infoPanel, 0);
            grid.Children.Add(infoPanel);

            var delBtn = new Button
            {
                Content = "🗑",
                Height = 22,
                Width = 24,
                Padding = new Thickness(0),
                FontSize = 10.5,
                ToolTip = "删除此会话记录",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Tag = session
            };
            delBtn.SetResourceReference(FrameworkElement.StyleProperty, "FluentButton");
            delBtn.Click += (s, e) =>
            {
                e.Handled = true;
                DeleteSession(session);
            };
            Grid.SetColumn(delBtn, 1);
            grid.Children.Add(delBtn);

            card.Child = grid;

            card.MouseDown += (s, e) =>
            {
                SwitchToSession(session);
                CloseQaSidebar();
            };

            QaSessionListPanel.Children.Add(card);
        }
    }

    private void QaToggleHistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        if (QaHistorySidebarOverlay.Visibility == Visibility.Visible)
        {
            CloseQaSidebar();
        }
        else
        {
            OpenQaSidebar();
        }
    }

    private void OpenQaSidebar()
    {
        QaHistorySidebarOverlay.Visibility = Visibility.Visible;
        RenderSessionList();
    }

    private void CloseQaSidebar()
    {
        QaHistorySidebarOverlay.Visibility = Visibility.Collapsed;
    }

    private void QaCloseSidebarBtn_Click(object sender, RoutedEventArgs e) => CloseQaSidebar();

    private void QaSidebarBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseQaSidebar();

    private void QaSidebarNewSessionBtn_Click(object sender, RoutedEventArgs e) => CreateNewSession();

    private void QaInputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool hasText = !string.IsNullOrWhiteSpace(QaInputBox.Text);
        if (QaInputPlaceholder != null)
        {
            QaInputPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        }
        if (QaSendButton != null)
        {
            QaSendButton.Tag = hasText ? "Active" : "Inactive";
        }
    }

    private void QaPromptPill_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string prompt && !string.IsNullOrWhiteSpace(prompt))
        {
            QaInputBox.Text = prompt;
            SendQaQuestion(prompt);
        }
    }

    private void QaInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            string text = QaInputBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                SendQaQuestion(text);
            }
        }
    }

    private void QaSendButton_Click(object sender, RoutedEventArgs e)
    {
        string text = QaInputBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            SendQaQuestion(text);
        }
    }

    private void QaStopButton_Click(object sender, RoutedEventArgs e)
    {
        _qaCts?.Cancel();
    }

    private void AppendQaUserBubble(string text, DateTime? timestamp = null)
    {
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(35, 4, 0, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10, 10, 2, 10),
            Padding = new Thickness(12, 8, 12, 8)
        };
        border.SetResourceReference(Border.BackgroundProperty, "ChatOutgoingBgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ChatOutgoingBorderBrush");

        var panel = new StackPanel();

        var header = new TextBlock
        {
            Text = $"🧑 我 · {(timestamp ?? DateTime.Now):HH:mm}",
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 4)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "ChatOutgoingBadgeBrush");
        panel.Children.Add(header);

        var content = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };
        content.SetResourceReference(TextBlock.ForegroundProperty, "ChatOutgoingTextBrush");
        panel.Children.Add(content);

        border.Child = panel;

        int insertIndex = QaMessagesPanel.Children.IndexOf(QaThinkingPanel);
        if (insertIndex >= 0)
        {
            QaMessagesPanel.Children.Insert(insertIndex, border);
        }
        else
        {
            QaMessagesPanel.Children.Add(border);
        }
    }

    private void AppendQaAiBubble(string text, string groundingInfo, DateTime? timestamp = null)
    {
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 15, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10, 10, 10, 2),
            Padding = new Thickness(12, 10, 12, 10)
        };
        border.SetResourceReference(Border.BackgroundProperty, "ChatIncomingBgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ChatIncomingBorderBrush");

        var panel = new StackPanel();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"🤖 AI 对话军师 · {(timestamp ?? DateTime.Now):HH:mm}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var copyBtn = new Button
        {
            Content = "📋 复制",
            Height = 22,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        copyBtn.SetResourceReference(FrameworkElement.StyleProperty, "FluentButton");
        copyBtn.Click += (s, e) =>
        {
            Clipboard.SetText(text);
            copyBtn.Content = "✅ 已复制";
            _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => copyBtn.Content = "📋 复制"));
        };
        Grid.SetColumn(copyBtn, 1);
        grid.Children.Add(copyBtn);

        panel.Children.Add(grid);

        if (!string.IsNullOrWhiteSpace(groundingInfo))
        {
            var badge = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            badge.SetResourceReference(Border.BackgroundProperty, "IntentFlowBgBrush");
            badge.SetResourceReference(Border.BorderBrushProperty, "IntentFlowBorderBrush");

            var badgeText = new TextBlock
            {
                Text = $"🔗 {groundingInfo}",
                FontSize = 10.5
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "IntentFlowTextBrush");
            badge.Child = badgeText;
            panel.Children.Add(badge);
        }

        // 使用 Markdown 富文本结构化预览渲染（标题、引用卡片、加粗、话术一键复制等）
        var mdView = MarkdownRenderer.Render(text, this);
        panel.Children.Add(mdView);

        border.Child = panel;

        int insertIndex = QaMessagesPanel.Children.IndexOf(QaThinkingPanel);
        if (insertIndex >= 0)
        {
            QaMessagesPanel.Children.Insert(insertIndex, border);
        }
        else
        {
            QaMessagesPanel.Children.Add(border);
        }
    }

    private (Border bubble, Action<string, bool> updateContent, Action<string> finalizeContent) CreateStreamingQaAiBubble(
        string groundingInfo,
        DateTime? timestamp = null)
    {
        var border = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 4, 15, 10),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10, 10, 10, 2),
            Padding = new Thickness(12, 10, 12, 10)
        };
        border.SetResourceReference(Border.BackgroundProperty, "ChatIncomingBgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "ChatIncomingBorderBrush");

        var panel = new StackPanel();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = $"🤖 AI 对话军师 · {(timestamp ?? DateTime.Now):HH:mm}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        Grid.SetColumn(title, 0);
        grid.Children.Add(title);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var stopBtn = new Button
        {
            Content = "⏹ 停止",
            Height = 22,
            Padding = new Thickness(6, 0, 6, 0),
            FontSize = 10,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "停止本次生成"
        };
        stopBtn.SetResourceReference(FrameworkElement.StyleProperty, "FluentButton");
        stopBtn.Click += (s, e) => _qaCts?.Cancel();
        btnPanel.Children.Add(stopBtn);

        string currentFullText = "";
        var copyBtn = new Button
        {
            Content = "📋 复制",
            Height = 22,
            Padding = new Thickness(8, 0, 8, 0),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center
        };
        copyBtn.SetResourceReference(FrameworkElement.StyleProperty, "FluentButton");
        copyBtn.Click += (s, e) =>
        {
            if (!string.IsNullOrEmpty(currentFullText))
            {
                try
                {
                    Clipboard.SetText(currentFullText);
                    copyBtn.Content = "✅ 已复制";
                    _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => copyBtn.Content = "📋 复制"));
                }
                catch
                {
                }
            }
        };
        btnPanel.Children.Add(copyBtn);
        Grid.SetColumn(btnPanel, 1);
        grid.Children.Add(btnPanel);

        panel.Children.Add(grid);

        if (!string.IsNullOrWhiteSpace(groundingInfo))
        {
            var badge = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            badge.SetResourceReference(Border.BackgroundProperty, "IntentFlowBgBrush");
            badge.SetResourceReference(Border.BorderBrushProperty, "IntentFlowBorderBrush");

            var badgeText = new TextBlock
            {
                Text = $"🔗 {groundingInfo}",
                FontSize = 10.5
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "IntentFlowTextBrush");
            badge.Child = badgeText;
            panel.Children.Add(badge);
        }

        var contentContainer = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(contentContainer);
        border.Child = panel;

        void UpdateContent(string text, bool isStreaming)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => UpdateContent(text, isStreaming));
                return;
            }
            currentFullText = text;
            string displayText = isStreaming ? text + " ▌" : text;
            contentContainer.Child = MarkdownRenderer.Render(displayText, this);
        }

        void FinalizeContent(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => FinalizeContent(text));
                return;
            }
            currentFullText = text;
            stopBtn.Visibility = Visibility.Collapsed;
            contentContainer.Child = MarkdownRenderer.Render(text, this);
        }

        return (border, UpdateContent, FinalizeContent);
    }

    private async void SendQaQuestion(string questionText)
    {
        if (string.IsNullOrWhiteSpace(questionText)) return;

        var settings = _settingsStore.Load();
        string apiKey = DpapiProtector.Unprotect(settings.EncryptedApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(
                "尚未配置 AI 接口的 API Key。\n请在【设置】->【AI设置】中填写 API Key 后再使用 AI 问答功能。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            MessageBox.Show(
                "AI 接口基址为空，请在【设置】->【AI设置】中配置有效接口。",
                "提示",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        string contact = ContactBox.Text.Trim();
        if (string.IsNullOrEmpty(contact))
        {
            contact = !string.IsNullOrWhiteSpace(_currentQaSession?.ContactName)
                ? _currentQaSession.ContactName
                : (_historyContactName ?? "当前联系人");
        }

        // 确保有活跃会话
        if (_currentQaSession == null)
        {
            CreateNewSession();
        }

        if (_currentQaSession!.Title == "新会话")
        {
            _currentQaSession.Title = questionText.Length > 16 ? questionText[..16] + "..." : questionText;
        }
        _currentQaSession.ContactName = contact;

        QaWelcomeCard.Visibility = Visibility.Collapsed;
        QaInputBox.Clear();

        // 1. 追加用户消息
        var userMsg = new QaSessionMessage(MessageRole.Outgoing, questionText, DateTime.Now);
        AppendQaUserBubble(questionText, userMsg.Timestamp);
        _currentQaSession.Messages.Add(userMsg);
        _currentQaSession.UpdatedAt = DateTime.Now;
        _qaConversationHistory.Add(userMsg);
        _qaSessionStore.SaveAll(_qaSessions);

        QaThinkingPanel.Visibility = Visibility.Visible;
        QaSendButton.IsEnabled = false;
        QaMessagesScrollViewer.ScrollToEnd();

        _qaCts?.Dispose();
        _qaCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(settings.TimeoutSeconds, 90)));

        bool bubbleInserted = false;
        var sbStreamed = new StringBuilder();
        string groundingInfo = "";
        Action<string>? finalizeContent = null;
        OpenAiCompatibleProvider? provider = null;
        AiRequest? req = null;

        try
        {
            // 2. 收集本地画像事实依据 (Grounding Persona)
            bool includePersona = QaIncludePersonaCheck.IsChecked == true;
            bool includeHistory = QaIncludeHistoryCheck.IsChecked == true;

            var sbGroundingPersona = new StringBuilder();
            int traitCount = 0;
            if (includePersona && !string.IsNullOrEmpty(contact))
            {
                var persona = _personaStore.Load(contact);
                if (persona != null && persona.Traits.Count > 0)
                {
                    traitCount = persona.Traits.Count;
                    sbGroundingPersona.AppendLine($"【联系人「{contact}」的本地心智特质画像】：");
                    if (!string.IsNullOrWhiteSpace(persona.UltimateGoal))
                    {
                        sbGroundingPersona.AppendLine($"- 既定互动终极目的：{persona.UltimateGoal}");
                    }
                    foreach (var t in persona.Traits.OrderByDescending(x => x.Score))
                    {
                        sbGroundingPersona.Append($"- 【{t.Dimension}】({t.ScoreInt}分/{t.ConfidenceText})：{t.Attribute}");
                        if (t.Evidence.Count > 0)
                        {
                            sbGroundingPersona.Append($"（原话印证：“{string.Join(" / ", t.Evidence.Take(2))}”）");
                        }
                        sbGroundingPersona.AppendLine();
                    }
                }
            }

            // 3. 收集近期真实聊天记录事实依据 (Grounding Chat History)
            var sbGroundingHistory = new StringBuilder();
            int historyCount = 0;
            if (includeHistory && !string.IsNullOrEmpty(contact))
            {
                var hist = _history.Count > 0 && string.Equals(_historyContactName, contact, StringComparison.OrdinalIgnoreCase)
                    ? _history.TakeLast(25).ToList()
                    : _sqliteStorage.GetMessages(contact, 25).ToList();

                historyCount = hist.Count;
                if (hist.Count > 0)
                {
                    sbGroundingHistory.AppendLine($"【联系人「{contact}」近期真实聊天记录（按时间先后）：】");
                    foreach (var m in hist)
                    {
                        string sender = m.Role == MessageRole.Outgoing ? "我" : contact;
                        string timeStr = m.Timestamp.HasValue ? $"[{m.Timestamp.Value:MM-dd HH:mm}] " : "";
                        sbGroundingHistory.AppendLine($"{timeStr}{sender}: {m.Text}");
                    }
                }
            }

            // 4. 构建多轮 Q&A 历史
            var sbQaHistory = new StringBuilder();
            if (_qaConversationHistory.Count > 1)
            {
                sbQaHistory.AppendLine("【此前问答对话上下文】：");
                foreach (var item in _qaConversationHistory.Take(_qaConversationHistory.Count - 1).TakeLast(6))
                {
                    string rName = item.Role == MessageRole.Outgoing ? "用户提问" : "军师回答";
                    sbQaHistory.AppendLine($"{rName}: {item.Text}");
                }
                sbQaHistory.AppendLine();
            }

            // 5. 提示词工程 (Antigravity Copilot Style + 当前激活 Skill 注入)
            string skillGuideline = _currentSkill.GetEffectiveAdviceGuideline();
            if (skillGuideline.Length > 1200)
            {
                skillGuideline = skillGuideline[..1200] + "…";
            }
            string systemPrompt = $"【当前激活技能体系（{_currentSkill.Icon} {_currentSkill.Name}）】：\n{skillGuideline}\n\n" +
$@"你是一位精通人情世故、人际心理学、潜台词洞悉与高情商沟通博弈的资深私人AI军师（Antigravity Copilot）。
用户正在与联系人【{contact}】进行沟通，并就现实互动向你寻求谋划和指导。
你有当前联系人的实名画像数据与聊天记录作为事实依据。

你的核心准则：
1. 【直奔本质，拒绝AI空话】：讲人情大白话，绝不使用“操作系统”、“自洽闭环”等空洞学术黑话。
2. 【严格依托事实依据】：深入结合其特质分数高低与亲口原话，精准点中对方的心理痛点和需求。
3. 【给出实战拿来即用的话术与行动方案】：当用户询问回复建议时，给出清晰、可直接复制的高情商示范，并简短点拨背后的心理机制。
4. 【结构清晰美观】：使用重点突出、条理分明的 Markdown 样式排版。";

            var sbUserPrompt = new StringBuilder();
            if (sbGroundingPersona.Length > 0)
            {
                sbUserPrompt.AppendLine(sbGroundingPersona.ToString());
            }
            if (sbGroundingHistory.Length > 0)
            {
                sbUserPrompt.AppendLine(sbGroundingHistory.ToString());
            }
            if (sbQaHistory.Length > 0)
            {
                sbUserPrompt.AppendLine(sbQaHistory.ToString());
            }

            sbUserPrompt.AppendLine($"【用户当下问题】：\n{questionText}");

            var groundingParts = new List<string> { $"技能({_currentSkill.Name})" };
            if (traitCount > 0) groundingParts.Add($"画像({traitCount}项特质)");
            if (historyCount > 0) groundingParts.Add($"真实历史({historyCount}条)");
            groundingInfo = groundingParts.Count > 0
                ? $"已融合「{contact}」{string.Join(" + ", groundingParts)}综合研判"
                : "";

            provider = new OpenAiCompatibleProvider(
                settings.Endpoint,
                () => apiKey,
                timeout: TimeSpan.FromSeconds(Math.Max(settings.TimeoutSeconds, 90)));

            req = new AiRequest(
                SystemPrompt: systemPrompt,
                UserPrompt: sbUserPrompt.ToString().Trim(),
                Model: settings.Model,
                Temperature: 0.7,
                MaxTokens: 2500);

            var (bubble, updateContent, finalizeAction) = CreateStreamingQaAiBubble(groundingInfo, DateTime.Now);
            finalizeContent = finalizeAction;
            var sw = Stopwatch.StartNew();
            long lastRenderMs = 0;

            await foreach (string chunk in provider.CompleteStreamAsync(req, _qaCts.Token))
            {
                if (string.IsNullOrEmpty(chunk)) continue;

                sbStreamed.Append(chunk);

                if (!bubbleInserted)
                {
                    bubbleInserted = true;
                    QaThinkingPanel.Visibility = Visibility.Collapsed;

                    int insertIndex = QaMessagesPanel.Children.IndexOf(QaThinkingPanel);
                    if (insertIndex >= 0)
                    {
                        QaMessagesPanel.Children.Insert(insertIndex, bubble);
                    }
                    else
                    {
                        QaMessagesPanel.Children.Add(bubble);
                    }

                    updateContent(sbStreamed.ToString(), true);
                    QaMessagesScrollViewer.ScrollToEnd();
                    lastRenderMs = sw.ElapsedMilliseconds;
                }
                else if (sw.ElapsedMilliseconds - lastRenderMs >= 50)
                {
                    updateContent(sbStreamed.ToString(), true);
                    QaMessagesScrollViewer.ScrollToEnd();
                    lastRenderMs = sw.ElapsedMilliseconds;
                }
            }

            if (bubbleInserted)
            {
                string finalText = sbStreamed.ToString().Trim();
                if (string.IsNullOrEmpty(finalText))
                {
                    finalizeContent("⚠️ AI 军师返回内容为空，请稍后重试。");
                }
                else
                {
                    finalizeContent(finalText);
                    var aiMsg = new QaSessionMessage(MessageRole.Incoming, finalText, DateTime.Now, groundingInfo);
                    _currentQaSession.Messages.Add(aiMsg);
                    _currentQaSession.UpdatedAt = DateTime.Now;
                    _qaConversationHistory.Add(aiMsg);
                    _qaSessionStore.SaveAll(_qaSessions);
                }
                QaMessagesScrollViewer.ScrollToEnd();
            }
            else
            {
                // 未产生任何分块，尝试非流式兜底
                AiReply reply = await provider.CompleteAsync(req, _qaCts.Token);
                if (reply.Success && !string.IsNullOrWhiteSpace(reply.Text))
                {
                    string replyText = reply.Text.Trim();
                    var aiMsg = new QaSessionMessage(MessageRole.Incoming, replyText, DateTime.Now, groundingInfo);
                    AppendQaAiBubble(replyText, groundingInfo, aiMsg.Timestamp);
                    _currentQaSession.Messages.Add(aiMsg);
                    _currentQaSession.UpdatedAt = DateTime.Now;
                    _qaConversationHistory.Add(aiMsg);
                    _qaSessionStore.SaveAll(_qaSessions);
                }
                else
                {
                    string errorText = reply.Error ?? "AI 军师返回内容为空，请稍后重试。";
                    AppendQaAiBubble($"⚠️ 回答生成失败：\n{errorText}", "");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (bubbleInserted && sbStreamed.Length > 0 && finalizeContent != null)
            {
                string partialText = sbStreamed.ToString().Trim() + "\n\n*(⏹ 已停止生成)*";
                finalizeContent(partialText);
                var aiMsg = new QaSessionMessage(MessageRole.Incoming, partialText, DateTime.Now, groundingInfo);
                _currentQaSession.Messages.Add(aiMsg);
                _currentQaSession.UpdatedAt = DateTime.Now;
                _qaConversationHistory.Add(aiMsg);
                _qaSessionStore.SaveAll(_qaSessions);
            }
            else
            {
                AppendQaAiBubble("⏹ 用户已停止本次思考生成。", "");
            }
        }
        catch (Exception ex)
        {
            if (bubbleInserted && sbStreamed.Length > 0 && finalizeContent != null)
            {
                string partialText = sbStreamed.ToString().Trim() + $"\n\n*(⚠️ 连接异常中断：{ex.Message})*";
                finalizeContent(partialText);
                var aiMsg = new QaSessionMessage(MessageRole.Incoming, partialText, DateTime.Now, groundingInfo);
                _currentQaSession.Messages.Add(aiMsg);
                _currentQaSession.UpdatedAt = DateTime.Now;
                _qaConversationHistory.Add(aiMsg);
                _qaSessionStore.SaveAll(_qaSessions);
            }
            else if (provider != null && req != null)
            {
                AppLog.Warn("OverlayWindow.Qa", "流式生成失败，尝试非流式兜底请求", ex);
                try
                {
                    AiReply reply = await provider.CompleteAsync(req, _qaCts.Token);
                    if (reply.Success && !string.IsNullOrWhiteSpace(reply.Text))
                    {
                        string replyText = reply.Text.Trim();
                        var aiMsg = new QaSessionMessage(MessageRole.Incoming, replyText, DateTime.Now, groundingInfo);
                        AppendQaAiBubble(replyText, groundingInfo, aiMsg.Timestamp);
                        _currentQaSession.Messages.Add(aiMsg);
                        _currentQaSession.UpdatedAt = DateTime.Now;
                        _qaConversationHistory.Add(aiMsg);
                        _qaSessionStore.SaveAll(_qaSessions);
                    }
                    else
                    {
                        string errorText = reply.Error ?? ex.Message;
                        AppendQaAiBubble($"⚠️ 回答生成失败：\n{errorText}", "");
                    }
                }
                catch (Exception fallbackEx)
                {
                    AppendQaAiBubble($"❌ 问答请求发生异常：\n{fallbackEx.Message}", "");
                }
            }
            else
            {
                AppendQaAiBubble($"❌ 问答请求发生异常：\n{ex.Message}", "");
            }
        }
        finally
        {
            provider?.Dispose();
            QaThinkingPanel.Visibility = Visibility.Collapsed;
            QaSendButton.IsEnabled = true;
            QaMessagesScrollViewer.ScrollToEnd();
        }
    }
}
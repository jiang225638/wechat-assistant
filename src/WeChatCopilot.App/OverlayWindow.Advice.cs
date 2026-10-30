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

        SyncQaSessionForContact(target);
    }

    private const int DefaultAdviceTimeoutSeconds = 120;
    private const int DefaultDistillTimeoutSeconds = 300;

    private static OpenAiCompatibleProvider CreateAiProvider(AiSettings settings, int minTimeoutSeconds)
    {
        string key = DpapiProtector.Unprotect(settings.EncryptedApiKey);
        int timeoutSec = Math.Max(settings.TimeoutSeconds, minTimeoutSeconds);
        return new OpenAiCompatibleProvider(settings.Endpoint, () => key, timeout: TimeSpan.FromSeconds(timeoutSec));
    }

    /// <summary>当对话缓冲为空时，自动尝试从内存历史、本地 SQLite 缓存或微信 DB 同步填充最近对话。</summary>
    private async Task<bool> EnsureConversationLoadedAsync()
    {
        if (_conversation.Count > 0)
        {
            return true;
        }

        if (_currentHistoryMessages.Count > 0)
        {
            var recentFromMem = _currentHistoryMessages.TakeLast(25)
                .Select(m => new ChatMessage(m.Role, m.Text, m.Timestamp))
                .ToList();
            _conversation.Ingest(recentFromMem);
            RefreshChatView();
            return _conversation.Count > 0;
        }

        string target = ContactBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            target = _currentChatContact;
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            var cached = _sqliteStorage.GetMessages(target, limit: 30);
            if (cached.Count > 0)
            {
                _currentHistoryMessages = cached;
                var recentFromDb = cached.TakeLast(25)
                    .Select(m => new ChatMessage(m.Role, m.Text, m.Timestamp))
                    .ToList();
                _conversation.Ingest(recentFromDb);
                RefreshChatView();
                return _conversation.Count > 0;
            }
        }

        await ReadConversationAsync(silent: true);
        return _conversation.Count > 0;
    }

    private AiRequest BuildCurrentAdviceRequest(
        AiSettings settings,
        out string targetContact,
        out DistillSkill activeSkill,
        out string targetStatement,
        out string? ultimateGoal,
        out int count)
    {
        count = CountBox.SelectedIndex == 1 ? 5 : 3;
        activeSkill = _currentSkill ?? DistillSkillPresets.Intimacy;

        targetContact = ContactBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetContact))
        {
            targetContact = _currentChatContact;
        }

        Persona? personaContext = null;
        if (IncludePersonaCheck.IsChecked == true && !string.IsNullOrEmpty(targetContact))
        {
            personaContext = _personaStore.Load(targetContact);
        }

        string userIntent = AiIntentBox.Text.Trim();
        ultimateGoal = personaContext?.UltimateGoal;

        var latestIncoming = _conversation.Messages.LastOrDefault(m => m.Role == MessageRole.Incoming)
            ?? _conversation.Messages.LastOrDefault();
        targetStatement = latestIncoming?.Text ?? string.Empty;

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

        // 根据最近 5 条对话内容与用户回复意图，动态按需路由当前 Skill 匹配的 references 子模块
        string contextQuery = string.Join(" ",
            _conversation.Messages.TakeLast(5).Select(m => m.Text)
            .Append(userIntent)
            .Append(ultimateGoal ?? string.Empty));
        string dynamicGuideline = activeSkill.GetGuidelineForQa(contextQuery, out string? routedAdviceModule);
        _lastRoutedAdviceModule = routedAdviceModule;
        DistillSkill routedSkill = !string.IsNullOrWhiteSpace(routedAdviceModule)
            ? activeSkill with { AdviceGuideline = dynamicGuideline }
            : activeSkill;

        return PromptBuilder.BuildUnifiedAdvice(
            settings,
            _conversation.Messages,
            count,
            personaContext,
            targetContact,
            relationship: null,
            routedSkill,
            userIntent: userIntent,
            ultimateGoal: ultimateGoal,
            contextCount: contextCount);
    }

    private string? _lastRoutedAdviceModule;

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adviceCts != null)
        {
            _adviceCts.Cancel();
            return;
        }

        if (!await EnsureConversationLoadedAsync())
        {
            SetAiStatus("对话缓冲为空，请先在上方选择会话或点击【刷新】同步聊天记录。", isError: true);
            return;
        }

        var settings = _settingsStore.Load();
        AiRequest req = BuildCurrentAdviceRequest(
            settings,
            out string target,
            out DistillSkill activeSkill,
            out string targetStatement,
            out string? ultimateGoal,
            out int count);

        string goalHint = !string.IsNullOrWhiteSpace(ultimateGoal) ? $"，战略导向【{ultimateGoal}】" : string.Empty;
        string routeHint = !string.IsNullOrWhiteSpace(_lastRoutedAdviceModule) ? $"（🧭 已挂载: {_lastRoutedAdviceModule}）" : string.Empty;
        SetAiStatus($"AI 正在结合【{activeSkill.Icon} {activeSkill.Name}】{routeHint}策略与对方画像{goalHint}，深度剖析意图并生成 {count} 条针对性回复建议...", isError: false);
        GenerateButton.Content = "⏹ 停止生成";
        GenerateButton.IsEnabled = true;

        try
        {
            using var provider = CreateAiProvider(settings, DefaultAdviceTimeoutSeconds);

            _adviceCts = new CancellationTokenSource();
            var sbStream = new StringBuilder();
            var sw = Stopwatch.StartNew();
            long lastUiUpdateMs = 0;

            try
            {
                await foreach (string chunk in provider.CompleteStreamAsync(req, _adviceCts.Token))
                {
                    if (string.IsNullOrEmpty(chunk)) continue;
                    sbStream.Append(chunk);

                    if (sw.ElapsedMilliseconds - lastUiUpdateMs >= 120)
                    {
                        lastUiUpdateMs = sw.ElapsedMilliseconds;
                        string currentRaw = sbStream.ToString();
                        string stageDesc = currentRaw.Contains("回复建议")
                            ? $"正在生成 {count} 条多风格实战回复建议..."
                            : currentRaw.Contains("评估维度")
                                ? "正在提炼动态博弈评估维度..."
                                : currentRaw.Contains("意图剖析") || currentRaw.Contains("潜台词")
                                    ? "正在穿透字面表象剖析潜台词与真实心理..."
                                    : "正在深度推演对话脉络与博弈策略...";
                        SetAiStatus($"AI 实时推演中（已接收 {currentRaw.Length} 字）：{stageDesc}", isError: false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception streamEx)
            {
                AppLog.Warn("OverlayWindow", "流式生成回复建议中断，自动回退非流式调用", streamEx);
            }

            string finalRaw = sbStream.ToString().Trim();
            if (string.IsNullOrWhiteSpace(finalRaw))
            {
                AiReply reply = await provider.CompleteAsync(req, _adviceCts.Token);
                if (!reply.Success)
                {
                    AiStatusBanner.Visibility = Visibility.Collapsed;
                    SetAiStatus("AI 生成失败：" + reply.Error, isError: true);
                    return;
                }
                finalRaw = reply.Text;
            }

            AiStatusBanner.Visibility = Visibility.Collapsed;
            var advice = AiOutputParser.ParseUnifiedAdvice(finalRaw, targetStatement);
            ShowUnifiedAdvice(advice, target, activeSkill);
        }
        catch (OperationCanceledException)
        {
            AiStatusBanner.Visibility = Visibility.Collapsed;
            SetAiStatus("⏹ 已取消本次回复建议生成。", isError: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("OverlayWindow", "生成回复建议异常", ex);
            AiStatusBanner.Visibility = Visibility.Collapsed;
            SetAiStatus("调用异常：" + ex.Message, isError: true);
        }
        finally
        {
            _adviceCts?.Dispose();
            _adviceCts = null;
            GenerateButton.IsEnabled = true;
            GenerateButton.Content = "🚀 智能分析回复建议";
        }
    }

    private async Task<UnifiedAiAdvice?> RegenerateAdviceFromDashboardAsync(CancellationToken ct)
    {
        if (!await EnsureConversationLoadedAsync()) return null;

        var settings = _settingsStore.Load();
        AiRequest req = BuildCurrentAdviceRequest(
            settings,
            out string target,
            out DistillSkill activeSkill,
            out string targetStatement,
            out _,
            out _);

        using var provider = CreateAiProvider(settings, DefaultAdviceTimeoutSeconds);
        AiReply reply = await provider.CompleteAsync(req, ct);
        if (!reply.Success || string.IsNullOrWhiteSpace(reply.Text))
        {
            return null;
        }

        var advice = AiOutputParser.ParseUnifiedAdvice(reply.Text, targetStatement);
        ShowUnifiedAdvice(advice, target, activeSkill);
        return advice;
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
            AiAppliedSkillText.Text = !string.IsNullOrWhiteSpace(_lastRoutedAdviceModule)
                ? $"{skill.Icon} {skill.Name} · {_lastRoutedAdviceModule}"
                : $"{skill.Icon} {skill.Name}";
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

        if (advice.Suggestions.Count == 0)
        {
            var fallbackList = new List<ReplySuggestion>();
            if (!string.IsNullOrWhiteSpace(advice.Strategy))
            {
                fallbackList.Add(new ReplySuggestion("策略破局", advice.Strategy, "依据当前战局破局心法自动提炼"));
            }
            else
            {
                fallbackList.Add(new ReplySuggestion("重新生成", "本次模型回复未生成明确的候选，请点击上方「智能分析回复建议」重试或在输入框微调意图。", "建议微调回复意图重新推演"));
            }
            SuggestionCards.ItemsSource = fallbackList;
        }
        else
        {
            SuggestionCards.ItemsSource = advice.Suggestions;
        }
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
            AiStatusBanner.SetResourceReference(Border.BackgroundProperty, "StatusErrorBgBrush");
            AiStatusBanner.SetResourceReference(Border.BorderBrushProperty, "StatusErrorBorderBrush");
            AiStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorBrush");
            AiStatusDismissBtn.Visibility = Visibility.Visible;
        }
        else
        {
            AiStatusProgress.Visibility = Visibility.Visible;
            AiStatusSpinner.Visibility = Visibility.Visible;
            AiStatusIcon.Visibility = Visibility.Collapsed;
            AiStatusBanner.SetResourceReference(Border.BackgroundProperty, "StatusBusyBgBrush");
            AiStatusBanner.SetResourceReference(Border.BorderBrushProperty, "StatusBusyBorderBrush");
            AiStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            AiStatusDismissBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void AiStatusDismiss_Click(object sender, RoutedEventArgs e)
    {
        AiStatusBanner.Visibility = Visibility.Collapsed;
    }

    private async void PasteSuggestionToWeChat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string text)
        {
            try
            {
                Clipboard.SetText(text);
                bool pasted = await WindowPositioner.TryPasteToWeChatInputAsync(_tracker.GetCurrent());
                btn.Content = pasted ? "✔ 已填入" : "✔ 已复制";
                btn.Foreground = (SolidColorBrush)FindResource("GreenBrush");
                await Task.Delay(1500);
                btn.Content = "📤 填入";
                btn.ClearValue(ForegroundProperty);
            }
            catch (Exception ex)
            {
                AppLog.Warn("OverlayWindow", "填入建议至微信输入框失败", ex);
            }
        }
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
            catch (Exception ex)
            {
                AppLog.Warn("OverlayWindow", "复制建议至剪贴板失败", ex);
            }
        }
    }
}

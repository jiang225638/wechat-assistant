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

        return PromptBuilder.BuildUnifiedAdvice(
            settings,
            _conversation.Messages,
            count,
            personaContext,
            targetContact,
            relationship: null,
            activeSkill,
            userIntent: userIntent,
            ultimateGoal: ultimateGoal,
            contextCount: contextCount);
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adviceCts != null)
        {
            _adviceCts.Cancel();
            return;
        }

        if (_conversation.Count == 0)
        {
            SetAiStatus("对话缓冲为空，请先在「当前对话」点击【⚡ 读取当前对话】。", isError: true);
            return;
        }

        var settings = new SettingsStore().Load();
        AiRequest req = BuildCurrentAdviceRequest(
            settings,
            out string target,
            out DistillSkill activeSkill,
            out string targetStatement,
            out string? ultimateGoal,
            out int count);

        string goalHint = !string.IsNullOrWhiteSpace(ultimateGoal) ? $"，战略导向【{ultimateGoal}】" : string.Empty;
        SetAiStatus($"AI 正在结合【{activeSkill.Icon} {activeSkill.Name}】策略与对方画像{goalHint}，深度剖析意图并生成 {count} 条针对性回复建议...", isError: false);
        GenerateButton.Content = "⏹ 停止生成";
        GenerateButton.IsEnabled = true;

        try
        {
            using var provider = CreateAiProvider(settings, DefaultAdviceTimeoutSeconds);

            _adviceCts = new CancellationTokenSource();
            AiReply reply = await provider.CompleteAsync(req, _adviceCts.Token);
            AiStatusBanner.Visibility = Visibility.Collapsed;

            if (!reply.Success)
            {
                SetAiStatus("AI 生成失败：" + reply.Error, isError: true);
                return;
            }

            var advice = AiOutputParser.ParseUnifiedAdvice(reply.Text, targetStatement);
            ShowUnifiedAdvice(advice, target, activeSkill);
        }
        catch (OperationCanceledException)
        {
            AiStatusBanner.Visibility = Visibility.Collapsed;
            SetAiStatus("⏹ 已取消本次回复建议生成。", isError: true);
        }
        catch (Exception ex)
        {
            WeChatCopilot.Core.Diagnostics.AppLog.Error("OverlayWindow", "生成回复建议异常", ex);
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
        if (_conversation.Count == 0) return null;

        var settings = new SettingsStore().Load();
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
            catch
            {
                // ignored
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
            catch
            {
                // ignored
            }
        }
    }
}

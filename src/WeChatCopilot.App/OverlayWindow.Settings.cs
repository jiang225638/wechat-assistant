using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
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
    // ===================== VIEW 4: 设置 TAB 业务逻辑 =====================

    private void LoadSettingsView()
    {
        var s = _settingsStore.Load();
        SettingEndpointBox.Text = s.Endpoint;
        SettingModelBox.Text = s.Model;
        SettingTempBox.Text = s.Temperature.ToString(CultureInfo.InvariantCulture);
        SettingTimeoutBox.Text = s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        SettingKeyBox.Password = DpapiProtector.Unprotect(s.EncryptedApiKey);
        if (SettingWeChatDbRootBox != null)
        {
            SettingWeChatDbRootBox.Text = s.WeChatDbRoot ?? string.Empty;
        }

        string savedDbKey = DpapiProtector.Unprotect(s.EncryptedWeChatDbKey);
        if (SettingWeChatDbKeyBox != null)
        {
            SettingWeChatDbKeyBox.Text = savedDbKey;
        }

        // 若尚未保存目录或密钥，自动嗅探本机并预填到输入框，方便用户直观查看
        if (string.IsNullOrWhiteSpace(savedDbKey) || string.IsNullOrWhiteSpace(s.WeChatDbRoot))
        {
            if (WeChatDbKeyAndRootProvider.TryResolveConfiguration(s, out string autoRoot, out _, out _, out string autoKey, out _))
            {
                if (SettingWeChatDbKeyBox != null && string.IsNullOrWhiteSpace(SettingWeChatDbKeyBox.Text) && !string.IsNullOrWhiteSpace(autoKey))
                {
                    SettingWeChatDbKeyBox.Text = autoKey;
                }
                if (SettingWeChatDbRootBox != null && string.IsNullOrWhiteSpace(SettingWeChatDbRootBox.Text) && !string.IsNullOrWhiteSpace(autoRoot))
                {
                    SettingWeChatDbRootBox.Text = autoRoot;
                }
            }
        }

        SettingSelfNicknameBox.Text = _mySelfNickname ?? string.Empty;

        // 外观主题
        string currentTheme = s.ThemeMode ?? "Light";
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

        RefreshSettingsSkillList();
        RefreshSettingsPersonaGoalsList();
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
            MainTabControl.SelectedIndex = 4;
        }
        SwitchSettingSubTab(3);
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
        if (SettingViewAi == null || SettingViewWeChat == null || SettingViewSkill == null || SettingViewGoals == null || SettingViewPref == null || SettingViewHelp == null)
        {
            return;
        }

        SettingViewAi.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewWeChat.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewSkill.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewGoals.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewPref.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        SettingViewHelp.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 0 && SettingSubTabAi != null) SettingSubTabAi.IsChecked = true;
        else if (index == 1 && SettingSubTabWeChat != null) SettingSubTabWeChat.IsChecked = true;
        else if (index == 2 && SettingSubTabSkill != null) SettingSubTabSkill.IsChecked = true;
        else if (index == 3 && SettingSubTabGoals != null)
        {
            SettingSubTabGoals.IsChecked = true;
            RefreshSettingsPersonaGoalsList();
        }
        else if (index == 4 && SettingSubTabPref != null) SettingSubTabPref.IsChecked = true;
        else if (index == 5 && SettingSubTabHelp != null) SettingSubTabHelp.IsChecked = true;
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

        var saved = existing with
        {
            Endpoint = SettingEndpointBox.Text.Trim(),
            Model = SettingModelBox.Text.Trim(),
            Temperature = temp,
            TimeoutSeconds = timeoutSec,
            EncryptedApiKey = encrypted
        };

        _settingsStore.Save(saved);

        ShowSettingTestResult(true, "AI 设置已保存", "AI 模型与接口配置已成功保存至本地。", 0);
    }

    private void CopyWeChatDbKeyButton_Click(object sender, RoutedEventArgs e)
    {
        string key = SettingWeChatDbKeyBox?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(key))
        {
            try
            {
                Clipboard.SetText(key);
                ShowWeChatTestResult(true, "密钥已复制到剪贴板", $"已成功复制 64 位数据库密钥至系统剪贴板：\n{key}", 0);
            }
            catch (Exception ex)
            {
                ShowWeChatTestResult(false, "复制剪贴板失败", ex.Message, 0);
            }
        }
        else
        {
            ShowWeChatTestResult(false, "密钥为空", "当前输入框中尚无密钥，请先点击【🔍 自动探测本机配置】自动嗅探提取。", 0);
        }
    }

    private void SettingSaveWeChatDbButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = _settingsStore.Load();

        string keyInput = SettingWeChatDbKeyBox?.Text?.Trim() ?? string.Empty;
        string encryptedDbKey = !string.IsNullOrEmpty(keyInput)
            ? DpapiProtector.Protect(keyInput)
            : existing.EncryptedWeChatDbKey;

        var saved = existing with
        {
            WeChatDbRoot = SettingWeChatDbRootBox?.Text.Trim() ?? existing.WeChatDbRoot,
            EncryptedWeChatDbKey = encryptedDbKey
        };

        _settingsStore.Save(saved);
        _ = RefreshRecentSessionsAsync(autoSelectActive: string.IsNullOrWhiteSpace(_currentChatContact));

        ShowWeChatTestResult(true, "本地微信设置已保存", "微信 4.x 数据库配置已加密保存至本地，最近会话列表已自动刷新。", 0);
    }

    private void ShowWeChatTestingStatus(string title, string detail)
    {
        if (SettingWeChatTestResultBorder == null) return;
        SettingWeChatTestResultBorder.Visibility = Visibility.Visible;
        SettingWeChatTestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        SettingWeChatTestStatusIcon.Text = "⏳";
        SettingWeChatTestStatusTitle.Text = title;
        SettingWeChatTestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        SettingWeChatTestLatencyText.Text = string.Empty;
        SettingWeChatTestDetailBox.Text = detail;
    }

    private void ShowWeChatTestResult(bool success, string title, string detail, long elapsedMs)
    {
        if (SettingWeChatTestResultBorder == null) return;
        SettingWeChatTestResultBorder.Visibility = Visibility.Visible;
        if (success)
        {
            SettingWeChatTestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            SettingWeChatTestStatusIcon.Text = "✅";
            SettingWeChatTestStatusTitle.Text = title;
            SettingWeChatTestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
            SettingWeChatTestLatencyText.Text = elapsedMs > 0 ? $"(耗时 {elapsedMs} ms)" : string.Empty;
        }
        else
        {
            SettingWeChatTestResultBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            SettingWeChatTestStatusIcon.Text = "❌";
            SettingWeChatTestStatusTitle.Text = title;
            SettingWeChatTestStatusTitle.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            SettingWeChatTestLatencyText.Text = elapsedMs > 0 ? $"(耗时 {elapsedMs} ms)" : string.Empty;
        }

        SettingWeChatTestDetailBox.Text = detail;
    }

    private void SettingAutoDetectWeChatDbButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = _settingsStore.Load();
        string keyInput = SettingWeChatDbKeyBox?.Text?.Trim() ?? string.Empty;
        var temp = new AiSettings
        {
            WeChatDbRoot = SettingWeChatDbRootBox?.Text.Trim() ?? string.Empty,
            EncryptedWeChatDbKey = !string.IsNullOrEmpty(keyInput)
                ? DpapiProtector.Protect(keyInput)
                : existing.EncryptedWeChatDbKey
        };

        var sw = Stopwatch.StartNew();
        if (WeChatDbKeyAndRootProvider.TryResolveConfiguration(temp, out string accountRoot, out _, out string wxid, out string rawKeyHex, out string? error))
        {
            sw.Stop();
            if (SettingWeChatDbRootBox != null)
            {
                SettingWeChatDbRootBox.Text = accountRoot;
            }
            if (SettingWeChatDbKeyBox != null)
            {
                SettingWeChatDbKeyBox.Text = rawKeyHex;
            }
            ShowWeChatTestResult(
                true,
                "已自动发现微信 4.x 账号与密钥",
                $"账号目录：{accountRoot}\n当前 wxid：{wxid}\n数据库密钥：{rawKeyHex}\n（已自动填入上方输入框，可一键复制或点击【⚡ 测试微信 4.x 直连】）",
                sw.ElapsedMilliseconds);
        }
        else
        {
            sw.Stop();
            string discoveredRoot = WeChatDbKeyAndRootProvider.TryDiscoverDefaultAccountRoot(out string autoRoot, out _, out _, out _)
                ? autoRoot
                : "未检测到";
            if (SettingWeChatDbRootBox != null && string.IsNullOrWhiteSpace(SettingWeChatDbRootBox.Text) && discoveredRoot != "未检测到")
            {
                SettingWeChatDbRootBox.Text = discoveredRoot;
            }
            ShowWeChatTestResult(
                false,
                "自动探测未完成",
                $"探测到的账号目录：{discoveredRoot}\n原因：{error}",
                sw.ElapsedMilliseconds);
        }
    }

    private async void SettingTestWeChatDbButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = _settingsStore.Load();
        string keyInput = SettingWeChatDbKeyBox?.Text?.Trim() ?? string.Empty;
        var temp = new AiSettings
        {
            WeChatDbRoot = SettingWeChatDbRootBox?.Text.Trim() ?? existing.WeChatDbRoot,
            EncryptedWeChatDbKey = !string.IsNullOrEmpty(keyInput)
                ? DpapiProtector.Protect(keyInput)
                : existing.EncryptedWeChatDbKey,
            NativeWcdbDllDir = existing.NativeWcdbDllDir
        };

        if (SettingTestWeChatDbButton != null)
        {
            SettingTestWeChatDbButton.IsEnabled = false;
            SettingTestWeChatDbButton.Content = "⏳ 直连测试中...";
        }

        ShowWeChatTestingStatus("正在测试微信 4.x 数据库直连...", "正在使用 WCDB4 原生引擎建立只读游标连接并检验联系人与消息...");

        var sw = Stopwatch.StartNew();
        try
        {
            using var provider = new Wcdb4HistoryProvider(temp);
            var status = await provider.TestConnectionAsync();
            sw.Stop();

            if (status.Success)
            {
                if (WeChatDbKeyAndRootProvider.TryResolveConfiguration(temp, out string resolvedRoot, out _, out _, out string resolvedKey, out _))
                {
                    if (SettingWeChatDbRootBox != null && string.IsNullOrWhiteSpace(SettingWeChatDbRootBox.Text))
                    {
                        SettingWeChatDbRootBox.Text = resolvedRoot;
                    }
                    if (SettingWeChatDbKeyBox != null && string.IsNullOrWhiteSpace(SettingWeChatDbKeyBox.Text) && !string.IsNullOrWhiteSpace(resolvedKey))
                    {
                        SettingWeChatDbKeyBox.Text = resolvedKey;
                    }
                }
                else if (SettingWeChatDbRootBox != null && string.IsNullOrWhiteSpace(SettingWeChatDbRootBox.Text))
                {
                    SettingWeChatDbRootBox.Text = status.AccountRoot;
                }
                ShowWeChatTestResult(
                    true,
                    "微信 4.x 本地数据库直连成功！",
                    $"{status.Message}\n数据源：{status.SourceName}\n账号目录：{status.AccountRoot}",
                    sw.ElapsedMilliseconds);
            }
            else
            {
                ShowWeChatTestResult(
                    false,
                    "微信 4.x 本地数据库直连失败",
                    $"错误详情：{status.Message}\n账号目录：{(string.IsNullOrEmpty(status.AccountRoot) ? "未定位" : status.AccountRoot)}",
                    sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            ShowWeChatTestResult(false, "直连测试异常", ex.Message, sw.ElapsedMilliseconds);
        }
        finally
        {
            if (SettingTestWeChatDbButton != null)
            {
                SettingTestWeChatDbButton.IsEnabled = true;
                SettingTestWeChatDbButton.Content = "⚡ 测试微信 4.x 直连";
            }
        }
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
        RefreshSettingsPersonaGoalsList();
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
            var s = _settingsStore.Load();
            if (Math.Abs(s.WindowOpacity - opacity) > 0.005)
            {
                s.WindowOpacity = opacity;
                _settingsStore.Save(s);
            }
        }
    }

    private void SettingSelfNicknameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        string text = SettingSelfNicknameBox.Text.Trim();
        _mySelfNickname = string.IsNullOrEmpty(text) ? null : text;
        var s = _settingsStore.Load();
        if (s.SelfNickname != _mySelfNickname)
        {
            s.SelfNickname = _mySelfNickname;
            _settingsStore.Save(s);
        }
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

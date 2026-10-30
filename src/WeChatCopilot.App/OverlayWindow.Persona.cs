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
    // ===================== VIEW 2: 人格画像 TAB (FR-3/FR-8 闭环) =====================

    private void ContactBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingPersonaSelection)
        {
            return;
        }

        string name = ContactBox.Text.Trim();
        if (_autoSyncContact &&
            !_isSyncingRecentSessionSelection &&
            !string.Equals(name, _currentChatContact, StringComparison.OrdinalIgnoreCase))
        {
            _autoSyncContact = false;
            UpdateAutoSyncToggleUi();
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
            SyncRecentSessionCombosSelection(name);
            UpdateAiTargetInfo();
        }
        else
        {
            _currentLoadedPersona = null;
            PersonaScroll.Visibility = Visibility.Collapsed;
            PersonaEmptyCard.Visibility = Visibility.Visible;
            PersonaEmptyTitle.Text = "请选择或输入联系人";
            RadarCanvas.Children.Clear();
            SyncRecentSessionCombosSelection(null);
            UpdateAiTargetInfo();
        }
    }

    /// <summary>
    /// 尝试通过微信窗口与本地数据库探测当前聊天好友名称：
    /// 1. 若为独立聊天窗口（ChatWnd），直接从窗口标题获取；
    /// 2. 若为微信主窗口，通过微信 4.x 本地数据库直读 (WCDB4) 或 TraceMemo 获取排在首位的活跃单聊好友。
    /// </summary>
    private async Task<string?> DetectContactFromWeChatHeaderAsync()
    {
        var info = _tracker.GetCurrent();
        if (info is not null && info.ClassName == "ChatWnd")
        {
            string contact = CleanContactName(info.Title);
            if (!string.IsNullOrEmpty(contact) && contact != _mySelfNickname && !TraceMemoClient.IsSystemContact(null, contact))
            {
                return contact;
            }
        }

        try
        {
            var settings = _settingsStore.Load();
            using var provider = CompositeChatHistoryProvider.CreateDefault(settings);
            var sessions = await provider.GetRecentSessionsAsync(maxCount: 50);
            var (detected, _) = await DetectBestActiveContactAsync(sessions, provider, forceAutoDetect: true);
            if (!string.IsNullOrWhiteSpace(detected) && detected != _mySelfNickname && !TraceMemoClient.IsSystemContact(null, detected))
            {
                return detected;
            }
        }
        catch
        {
            // 忽略本地数据库或服务不可用异常
        }

        return null;
    }

    private async void SyncContactButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSyncContact = true;
        UpdateAutoSyncToggleUi();
        SyncContactButton.IsEnabled = false;
        SetPersonaStatus("正在探测当前对话好友...", isBusy: true);

        try
        {
            string? detectedName = null;

            // 1. 优先尝试从微信窗口探测当前好友（独立窗口标题或主窗口顶部标题区 OCR）
            detectedName = await DetectContactFromWeChatHeaderAsync();

            // 2. 若窗口探测未成功，尝试从微信 4.x 本地数据库直读 (WCDB4) 或 TraceMemo 获取当前活跃单聊好友（已强力过滤服务通知与公众号）
            if (string.IsNullOrWhiteSpace(detectedName))
            {
                var settings = _settingsStore.Load();
                try
                {
                    using var provider = CompositeChatHistoryProvider.CreateDefault(settings);
                    detectedName = await provider.GetActiveContactNameAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("OverlayWindow.Persona", "探测当前活跃单聊好友失败", ex);
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
                _ = RefreshRecentSessionsAsync(autoSelectActive: false);
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
        RefreshSettingsPersonaGoalsList();
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
            MainTabControl.SelectedIndex = 4;
            SwitchSettingSubTab(2);
            InitSkillCombo();
            RefreshSettingsSkillList();
        RefreshSettingsPersonaGoalsList();

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
        RefreshSettingsPersonaGoalsList();
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
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex)
            {
                WeChatCopilot.Core.Diagnostics.AppLog.Warn("OverlayWindow", $"创建技能目录失败: {dir}", ex);
            }
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
            SetPersonaStatus("请先填写或同步联系人姓名（用于在微信本地数据库中搜索历史）", isBusy: false);
            return;
        }

        var settings = _settingsStore.Load();
        FetchHistoryButton.IsEnabled = false;
        FetchHistoryButton.Content = "⏳ 刷新中...";
        SetPersonaStatus($"正在从微信本地数据库直读并解析「{name}」历史记录...", isBusy: true);

        try
        {
            bool tmSuccess = false;
            string? lastError = null;
            try
            {
                using var provider = CompositeChatHistoryProvider.CreateDefault(settings);
                var res = await provider.FetchHistoryAsync(name);

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
                        SetPersonaStatus($"✅ 已成功从 {res.SourceName} 拉取「{name}」共 {res.Messages.Count} 条历史消息并存入本地 SQLite（本次入库 {newInserted} 条，本地累计 {totalLocal} 条）。可点击【✨ 一键蒸馏画像】！", isBusy: false);
                    }
                    else
                    {
                        SetPersonaStatus($"ℹ️ {res.SourceName} 中找到了联系人「{name}」，但该会话暂无聊天消息（本地累计 {totalLocal} 条）", isBusy: false);
                    }
                }
                else
                {
                    lastError = res.Error;
                }
            }
            catch (Exception ex)
            {
                tmSuccess = false;
                lastError = ex.Message;
            }

            if (!tmSuccess)
            {
                // 实时数据源不可用：自动回退从本地 SQLite 数据库秒级加载
                var localMessages = await Task.Run(() => _sqliteStorage.GetMessages(name));
                if (localMessages.Count > 0)
                {
                    _history.Clear();
                    _history.AddRange(localMessages);
                    _historyContactName = name;
                    SetPersonaStatus($"⚠️ 实时历史源暂未就绪，已自动从本地 SQLite 数据库秒级加载离线历史（共 {localMessages.Count} 条）。可直接点击【✨ 一键蒸馏画像】！", isBusy: false);
                }
                else
                {
                    string errDetail = !string.IsNullOrWhiteSpace(lastError) ? $"\n原因：{lastError}" : string.Empty;
                    SetPersonaStatus($"❌ 暂未读取到「{name}」的历史记录且本地 SQLite 无缓存。{errDetail}\n（可在【设置】中检查微信 4.x 本地数据库直连配置，或使用「📥 导入 CSV」）", isBusy: false);
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
        if (_distillCts != null)
        {
            _distillCts.Cancel();
            return;
        }

        string name = ContactBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            SetPersonaStatus("请先输入或同步联系人姓名。", isBusy: false, PersonaStatusKind.Error);
            return;
        }

        // 1. 若当前历史为空，或联系人发生变动，自动直连微信 4.x 本地数据库 (WCDB4) 或 TraceMemo 兜底读取最新记录，离线时自动回退本地 SQLite
        if (_history.Count == 0 || !string.Equals(_historyContactName, name, StringComparison.OrdinalIgnoreCase))
        {
            var settings = _settingsStore.Load();
            SetPersonaStatus($"未检测到「{name}」缓存历史，正在从微信本地数据库直读历史记录...", isBusy: true, PersonaStatusKind.Busy);

            try
            {
                using var provider = CompositeChatHistoryProvider.CreateDefault(settings);
                var fetchRes = await provider.FetchHistoryAsync(name);
                if (fetchRes.Success && fetchRes.Messages.Count > 0)
                {
                    _history.Clear();
                    _history.AddRange(fetchRes.Messages);
                    _historyContactName = name;
                    _ = Task.Run(() => _sqliteStorage.SaveMessages(name, fetchRes.Messages));
                }
            }
            catch (Exception ex)
            {
                WeChatCopilot.Core.Diagnostics.AppLog.Warn("OverlayWindow", $"自动拉取「{name}」历史记录失败", ex);
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
                SetPersonaStatus($"历史记录为空：未能从微信 4.x 本地数据库读取且本地缓存无「{name}」的记录。可点击【⚡ 刷新历史】重试或使用【📥 导入CSV】。", isBusy: false, PersonaStatusKind.Error);
                return;
            }
        }

        _distillCts?.Dispose();
        _distillCts = new CancellationTokenSource();
        DistillButton.IsEnabled = true;
        DistillButton.Content = "⏹ 停止蒸馏";
        var sampledForRoute = PersonaDistiller.SampleRepresentativeHistory(_history);
        string sampleContext = string.Join(" ", sampledForRoute.Select(m => m.Text));
        _ = _currentSkill.GetSystemPromptForDistill(sampleContext, out string? routedDistillModule);
        string routeNote = !string.IsNullOrWhiteSpace(routedDistillModule)
            ? $"（🧭 已挂载: {routedDistillModule}）"
            : string.Empty;
        SetPersonaStatus($"🧬 正在调用【{_currentSkill.Icon} {_currentSkill.Name}】蒸馏视角{routeNote}，跨周期采样 {sampledForRoute.Count}/{_history.Count} 条有效历史...", isBusy: true, PersonaStatusKind.Busy);

        try
        {
            var settings = _settingsStore.Load();
            using var provider = CreateAiProvider(settings, DefaultDistillTimeoutSeconds);

            Persona? existing = _personaStore.Load(name);
            var distillResult = await PersonaDistiller.DistillDetailedAsync(
                provider,
                settings,
                name,
                _history,
                existing,
                _mySelfNickname,
                _currentSkill,
                freshDistill: true,
                preferSinglePass: true,
                cancellationToken: _distillCts.Token);

            if (!distillResult.Success || distillResult.Persona.Traits.Count == 0)
            {
                string err = distillResult.ErrorMessage ?? "大模型未按约定格式输出，请重试或在设置中更换模型。";
                SetPersonaStatus($"❌ 蒸馏失败：{err}", isBusy: false, PersonaStatusKind.Error);
                return;
            }

            Persona persona = distillResult.Persona;
            _personaStore.Save(persona);
            ShowPersona(persona);
            RefreshSavedPersonasCombo();
            SetPersonaStatus($"✅「{name}」画像蒸馏成功并已保存！（应用【{_currentSkill.Icon} {_currentSkill.Name}】{routeNote}，提炼 {persona.Traits.Count} 项特质，基于 {persona.SourceMessageCount} 条历史）", isBusy: false, PersonaStatusKind.Success);
        }
        catch (OperationCanceledException)
        {
            SetPersonaStatus("⏹ 已取消本次画像蒸馏。", isBusy: false, PersonaStatusKind.Neutral);
        }
        catch (Exception ex)
        {
            WeChatCopilot.Core.Diagnostics.AppLog.Error("OverlayWindow", "画像蒸馏异常", ex);
            SetPersonaStatus("画像蒸馏异常：" + ex.Message, isBusy: false, PersonaStatusKind.Error);
        }
        finally
        {
            _distillCts?.Dispose();
            _distillCts = null;
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
        string name = string.Empty;
        if (SavedPersonasCombo?.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
        {
            name = selected.Trim();
        }
        else
        {
            name = ContactBox.Text.Trim();
        }

        if (string.IsNullOrEmpty(name))
        {
            SetPersonaStatus("请先在「已保存画像」下拉框中选择要删除的画像。", isBusy: false);
            return;
        }

        var confirm = MessageBox.Show(
            $"确定要删除「{name}」的已保存画像吗？\n删除后将同步清除本地数据库与 JSON 备份记录。",
            "确认删除画像",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
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
        RefreshSettingsPersonaGoalsList();
        UpdateAiTargetInfo();
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

        // 确保传入看板的是当前画像联系人的真实历史记录（若内存未加载或属于上一位联系人，自动从 SQLite 补齐）
        IReadOnlyList<HistoryMessage> contactHistory = _history;
        if (contactHistory.Count == 0 ||
            !string.Equals(_historyContactName, _currentLoadedPersona.ContactName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var loaded = _sqliteStorage.GetMessages(_currentLoadedPersona.ContactName);
                if (loaded.Count > 0)
                {
                    _history.Clear();
                    _history.AddRange(loaded);
                    _historyContactName = _currentLoadedPersona.ContactName;
                    contactHistory = _history;
                }
                else
                {
                    contactHistory = Array.Empty<HistoryMessage>();
                }
            }
            catch
            {
                contactHistory = Array.Empty<HistoryMessage>();
            }
        }

        var win = new PersonaAnalyticsWindow(_currentLoadedPersona, skill, contactHistory)
        {
            Owner = this
        };
        try
        {
            var s = _settingsStore.Load();
            ThemeManager.ApplyTheme(win, s.ThemeMode ?? "Light");
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

        var win = new AdviceAnalyticsWindow(_lastUnifiedAdvice, persona, skill, contact, goal, intent, RegenerateAdviceFromDashboardAsync)
        {
            Owner = this
        };

        try
        {
            var s = _settingsStore.Load();
            ThemeManager.ApplyTheme(win, s.ThemeMode ?? "Light");
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

    private enum PersonaStatusKind
    {
        Auto,
        Neutral,
        Busy,
        Success,
        Error
    }

    private void SetPersonaStatus(string text, bool isBusy, PersonaStatusKind kind = PersonaStatusKind.Auto)
    {
        PersonaStatusText.Text = text;
        PersonaProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        PersonaSpinner.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;

        PersonaStatusKind resolvedKind = kind;
        if (resolvedKind == PersonaStatusKind.Auto)
        {
            if (text.StartsWith("❌") || text.Contains("失败") || text.Contains("异常"))
            {
                resolvedKind = PersonaStatusKind.Error;
            }
            else if (text.StartsWith("✅") || text.Contains("成功"))
            {
                resolvedKind = PersonaStatusKind.Success;
            }
            else if (isBusy)
            {
                resolvedKind = PersonaStatusKind.Busy;
            }
            else
            {
                resolvedKind = PersonaStatusKind.Neutral;
            }
        }

        switch (resolvedKind)
        {
            case PersonaStatusKind.Error:
                PersonaStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusErrorBrush");
                PersonaStatusCard.SetResourceReference(Border.BackgroundProperty, "StatusErrorBgBrush");
                PersonaStatusCard.SetResourceReference(Border.BorderBrushProperty, "StatusErrorBorderBrush");
                break;
            case PersonaStatusKind.Success:
                PersonaStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusSuccessBrush");
                PersonaStatusCard.SetResourceReference(Border.BackgroundProperty, "StatusSuccessBgBrush");
                PersonaStatusCard.SetResourceReference(Border.BorderBrushProperty, "StatusSuccessBorderBrush");
                break;
            case PersonaStatusKind.Busy:
                PersonaStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                PersonaStatusCard.SetResourceReference(Border.BackgroundProperty, "StatusBusyBgBrush");
                PersonaStatusCard.SetResourceReference(Border.BorderBrushProperty, "StatusBusyBorderBrush");
                break;
            default:
                PersonaStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                PersonaStatusCard.SetResourceReference(Border.BackgroundProperty, "CardSubtleBgBrush");
                PersonaStatusCard.SetResourceReference(Border.BorderBrushProperty, "BorderSubtleBrush");
                break;
        }
    }

    private void RefreshSavedPersonasCombo()
    {
        var list = _personaStore.List();
        SavedPersonasCombo.ItemsSource = list;
    }
}

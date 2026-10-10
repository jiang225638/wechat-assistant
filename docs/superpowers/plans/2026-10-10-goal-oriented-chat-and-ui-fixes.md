# 终极目的导向聊天副驾与全景看板视觉优化实施计划 (Goal-Oriented Chat Copilot & UI Enhancements)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 彻底解决悬浮窗看板按钮重叠与全景大窗系统原生边框问题，重构建议控制卡片（移除死板关系下拉框，提供自定义回复方式输入框并实现动态个性化标签），并在设置中新增已保存画像管理与“最终战略目的”功能，使 AI 回复全流程均以最终目的为最高战略指引。

**Architecture:** 
1. **视图层 (WPF / XAML)**：
   - 修复悬浮窗 `OverlayWindow.xaml` 雷达图头部布局，重构为两列自适应 Grid 并缩减标题长度，彻底杜绝按钮与徽章重叠；
   - 优化 `PersonaAnalyticsWindow` 为无边框窗口 (`WindowStyle="None"`, `CanResizeWithGrip`)，支持顶部整条拖拽和双击最大化/还原，并在关闭按钮左侧新增 `[🗖 最大化 / 🗗 还原]` 切换按钮；
   - 重构“建议”控制面板：移除 `RelationshipBox`，加入全宽 `AiIntentBox`（用户自定义回复方式/意图输入框），动态联动当前联系人的 `UltimateGoal`；
   - 在“设置”面板增加 `SettingViewGoals`（已保存画像列表与最终目的管理），支持为每个好友查看、编辑和持久化保存其最终战略目的。
2. **数据层 (Data & Models)**：
   - `Persona.cs` 扩展可空属性 `string? UltimateGoal = null`；
   - `PersonaStore.cs` 增加 `UpdateUltimateGoal(contactName, goal)` 与读取辅助方法；
   - `PersonaDistiller.cs` 保证重蒸馏时保留已有联系人的 `UltimateGoal`。
3. **AI 提示词与编排层 (AI / Prompt)**：
   - `PromptBuilder.BuildUnifiedAdvice` 升级：将 `ultimateGoal` 作为最高战略导向注入 SystemPrompt 与 UserPrompt；
   - 将用户的即时 `userIntent` 融入回复要求；
   - 彻底打破原来固定的 `[高情商/直接/专业/缓和]` 死板标签限制，指示大模型根据对方独特的画像特质、心理攻防与战略目的，动态生成量身定制的战术标签（如 `[情绪托底与台阶]`、`[高位推拉反制]`、`[顺势价值锚定]`、`[借势邀约引导]`）。

**Tech Stack:** .NET 10 LTS, C# 14, WPF Fluent UI, xUnit.

---

## Global Constraints
- 保证向前兼容：`UltimateGoal` 默认为 null，旧版本生成的 JSON 画像反序列化 100% 正常。
- 绝不代发消息：严格只读并生成建议与理由，保留一键复制。
- 编译通过且所有单元测试全部通过。

---

## Tasks

### Task 1: 修复全景深度看板窗口边框与增加最大化按钮 (图2优化)
**Files:**
- Modify: `src/WeChatCopilot.App/PersonaAnalyticsWindow.xaml`
- Modify: `src/WeChatCopilot.App/PersonaAnalyticsWindow.xaml.cs`

- [ ] **Step 1: 修改 `PersonaAnalyticsWindow.xaml`**
  - 设置 `WindowStyle="None"`, `AllowsTransparency="False"`, `ResizeMode="CanResizeWithGrip"`.
  - 在 Row 0 顶部 Header Border 添加 `MouseLeftButtonDown="Header_MouseLeftButtonDown"`.
  - 在右侧操作按钮区，在 `CloseButton` 左侧添加 `MaximizeButton` (`🗖 最大化` / `🗗 还原`)。

- [ ] **Step 2: 修改 `PersonaAnalyticsWindow.xaml.cs`**
  - 添加 `Header_MouseLeftButtonDown`（支持拖拽 `DragMove()` 与双击切换最大化/还原）。
  - 添加 `MaximizeButton_Click` 与 `ToggleMaximize()` 方法。
  - 重写 `OnStateChanged`，动态同步 `MaximizeButton` 的图标与提示文案。

- [ ] **Step 3: 验证编译**
  - 运行 `dotnet build src/WeChatCopilot.App/WeChatCopilot.App.csproj`。

---

### Task 2: 修复悬浮窗雷达图看板按钮重叠 (图1修复)
**Files:**
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml`

- [ ] **Step 1: 优化雷达图卡片 Header 布局**
  - 将 `多维性格与沟通雷达图谱` 精简为 `沟通雷达图谱`（字号 12，粗体）。
  - 为 Header Grid 添加 `ColumnDefinition Width="*"` 与 `ColumnDefinition Width="Auto"`。
  - 限制 `RadarSkillBadgeText` 的 `MaxWidth="105"` 与 `TextTrimming="CharacterEllipsis"`。
  - 缩短按钮边距与填充，彻底防止文字与按钮碰撞重叠。

- [ ] **Step 2: 验证编译**
  - 运行 `dotnet build src/WeChatCopilot.App/WeChatCopilot.App.csproj`。

---

### Task 3: 扩展数据模型与存储：Persona 最终战略目的 (UltimateGoal)
**Files:**
- Modify: `src/WeChatCopilot.Core/Models/Persona.cs`
- Modify: `src/WeChatCopilot.Data/PersonaStore.cs`
- Modify: `src/WeChatCopilot.Core/Ai/PersonaDistiller.cs`
- Test: `tests/WeChatCopilot.Tests/PersonaStoreTests.cs`

- [ ] **Step 1: 编写单元测试**
  - 在 `PersonaStoreTests.cs` 中添加 `UpdateUltimateGoal_PersistsAndLoadsCorrectly` 测试。

- [ ] **Step 2: 运行测试并确认失败**
  - 运行 `dotnet test tests/WeChatCopilot.Tests/WeChatCopilot.Tests.csproj`。

- [ ] **Step 3: 修改 `Persona.cs`、`PersonaStore.cs` 与 `PersonaDistiller.cs`**
  - 在 `Persona` record 添加 `string? UltimateGoal = null`。
  - 在 `PersonaStore` 增加 `UpdateUltimateGoal(string contactName, string? ultimateGoal)`。
  - 在 `PersonaDistiller` 合并保存时传递 `existing?.UltimateGoal`。

- [ ] **Step 4: 运行测试并确认通过**
  - 运行 `dotnet test tests/WeChatCopilot.Tests/WeChatCopilot.Tests.csproj`。

---

### Task 4: 建议面板重构与动态提示词体系 (图3、图4与最终目的联动)
**Files:**
- Modify: `src/WeChatCopilot.Core/Ai/PromptBuilder.cs`
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml`
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml.cs`
- Test: `tests/WeChatCopilot.Tests/PromptBuilderTests.cs`

- [ ] **Step 1: 编写 PromptBuilder 单元测试**
  - 在 `PromptBuilderTests.cs` 中添加验证 `userIntent` 与 `ultimateGoal` 战略注入的测试，验证动态标签与目的导向提示词。

- [ ] **Step 2: 运行测试并确认失败**
  - 运行 `dotnet test tests/WeChatCopilot.Tests/WeChatCopilot.Tests.csproj`。

- [ ] **Step 3: 重构 `PromptBuilder.cs`**
  - `BuildUnifiedAdvice` 增加 `string? userIntent = null`, `string? ultimateGoal = null`。
  - 注入终极目的战略指引与用户自定义回复意图。
  - 明确要求大模型禁止死板的“高情商/直接/专业/缓和”标签，动态结合画像特质与战术动作输出标签。

- [ ] **Step 4: 重构 `OverlayWindow.xaml` 建议控制卡片**
  - 删除 `RelationshipBox`。
  - 新增 `AiIntentBox`（用户自定义回复方式/意图输入框）。
  - 新增 `AiGoalBadge` 与 `AiGoalText` 胶囊，显示当前联系人已配置的最终目的。

- [ ] **Step 5: 更新 `OverlayWindow.xaml.cs` 联动与生成逻辑**
  - `UpdateAiTargetInfo()` 动态展示当前好友的最终战略目的。
  - `GenerateButton_Click()` 获取 `AiIntentBox.Text` 与 `personaContext?.UltimateGoal`，传入 `PromptBuilder.BuildUnifiedAdvice`。

- [ ] **Step 6: 运行测试验证**
  - 运行 `dotnet test tests/WeChatCopilot.Tests/WeChatCopilot.Tests.csproj`。

---

### Task 5: 设置面板新增“已保存画像与最终目的管理”卡片
**Files:**
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml`
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml.cs`

- [ ] **Step 1: 在 `OverlayWindow.xaml` 的 `ViewSettings` 增加画像目的子页面**
  - 增加 RadioButton `SettingSubTabGoals` (`🎯 画像目的`, Tag="2")。
  - 增加 ScrollViewer `SettingViewGoals`，包含已保存画像列表 `SavedPersonasGoalsList`，支持每个好友显示备注名、特质数、更新时间、最终目的编辑输入框与 `[💾 保存]` 按钮。

- [ ] **Step 2: 在 `OverlayWindow.xaml.cs` 实现列表加载与保存逻辑**
  - 定义 `PersonaGoalItemViewModel`。
  - 实现 `RefreshSettingsPersonaGoalsList()`、`SavePersonaGoal_Click()` 与 `RefreshSavedGoalsButton_Click()`。
  - 切换到画像目的子标签时自动刷新列表。

- [ ] **Step 3: 全量编译与单元测试验证**
  - 运行 `dotnet build` 与 `dotnet test`。

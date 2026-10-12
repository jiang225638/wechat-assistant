# 动态分层立体研判与话语力量全景看板 (AdviceAnalyticsWindow) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 彻底重构「建议」提示词与研判引擎为基于【最终目的 $\times$ 认知画像 $\times$ 作战Skill $\times$ 用户意图】的动态非硬编码分层立体研判，并全新开发「话语力量与博弈推演全景大看板」（`AdviceAnalyticsWindow`），在一屏全景内融合动态维度重叠雷达图、维度分组对决柱状图与战略博弈双轴象限图，让用户直观感受话语的力量。

**Architecture:** 
1. Core 层升级 `UnifiedAiAdvice` 与 `ReplySuggestion`，承载 AI 实时动态提炼的评估维度集合与每条回复的多维得分（推进力、安全度、动态维度值）；
2. 升级 `PromptBuilder` 与 `AiOutputParser`，彻底去除任何写死的情感偏置与固定死板维度，由当前目的、画像、Skill与意图驱动 AI 动态量化；
3. App 层新建独立的 `AdviceAnalyticsWindow.xaml` / `.xaml.cs`，实现一屏全景三大复合可视化图表绘制、多方案联动高亮与战术报告导出；
4. 在 `OverlayWindow` 建议结果面板新增【🔍 放大看板】入口，打通全流程交互。

**Tech Stack:** C# 13, .NET 10, WPF, XAML DynamicResource Theming, Canvas 2D Graphics.

**Spec:** docs/superpowers/plans/2026-10-12-dynamic-advice-and-panoramic-dashboard.md

## Global Constraints
- 严禁硬编码固定的 5 个维度；评估维度必须由 AI 结合当前战局动态提炼（3~6个）；
- 全看板必须原生适配 Fluent Dark 与 Fluent Light 双主题（使用 `{DynamicResource ...}`）；
- 无系统标题栏（`WindowStyle="None" AllowsTransparency="True"`），配备底部优雅的最大化、关闭与一键复制导出控件；
- 全套测试必须保持 100% 绿灯（`dotnet test` 0 失败）。

## Review Focus
1. 当 AI 返回未按预期包含量化得分或维度时，解析器必须平滑兜底，绝不闪退；
2. 当候选回复为 1 条或多条（3~5条）时，重叠雷达图、分组柱状图与象限图均能自适应正确绘制；
3. 在浅色模式下，看板内所有文字、图表轴线、标签与卡片必须具有充足的对比度，绝不可出现隐形文字；
4. 动态维度包含中英文混合或特殊符号时，图表坐标与文字标签计算不发生越界或重叠；
5. 点击不同回复方案时，图表高亮与下方详情联动无延迟。

---

### Task 1: 数据模型升级 (UnifiedAiAdvice 与 ReplySuggestion 支持动态量化维度)

**Files:**
- Modify: `src/WeChatCopilot.Core/Models/UnifiedAiAdvice.cs`
- Test: `tests/WeChatCopilot.Tests/AiOutputParserTests.cs`

**Interfaces:**
- Consumes: `ReplySuggestion`
- Produces: `UnifiedAiAdvice.DynamicDimensions`, `ReplySuggestion.DimensionScores`, `ReplySuggestion.GoalPushScore`, `ReplySuggestion.PersonaSafetyScore`

- [ ] **Step 1: 编写数据模型与解析的单元测试**
在 `tests/WeChatCopilot.Tests/AiOutputParserTests.cs` 中增加测试，验证当 AI 返回包含动态维度及量化得分时，`ParseUnifiedAdvice` 能正确提取 `DynamicDimensions`、`DimensionScores`、`GoalPushScore` 与 `PersonaSafetyScore`。

- [ ] **Step 2: 运行测试验证失败**
Run: `dotnet test --filter ParseUnifiedAdvice_ParsesDynamicDimensionsAndScores`
Expected: FAIL (属性尚未定义)

- [ ] **Step 3: 扩展 `UnifiedAiAdvice.cs` 与 `ReplySuggestion` 模型**
为 `ReplySuggestion` 添加 `IReadOnlyDictionary<string, double>? DimensionScores = null`, `double GoalPushScore = 75`, `double PersonaSafetyScore = 80`。
为 `UnifiedAiAdvice` 添加 `IReadOnlyList<string>? DynamicDimensions = null`。

- [ ] **Step 4: 运行测试验证通过**
Run: `dotnet test`
Expected: PASS

---

### Task 2: 提示词引擎与解析器升级 (去硬编码，由目的/画像/Skill/意图驱动动态量化)

**Files:**
- Modify: `src/WeChatCopilot.Core/Ai/PromptBuilder.cs`
- Modify: `src/WeChatCopilot.Core/Ai/AiOutputParser.cs`
- Test: `tests/WeChatCopilot.Tests/PromptBuilderTests.cs`
- Test: `tests/WeChatCopilot.Tests/AiOutputParserTests.cs`

**Interfaces:**
- Consumes: `PromptBuilder.BuildUnifiedAdvice`, `AiOutputParser.ParseUnifiedAdvice`
- Produces: 动态沙盘立体重构提示词与解析映射

- [ ] **Step 1: 编写提示词与解析测试用例**
在 `PromptBuilderTests.cs` 增加测试验证 SystemPrompt 与 UserPrompt 不再出现狭隘的情感/好感约束，而是强调结合【最终目的 $\times$ 画像 $\times$ Skill $\times$ 用户意图】动态输出专属博弈维度与得分；在 `AiOutputParserTests.cs` 增加多格式得分容错解析测试。

- [ ] **Step 2: 运行测试验证期望**
Run: `dotnet test`
Expected: FAIL 或测试缺失

- [ ] **Step 3: 实现 `PromptBuilder.cs` 升级**
重构 `BuildUnifiedAdvice`：
1. 提示词定义角色为全域战略沟通军师与高维对话副驾；
2. 约束 AI 严格结合用户最终目的、对方画像特质与 Skill 心法，动态提炼 3～6 个专属评估维度；
3. 输出格式约定：包含沙盘态势、画像透视、潜台词、Skill破局策略、动态维度列表，以及每条回复的战术得分（维度评分、目标推进力、画像安全度）。

- [ ] **Step 4: 实现 `AiOutputParser.cs` 升级**
支持解析 `=== 动态博弈评估维度 ===` 或 `评估维度：...`，并解析每行回复末尾附带的量化得分 `| 得分：... | 推进力=xx, 安全度=yy`，具备平滑降级能力。

- [ ] **Step 5: 运行全量核心单测**
Run: `dotnet test`
Expected: 153+ PASS

---

### Task 3: 建议全景大看板 XAML 布局设计 (AdviceAnalyticsWindow.xaml)

**Files:**
- Create: `src/WeChatCopilot.App/AdviceAnalyticsWindow.xaml`
- Test: `tests/WeChatCopilot.Tests/OverlayWindowXamlResourceTests.cs`

**Interfaces:**
- Consumes: `UnifiedAiAdvice`, `Persona`, `DistillSkill`
- Produces: 一屏全景多维复合图表看板视图

- [ ] **Step 1: 编写 XAML 资源与键值测试**
在 `OverlayWindowXamlResourceTests.cs` 中增加对 `AdviceAnalyticsWindow.xaml` 的静态/动态资源扫描测试，确保所有画刷和控件键值完整合法。

- [ ] **Step 2: 创建 `AdviceAnalyticsWindow.xaml`**
设计一屏全景（推荐尺寸 980×720，最小 850×600）现代化 Fluent 看板：
1. **顶部 Header 战略信息栏**：对话好友、终极战略目的胶囊、当前激活 Skill 徽章、用户回复意图标签；
2. **中间复合图表分析区（一屏三维全景矩阵）**：
   - 左侧（40%）：【多方案全景重叠雷达图】（Canvas，多方案半透明交叠着色 + 图例说明）；
   - 中间（35%）：【动态维度横向对决分组柱状图】（Canvas / StackPanel，展示每个动态提炼维度的红蓝绿柱对比）；
   - 右侧（25%）：【战略博弈双轴象限图】（Canvas，推进力 vs 安全度，四象限战术定位打点）；
3. **下方沙盘推演与回复横向对战卡片**：
   - 战局态势、画像底牌透视、Skill 破局心法流式展示；
   - 候选回复对决列表，卡片与图表联动悬浮/点击高亮，附带【📋 复制话术】按钮；
4. **底部控制栏**：全景报告一键导出、最大化切换、关闭。

- [ ] **Step 3: 运行静态资源单测**
Run: `dotnet test`
Expected: PASS

---

### Task 4: 看板核心逻辑与全景图表绘制引擎 (AdviceAnalyticsWindow.xaml.cs)

**Files:**
- Create: `src/WeChatCopilot.App/AdviceAnalyticsWindow.xaml.cs`
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml`
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml.cs`
- Test: `tests/WeChatCopilot.Tests/OverlayWindowXamlResourceTests.cs`

**Interfaces:**
- Consumes: `AdviceAnalyticsWindow(UnifiedAiAdvice, Persona?, DistillSkill?, string?, string?)`
- Produces: 动态维度重叠雷达绘制算法、分组柱状图绘制算法、象限图打点算法

- [ ] **Step 1: 实现 `AdviceAnalyticsWindow.xaml.cs` 绘制逻辑**
1. `DrawMultiOptionRadarChart()`: 动态根据 `DynamicDimensions` 计算各轴，重叠绘制方案 1（亮蓝）、方案 2（翠绿）、方案 3（紫罗兰）的发光半透明多边形与高光圆点；
2. `DrawGroupedBarChart()`: 针对各动态维度，并排绘制各方案量化条，柱头显示数值；
3. `DrawQuadrantMatrix()`: 绘制双轴四象限力场背景，将各方案按 `(PersonaSafetyScore, GoalPushScore)` 打点并标注方案名；
4. 交互联动：点击方案卡片或图例，图表中该方案多边形高亮增粗，其他方案半透明淡化；
5. 支持最大化/还原、拖拽与报告导出。

- [ ] **Step 2: 在 `OverlayWindow` 建议结果中集成【🔍 放大看板】按钮**
在 `OverlayWindow.xaml` 的 `AiResultPanel` 标题栏新增 `OpenAdviceAnalyticsButton`，在 `OverlayWindow.xaml.cs` 中实现点击弹出 `AdviceAnalyticsWindow`。

- [ ] **Step 3: 编译与单测全量验证**
Run: `dotnet build`
Run: `dotnet test`
Expected: 0 警告，0 错误，全部测试绿灯。

---

### Task 5: 综合验收与实机验证

- [ ] **Step 1: 验证商务/职场/社交等多场景动态维度提炼**
- [ ] **Step 2: 验证一屏全景三大图表（重叠雷达、分组对比柱状、象限图）渲染美观无重叠**
- [ ] **Step 3: 验证浅色与深色模式热切换下的完美对比度**

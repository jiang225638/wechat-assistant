# 微信副驾 多轮语境分析、自由窗口缩放与明暗双主题实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

---

## 🎯 需求概述

本次改造包含用户确认的 3 大核心体验与功能需求：
1. **智能分析多轮语境深度判定与配置**：
   - 告别只看最后一句话单句断章取义的问题；
   - 默认采用人际沟通研判最佳的 **10 条**（涵盖 3~6 轮完整交互），并在设置与建议面板提供自定义跨度（5条/10条/15条/20条）；
   - 升级 `PromptBuilder.BuildUnifiedAdvice` 与 `UnifiedAiAdvice` 解析，显式引导 AI 分层输出【近期脉络与情感走向】、【最新发言深度穿透】与【承前启后的破局建议】，并在 UI 上直观展示近期语境脉络。
2. **主悬浮窗口自由缩放 (Resize) 与尺寸记忆**：
   - 在透明异形无边框窗口（`OverlayWindow`）中接入 Win32 原生 `WM_NCHITTEST` 消息拦截；
   - 当鼠标移动到窗口四周边缘及四角（6~8px）时，自动显示系统级调整大小光标（`SizeWE` / `SizeNS` / `SizeNWSE` / `SizeNESW`）并支持平滑拖拽调整宽高；
   - 限制安全缩放区间（最小 390×520，最大 800×1080），并在 `AiSettings` 中持久化记忆用户调整的尺寸，下次启动无缝恢复。
3. **明暗双套主题切换 (Fluent Dark & Fluent Light)**：
   - 在 `AiSettings` 中增加 `ThemeMode`（"Dark" / "Light" / "System"）；
   - 在【设置】->【偏好设置】中增加明暗主题切换下拉框；
   - 引入主题管理器 `ThemeManager`，无缝动态更新应用调色板画刷与 `Wpf.Ui.Appearance.ApplicationThemeManager`，实现全应用界面热重载切换，无需重启。

---

## 🗺️ 文件结构与修改范围

- **数据与领域模型层**：
  - `src/WeChatCopilot.Core/Models/AiSettings.cs`：增加 `ContextMessageCount`, `ThemeMode`, `WindowWidth`, `WindowHeight` 属性。
  - `src/WeChatCopilot.Core/Models/UnifiedAiAdvice.cs`：增加 `ContextFlowSummary`（近期语境脉络与情绪走向）。
  - `src/WeChatCopilot.Core/Ai/PromptBuilder.cs`：升级 `BuildUnifiedAdvice`，支持 `contextCount` 参数与多轮语境演进分析提示词。
  - `src/WeChatCopilot.Core/Ai/AiOutputParser.cs`：解析意图剖析中的【近期语境脉络/情感走向】字段。
- **UI 与交互层**：
  - `src/WeChatCopilot.App/ThemeManager.cs`（新文件）：负责 Fluent Dark / Fluent Light / System 主题画刷切换与状态广播。
  - `src/WeChatCopilot.App/OverlayWindow.xaml`：
    - 建议面板中增加语境跨度选择与近期脉络卡片；
    - 设置面板中增加【外观主题】与【对话语境分析跨度】选择框。
  - `src/WeChatCopilot.App/OverlayWindow.xaml.cs`：
    - 集成 `WM_NCHITTEST` 实现无边框窗口边缘拖拽缩放；
    - 集成主题切换事件与尺寸持久化保存；
    - 在建议生成时应用自定义语境条数。
- **自动化测试**：
  - `tests/WeChatCopilot.Tests/PromptBuilderTests.cs`：补充多轮语境与条数控制单测。
  - `tests/WeChatCopilot.Tests/AiOutputParserTests.cs`：补充近期语境脉络解析单测。

---

## 📋 任务拆解与实施步骤

### 任务 1：升级模型与设置，支持语境条数与主题尺寸持久化 (Core & Data)

- [x] **步骤 1.1**：在 `AiSettings.cs` 中增加新属性与默认值
  - `ContextMessageCount`: 默认 `10`
  - `ThemeMode`: 默认 `"Dark"`
  - `WindowWidth`: 默认 `430.0`
  - `WindowHeight`: 默认 `690.0`
- [x] **步骤 1.2**：在 `UnifiedAiAdvice.cs` 中增加 `ContextFlowSummary` 字段，用于记录 AI 提取的【近期语境脉络与情感走向】
- [x] **步骤 1.3**：在 `PromptBuilder.cs` 中增加对 `contextCount` 的支持
  - 截取最近 `contextCount` 条消息（默认 10）；
  - 增强提示词约束：要求 AI 在【意图剖析】首项输出“近期语境：双方对话当前的话题演进、情绪温度（升温/降温/拉扯）与语境氛围”；
- [x] **步骤 1.4**：在 `AiOutputParser.cs` 中增加对“近期语境/语境走向”的容错正则提取
- [x] **步骤 1.5**：编写或更新单元测试，运行 `dotnet test` 确保 100% 通过
- [x] **步骤 1.6**：提交 Task 1 代码

---

### 任务 2：实现透明无边框窗口自由缩放 (WM_NCHITTEST) 与尺寸记忆 (App)

- [x] **步骤 2.1**：在 `OverlayWindow.xaml.cs` 的 `OnSourceInitialized` 中注册 Win32 消息钩子 `HwndSource.AddHook`
- [x] **步骤 2.2**：编写 `WndProc` 处理 `WM_NCHITTEST (0x0084)`
  - 边缘判定厚度为 `7` 像素；
  - 精确计算鼠标坐标相对于窗口四周（左、右、顶、底、四角）；
  - 返回对应的 Win32 命中代码（`HTLEFT`, `HTRIGHT`, `HTTOP`, `HTBOTTOM`, `HTTOPLEFT`, `HTTOPRIGHT`, `HTBOTTOMLEFT`, `HTBOTTOMRIGHT`）；
  - Windows 原生自动变换光标（↔, ↕, ↖, ↗）并接管边缘拖拽缩放；
- [x] **步骤 2.3**：在窗口大小改变时（`SizeChanged`）进行防抖处理并保存新宽高到 `AiSettings`
- [x] **步骤 2.4**：在窗口初始化时读取已保存的 `WindowWidth` 和 `WindowHeight` 并安全恢复
- [x] **步骤 2.5**：运行程序与测试验证自由缩放

---

### 任务 3：实现明暗两套主题系统 (ThemeManager & Dynamic Palette)

- [x] **步骤 3.1**：创建 `src/WeChatCopilot.App/ThemeManager.cs`
  - 提供 `ApplyTheme(Window window, string themeMode)` 静态方法；
  - 针对 `Dark` 与 `Light` 定义专属调色板：
    - **Dark**：WindowBg `#FA181824`, CardBg `#EE222233`, CardSubtle `#771B1B2A`, TextPrimary `#FFF3F4F6`, TextSecondary `#FFA0A0B8`, Border `#33FFFFFF`;
    - **Light**：WindowBg `#FAFFFFFF`, CardBg `#FFF1F5F9`, CardSubtle `#FFE2E8F0`, TextPrimary `#FF0F172A`, TextSecondary `#FF475569`, Border `#FFE2E8F0`;
  - 支持调用 `Wpf.Ui.Appearance.ApplicationThemeManager.Apply(theme)` 刷新基础控件；
  - 动态变更非冻结的 `SolidColorBrush` 颜色，全窗口即时热重载生效；
- [x] **步骤 3.2**：在 `OverlayWindow.xaml` 的【偏好与系统设置】中增加【外观主题】下拉框（深色 / 浅色 / 跟随系统）
- [x] **步骤 3.3**：在 `OverlayWindow.xaml.cs` 中增加主题选择事件与启动自动应用主题逻辑
- [x] **步骤 3.4**：运行验证深浅色无缝切换

---

### 任务 4：“建议”面板多轮语境深度选择与 UI 呈现升级

- [x] **步骤 4.1**：在 `OverlayWindow.xaml` 的建议操作栏增加【语境跨度】下拉框（5条 / 10条 / 15条 / 20条）并在设置中保存默认偏好
- [x] **步骤 4.2**：在“意图剖析”面板顶部增加【近期语境脉络与情感走向】展示卡片（带有情感风向标图标 🌊）
- [x] **步骤 4.3**：在 `OverlayWindow.xaml.cs` 的 `ShowUnifiedAdvice` 中绑定并展示多轮语境走向
- [x] **步骤 4.4**：全套单元测试验证并打包验证
- [x] **步骤 4.5**：向用户交付效果与说明

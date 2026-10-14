<h1 align="center">WeChat Copilot · 微信智能聊天副驾</h1>

<div align="center">

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20x64-0078D4?logo=windows)
![.NET Version](https://img.shields.io/badge/.NET-10.0%20LTS-512BD4?logo=dotnet)
![Language](https://img.shields.io/badge/C%23-14-239120?logo=csharp)
![UI](https://img.shields.io/badge/WPF-Fluent%20Dark%20%26%20Light-2D7D9A)
![Database](https://img.shields.io/badge/Database-SQLite%20(WAL)-blue?logo=sqlite)
![Tests](https://img.shields.io/badge/Tests-170%20Passed-brightgreen?logo=xunit)
![License](https://img.shields.io/badge/License-MIT-blue.svg)
![Privacy](https://img.shields.io/badge/Privacy-Local--First%20%7C%20DPAPI-success)

**为微信 PC 端量身打造的贴边悬浮式 AI 智能沟通副驾**  
无感贴边跟随 · 自由拖拽缩放 · 明暗双主题 · SQLite本地持久化 · 技能文件夹绑定 · 话语力量全景博弈大看板

[核心特性](#-核心特性) • [系统架构](#-系统架构) • [快速开始](#-快速开始) • [打包发布](#-打包发布-exe-安装文件) • [设置与功能指南](#-设置与功能指南) • [快捷键指南](#-快捷键指南) • [项目结构](#-项目结构) • [隐私与安全](#-隐私与安全)

</div>

---

## 📖 项目简介

**WeChat Copilot** 是一款专为 Windows 平台微信 PC 端量身定制的**全域智能对话辅助桌面工具**。以轻量无边框的现代 Fluent 悬浮窗形态，自动贴合微信聊天窗口边缘，随微信窗口平滑移动与跨屏漫游；同时支持鼠标移动到边框自由缩放尺寸与明暗双主题无缝热切换。

项目严格遵循 **“本地优先 (Local-First)”** 与 **“只读不代发”** 的严苛安全原则：
- **热链路（实时感知）**：Windows 原生硬件级视觉 OCR 与聊天气泡几何判定，不注入进程、不解密内存、不 Hook 接口，安全零风控识读当前对话；
- **冷链路（深度认知与离线持久化）**：对接本地 [TraceMemo](https://github.com/Wxw-Gu/TraceMemo) 微信服务与本地 SQLite 数据库，基于可插拔的 **Skill 认知蒸馏方法论**（职场商业协同、亲密关系心理、麦肯锡结构化逻辑等），生成动态自适应的多维雷达图谱与长效画像；
- **SQLite 轻量化本地关系数据库**：引入 `Microsoft.Data.Sqlite` 开启 WAL 高并发模式。消息带 SHA256 唯一指纹幂等去重，TraceMemo 在线时自动增量入库；TraceMemo 离线时**秒级回退加载本地历史**，断网或未开 TraceMemo 依然能随时蒸馏画像与查阅记录；
- **Antigravity 风格技能文件夹绑定**：支持绑定工作区或指定目录，递归自动发现所有 `SKILL.md`，支持热重扫与物理安全删除；
- **AI 动态立体研判与话语力量看板**：彻底去除死板硬编码，由【终极战略目的 $\times$ 认知画像 $\times$ 作战Skill $\times$ 用户意图】协同驱动，实时提炼专属博弈维度，在一屏全景大看板中融合**重叠雷达、量化柱状、战略双轴象限图**，让用户直观感受话语的力量。

---

## ✨ 核心特性

### 1. 📌 智能贴边吸附、平滑跟随与自由缩放拖拽 (Overlay, Tracking & Resizing)
- **微信窗口自适应定位**：支持微信主窗口（`WeChatMainWndForPC`）与独立单聊窗口（`ChatWnd`），自动吸附贴边。
- **平滑无抖跟随**：实时感知微信窗口移动、缩放与跨多显示器拖拽，毫秒级平滑跟随；支持一键“固定/解锁”自由拖拽。
- **边框自由缩放调节**：鼠标移动到悬浮窗边框或四角时，自动显示双向/四向调整光标，支持 8 个方向自由拉伸放大缩小，窗口尺寸防抖持久化记忆。
- **智能生命周期感知**：微信最小化或隐藏时自动收起，恢复激活时平滑重现。

### 2. 🎨 Fluent 明暗双主题动态热重载 (Dark & Light Themes)
- **深浅主题无缝切换**：在设置中支持**深色模式 (Fluent Dark)**、**浅色模式 (Fluent Light)** 与**跟随系统 (System)** 动态热切换，无需重启即刻刷新全窗口调色板。
- **全要素高对比度保证**：浅色模式下白底黑字清爽利落、阴影通透；深色模式深邃科技感，彻底杜绝任何“白底白字”或隐形文字。

### 3. ⚡ 热链路：纯视觉零侵入当前对话识别 (Vision OCR & Bubble Geometry)
- **零封号风险**：完全依靠屏幕截图与 Windows 原生 WinRT OCR（`Windows.Media.Ocr`）引擎识别，绝不触碰微信内部内存或数据库。
- **气泡几何推断收发**：根据聊天气泡水平坐标中线与几何位置，精准区分**对方发言（左侧）**与**自己发言（右侧）**。
- **双引擎接入与自动持久化**：支持纯视觉 OCR 一键抓取，同时支持直连本地 TraceMemo 毫秒级提取零乱码原文字流；识别气泡自动异步沉淀至本地 SQLite 数据库。

### 4. 🧠 冷链路：动态 Skill 认知蒸馏与心智雷达图谱 (Cognitive Skill & Radar)
- **可插拔官方三大认知预设**：
  - 💼 **职场与商业认知 (`workplace`)**：提炼交付导向、推进能动性、权衡决策法、职业防备心、利益诉求导向与职场禁忌；
  - ❤️ **亲密关系与情感偏好 (`intimacy`)**：提炼情感表达、依恋安全感、推进节奏、情绪敏感度、核心情感需求与亲密禁忌雷区；
  - 🎯 **麦肯锡逻辑视角 (`logical`)**：提炼结构化表达、事实求证度、因果推导严密、辩论抗逆力、逻辑闭环度与逻辑反模式。
- **Antigravity 风格技能文件夹绑定**：
  - 彻底抛弃繁琐的单文件导入与手动表单，支持直接绑定本地技能文件夹；
  - 系统自动递归扫描所有子目录下的 `SKILL.md` / `skill.md` 技能定义；
  - 支持【📂 绑定技能文件夹】、【🔄 重新扫描】与【📁 打开目录】；
  - 自定义技能删除时同步安全清理底层物理文件与缓存，刷新绝不复现。
- **画像全景深度分析看板 (`PersonaAnalyticsWindow`)**：
  - 配备现代化顶部全景边框（支持全屏拖拽、双击缩放、最小化 `─`、最大化/还原 `🗖`、关闭 `✕`）；
  - 提供高分辨率多维雷达图谱、特质分值对比柱状图、特质走势折线图与真实原话证据清单，支持一键导出完整 Markdown 分析报告。

### 5. 💾 SQLite 本地轻量化数据库持久化 (Local SQLite Database & Offline Sync)
- **统一本地数据底座**：采用 `Microsoft.Data.Sqlite` 在 `%AppData%\WeChatCopilot\copilot.db` 建立统一关系库，开启 WAL 模式支持高并发吞吐。
- **SHA256 唯一指纹幂等去重**：每条消息根据 `contact_name + role + text + timestamp` 生成唯一哈希，多次刷新历史或频繁 OCR 抓屏均执行 `INSERT OR IGNORE`，绝不产生重复记录。
- **TraceMemo 在线增量入库 + 离线秒级回退**：
  - TraceMemo 在线时，点击【⚡ 刷新历史】自动拉取并增量同步入库；
  - TraceMemo 离线时（未启动或断开），自动零延迟回退从本地 SQLite 加载历史对话，状态栏友好提示，**【✨ 一键蒸馏画像】在离线状态下完全可用**！
- **全链路消息自动入库**：无论是 TraceMemo 同步、CSV 文件导入还是前台屏幕 OCR 捕获，均在后台静默异步写入 SQLite。
- **画像双向兼容**：画像与终极战略目的结构化存入 SQLite，同时保持本地 JSON 备份与平滑自动迁移。

### 6. ⚔️ 动态博弈立体研判与话语力量全景大看板 (`AdviceAnalyticsWindow`)
彻底告别刻板狭隘的“恋爱/情感”偏置与硬编码死板标签，升级为**全域战略沟通军师**（覆盖商务谈判、职场汇报、合作博弈、社交破冰、亲密关系等全场景）：
- **四大战略要素协同驱动**：根据当前对话对象的【终极战略目的】、【人物认知画像】、【激活作战Skill】与【用户期望的回复意图】进行分层立体研判。
- **自适应动态博弈维度**：由 AI 在推理时实时提炼 3～6 个专属博弈维度（如商务提炼：`商业主导权`、`筹码锁定`、`成单推进力`；向上管理提炼：`确定性交付`、`信任资产`、`边界防护`；社交破冰提炼：`舒适安全感`、`情绪共鸣`、`邀约契机`）。
- **多指标量化赋能**：每条候选回复均提供动态维度评分、**【🚩 目标推进力】**（0-100）与**【🛡️ 画像安全度】**（0-100）。
- **一屏全景纵览复合图表大看板**：
  - **图表 1（多方案全景重叠雷达图）**：基于动态维度多边形，方案 1（流光天蓝）、方案 2（翡翠绿）、方案 3（紫罗兰）半透明重叠绘制，直观展现攻防形态覆盖；
  - **图表 2（动态维度分组对决柱状图）**：按动态维度横向并排对比各候选回复方案的量化得分，单项爆发力一目了然；
  - **图表 3（战略博弈双轴象限图）**：纵轴【推进力】vs 横轴【安全度】，方案清晰定位在**🌟 黄金破局**、**⚡ 高能突刺**、**🛡️ 稳健防守**、**⚠️ 谨慎踩雷**四大象限；
  - **战局沙盘与卡片联动高亮**：点击方案卡片可在图表中联动高亮聚焦，支持一键快捷复制话术与导出 Markdown 战局推演报告。

### 7. 🎯 终极战略目的与多轮语境深度调节 (Strategic Goals & Context Depth)
- **多轮语境深度可调**：支持快捷切换或自定义分析深度（最近 3 条、5 条、10 条、15 条、20 条），避免孤立研判最后一句话，全面捕捉语境脉络与情感演进。
- **联系人终极战略目的管理**：为每位好友持久化设定终极战略目的，一键直达编辑与管理，所有回复建议均以目的达成为最高推进锚点。

### 8. ⚙️ 全新集成设置中心 (Settings Hub)
主界面内嵌 5 大设置专区，告别多窗口切换：
1. **🤖 AI 模型与接口**：配置 API 基础路径、模型名称、温度系数、密码级 DPAPI 密钥与连通性测试；
2. **🏷️ 技能中心 (Skills)**：查看当前生效技能，绑定技能文件夹、递归扫描发现、打开目录与物理安全删除；
3. **🚩 目的管理 (Goals)**：统一查看和管理所有联系人的终极战略目的；
4. **🎨 偏好与系统**：外观主题（深色/浅色/跟随系统）、悬浮窗透明度调节、OCR 采样放大倍率、己方微信昵称偏好与数据目录一键直达；
5. **📖 使用指南**：完整操作指引、快捷键一览与 TraceMemo 官方下载。

### 9. 🛡️ 本地优先与安全防护 (Security & Privacy)
- **DPAPI 硬件级加密**：API Key 仅通过 Windows DPAPI 本地加密存储，磁盘无明文。
- **无第三方云端中转**：除直连用户配置的大模型端点外，绝无任何开发者云端遥测与数据回传。
- **全方位测试守护**：170 项自动化单测守护核心业务算法、提示词组装、SQLite 持久化与 XAML 静态/动态资源契约。

---

## 🏛️ 系统架构

```mermaid
flowchart TD
    subgraph UI["🖥️ 用户界面层 (WeChatCopilot.App)"]
        Overlay["悬浮主窗口 (OverlayWindow - Fluent Dark & Light)\n【💬 对话】 【✨ 建议】 【👤 画像】 【⚙️ 设置】"]
        AdviceAnalytics["话语力量与博弈推演全景大看板 (AdviceAnalyticsWindow)\n【重叠雷达】 【分组柱状】 【双轴象限】 【沙盘对决】"]
        PersonaAnalytics["联系人画像深度分析看板 (PersonaAnalyticsWindow)\n【大雷达】 【分值柱状】 【走势折线】 【证据链】"]
        Theme["动态主题管理器 (ThemeManager)"]
    end

    subgraph HotLink["⚡ 热链路 (实时感知)"]
        WeChatWin["微信窗口 (WeChatMainWnd / ChatWnd)"]
        Cropper["区域几何裁切 (ChatRegionCropper)"]
        WinRTOCR["Windows 视觉 OCR 引擎"]
        Segmenter["气泡左右几何推断 (MessageSegmenter)\n左=对方 / 右=己方"]
        ConvBuffer["多轮对话滑动缓冲 (ConversationBuffer)"]
    end

    subgraph Storage["💾 数据持久化底座 (WeChatCopilot.Data)"]
        SqliteDB[("SQLite 本地数据库 (copilot.db)\n【WAL 模式】 【消息指纹去重】\n【离线秒级回退】 【画像与设置】")]
        SqliteStorage["SQLite 存储引擎 (SqliteStorage)"]
        SkillStore["技能中心与目录绑定 (SkillStore)\n【递归扫描 SKILL.md】 【物理安全删除】"]
        PersonaStore["画像与终极目的存储 (PersonaStore)\n【SQLite 优先 + JSON 平滑迁移】"]
    end

    subgraph ColdLink["🧠 冷链路 (历史蒸馏)"]
        TraceMemoAPI["TraceMemo 本地服务 (:6131)\nGitHub: Wxw-Gu/TraceMemo"]
        TMClient["TraceMemo 客户端 (TraceMemoClient)\n系统服务号与群聊噪声强力过滤"]
        Distiller["人格蒸馏器 (PersonaDistiller)\nMap-Reduce 蒸馏 & 证据链去重"]
    end

    subgraph AI["🤖 AI 智能层 (WeChatCopilot.AI & Core)"]
        Prompt["动态提示词工程 (PromptBuilder)\n目的 × 画像 × Skill × 意图 多维战略装配"]
        Client["OpenAI 兼容客户端 (AiClient)\nDeepSeek / Qwen / Kimi / Ollama 等"]
        Parser["输出容错解析器 (AiOutputParser)\n动态维度提炼 & 推进力/安全度量化解析"]
    end

    WeChatWin -->|截屏抓取| Cropper --> WinRTOCR --> Segmenter --> ConvBuffer
    ConvBuffer -.->|前台气泡异步入库| SqliteStorage
    TraceMemoAPI -->|拉取历史| TMClient --> SqliteStorage
    SqliteStorage <--> SqliteDB
    SqliteStorage -->|在线/离线读取历史| Distiller
    SkillStore -->|注入认知维度与系统提示词| Distiller
    Distiller --> PersonaStore
    PersonaStore <--> SqliteStorage
    
    ConvBuffer -->|多轮会话| Prompt
    PersonaStore -->|注入画像与终极目的| Prompt
    SkillStore -->|注入作战策略心法| Prompt
    Prompt --> Client --> Parser --> Overlay
    Parser --> AdviceAnalytics
    PersonaStore --> PersonaAnalytics
    Overlay --> UI
    Theme --> UI
```

---

## 📂 项目结构

```text
e:\huanmai\myprod
├── src/
│   ├── WeChatCopilot.App/        # 应用程序主工程 (WPF + XAML + Fluent 主题)
│   │   ├── OverlayWindow.xaml    # 贴边悬浮窗主界面 (对话 / 建议 / 画像 / 设置 4 大 Tab)
│   │   ├── OverlayWindow.xaml.cs # 交互事件、雷达图动态绘制、自动吸附与缩放对齐逻辑
│   │   ├── AdviceAnalyticsWindow.xaml    # 话语力量全景大看板 (重叠雷达/分组柱状/双轴象限)
│   │   ├── AdviceAnalyticsWindow.xaml.cs # 全景图表 Canvas 绘制与卡片联动交互引擎
│   │   ├── PersonaAnalyticsWindow.xaml   # 联系人画像深度看板 (现代化顶部边框与多图表)
│   │   ├── PersonaAnalyticsWindow.xaml.cs# 画像雷达图/柱状图/折线图绘制与报告导出
│   │   ├── ThemeManager.cs       # 明暗双主题 (Dark/Light/System) 动态热重载管理器
│   │   └── App.xaml / App.xaml.cs# 全局生命周期管理、未捕获异常穿透拦截与日志记录
│   │
│   ├── WeChatCopilot.Core/       # 核心业务逻辑与领域模型
│   │   ├── Ai/                   # PromptBuilder、AiOutputParser (动态维度/推进力/安全度解析)
│   │   ├── Models/               # Persona、DistillSkill、UnifiedAiAdvice、ReplySuggestion
│   │   ├── Processing/           # 气泡切分 MessageSegmenter、文本规整 OcrTextJoiner
│   │   └── Services/             # PersonaDistiller (多维心智画像提炼与证据链去重)
│   │
│   ├── WeChatCopilot.AI/         # 大模型通信层 (OpenAI 协议兼容客户端)
│   │   └── AiClient.cs           # 支持超时控制、健康探测、流式与非流式调用
│   │
│   ├── WeChatCopilot.Ocr/        # 屏幕图像处理与视觉识别
│   │   ├── WindowsMediaOcrEngine.cs # Windows 原生 WinRT OCR 加速
│   │   ├── ScreenCapture.cs      # 高 DPI 感知的自适应截图
│   │   └── ChatRegionCropper.cs  # 聊天消息区域自适应裁剪
│   │
│   ├── WeChatCopilot.Windows/    # Windows 底层交互与 Win32 Interop
│   │   ├── WindowLocator.cs      # 微信主窗口与独立单聊窗口探测
│   │   ├── WindowTracker.cs      # 窗口移动/尺寸变化实时监听追踪
│   │   └── HotkeyManager.cs      # 全局快捷键注册与消息泵路由
│   │
│   └── WeChatCopilot.Data/       # 数据持久化与外部适配层
│       ├── SqliteStorage.cs      # SQLite 本地轻量化存储引擎 (WAL模式、消息指纹去重、离线缓存)
│       ├── SkillStore.cs         # 技能目录绑定、递归扫描发现与物理安全删除
│       ├── TraceMemoClient.cs    # TraceMemo 本地 API 客户端与系统号过滤
│       ├── PersonaStore.cs       # 联系人画像与终极战略目的本地存储 (SQLite优先/双写兼容)
│       ├── SettingsStore.cs      # 系统设置（主题/透明度/语境条数）持久化
│       └── DpapiProtector.cs     # Windows DPAPI 本地敏感数据加解密
│
├── tests/
│   └── WeChatCopilot.Tests/      # xUnit 自动化测试工程 (170 项测试全覆盖)
│       ├── SqliteStorageTests.cs         # SQLite 表初始化、消息去重排序、画像与技能持久化测试
│       ├── SkillStoreTests.cs            # 技能绑定、递归扫描发现与物理安全删除测试
│       ├── PersonaDistillerTests.cs      # 画像蒸馏算法、证据过滤与可信度测试
│       ├── OverlayWindowXamlResourceTests.cs # XAML 静态/动态资源契约与 ThemeManager 测试
│       ├── PromptBuilderTests.cs         # 动态博弈维度提示词工程与装配测试
│       ├── AiOutputParserTests.cs        # 动态维度与推进力/安全度容错解析测试
│       └── SettingsStoreTests.cs         # 基础与外观配置读写测试
│
├── installer/                    # Inno Setup 自动化安装包工程配置
│   ├── WeChatCopilot.iss         # Inno Setup 安装包编译脚本 (现代向导风格)
│   └── ChineseSimplified.isl     # 简体中文向导语言资源
│
├── .github/workflows/
│   └── release.yml               # GitHub Actions CI/CD (全自动版本自增打包发布)
├── build_package.ps1             # 一键打包发布脚本 (生成 Setup.exe 与绿色单文件)
├── Directory.Build.props         # 统一项目属性与 BaseVersion 版本控制
├── WeChatCopilot.slnx            # 现代化 .NET 解决方案配置
└── README.md                     # 项目说明文档
```

---

## 🚀 快速开始

### 1. 环境准备
- **操作系统**：Windows 10 (Build 19041+) / Windows 11 x64
- **开发与运行环境**：[.NET 10 SDK (LTS)](https://dotnet.microsoft.com/download/dotnet/10.0)
- **微信桌面端**：微信 Windows PC 端 3.x 或 4.x
- **大模型 API**：支持任意 OpenAI 兼容端点（如 DeepSeek、通义千问、Kimi、Moonshot、Ollama、vLLM 等）
- **TraceMemo 服务（可选/推荐）**：前往 GitHub 下载并启动 [TraceMemo](https://github.com/Wxw-Gu/TraceMemo)，用于微信长历史记录的本地零侵入获取（即便未运行，Copilot 也会自动秒级回退至本地 SQLite 数据库）。

### 2. 编译与测试

```bash
# 1. 克隆代码仓库
git clone git@github.com:jiang225638/wechat-assistant.git
cd wechat-assistant

# 2. 运行自动化测试 (170 项测试全绿)
dotnet test

# 3. 编译主程序 (Release)
dotnet build WeChatCopilot.slnx -c Release
```

### 3. 启动应用

```bash
# 直接启动应用
dotnet run --project src/WeChatCopilot.App
```

启动后，悬浮窗将自动搜索并贴合吸附在微信聊天窗口的右侧。

---

## 📦 打包发布 (EXE 安装文件)

本项目提供开箱即用的自动化打包脚本与 GitHub Actions 持续集成工作流，可一键生成**标准安装向导安装包（Setup.exe）**与**绿色独立单文件版（Portable.exe）**。

### 1. 产物类型与特点

所有本地打包产物均输出在项目根目录的 `dist/` 文件夹下：

| 产物文件 | 文件大小 | 类型说明 | 特点与优势 |
| :--- | :--- | :--- | :--- |
| **`WeChatCopilot_Setup_v1.0.x.exe`** | **~55 MB** | **标准 Windows 安装包**（强烈推荐） | 包含简体中文向导、自定义安装目录、创建桌面/开始菜单快捷方式、Windows 控制面板标准卸载、支持无管理员权限安装，支持升级自动覆盖。 |
| **`WeChatCopilot_Portable_v1.0.x.exe`** | **~80 MB** | **绿色免安装单文件版** | 纯单个可执行程序，无需安装与解压，双击即可运行，方便放 U 盘或快速尝鲜。 |

> 🛡️ **运行环境自包含 (Self-Contained)**：  
> 两个产物均已内置 **.NET 10 Desktop 运行时**，目标用户的 Windows 电脑**无需提前安装 .NET 10 SDK 或任何依赖库**，直接双击运行！

---

### 2. 一键本地打包脚本

项目根目录预置了自动化打包脚本 [`build_package.ps1`](file:///e:/huanmai/myprod/build_package.ps1)。在 PowerShell 中执行即可：

```powershell
# 1. 默认一键生成安装包与绿色单文件
powershell -ExecutionPolicy Bypass -File .\build_package.ps1

# 2. 指定自定义版本号打包 (例如发布 v1.0.2)
powershell -ExecutionPolicy Bypass -File .\build_package.ps1 -Version "1.0.2"

# 3. 仅构建安装包（跳过单文件以加快打包速度）
powershell -ExecutionPolicy Bypass -File .\build_package.ps1 -IncludeSingleFile:$false
```

---

### 3. GitHub Actions 全自动版本递增发布

项目已集成全自动 CI/CD 工作流 [`.github/workflows/release.yml`](file:///e:/huanmai/myprod/.github/workflows/release.yml)：
- 每次您执行 `git push` 到 `main` 分支后，GitHub Actions 会**基于构建序号自动递增版本号**（`v1.0.1` -> `v1.0.2` -> `v1.0.3`...）；
- 自动编译、运行测试并发布到 GitHub Releases，附带标准安装包与绿色便携版下载，免去手动修改版本号与打标签的繁琐流程。

---

## ⚙️ 设置与功能指南

### 1. AI 接口与模型配置
在【设置】->【🤖 AI 模型与接口】中配置：

| 配置项 | 示例值 | 说明 |
|---|---|---|
| **接口基址** | `https://api.deepseek.com/v1` | OpenAI 兼容接口基础 URL（需带 `/v1`） |
| **模型名** | `deepseek-chat` | 推荐使用推理或通用对话大模型 |
| **温度系数** | `0.4` | 推荐 0.3~0.7，值越低逻辑越收敛，值越高回复越丰富多样 |
| **API Key** | `sk-xxxxxxxxxxxxxxxx` | 仅在本地通过 Windows DPAPI 加密保存，绝不落盘明文 |
| **TraceMemo 基址** | `http://127.0.0.1:6131` | 本地 TraceMemo 服务监听地址（默认端口 6131） |

> 💡 **连通性探测**：配置完毕后点击【⚡ 测试 AI 连接】，系统将发送轻量握手请求，并实时回显连通状态与往返延迟（ms）。

### 2. 技能中心 (Skill Studio)
在【设置】->【🏷️ 技能中心】中：
- **官方内置认知技能**：随时在全局下拉框中切换【💼 职场与商业认知】、【❤️ 亲密关系与情感偏好】或【🎯 麦肯锡逻辑视角】；
- **📂 绑定技能文件夹 (Antigravity 风格)**：点击【绑定技能文件夹】选择工作区或任意包含技能的目录（如 `.agents/skills`），系统自动递归扫描并展示其下所有子文件夹中的 `SKILL.md` / `skill.md`；
- **🔄 重新扫描**：本地修改或新增 `SKILL.md` 文件后，点击【重新扫描】即可秒级热生效；
- **📁 打开目录**：一键在 Windows 资源管理器中打开当前绑定的技能库文件夹；
- **安全物理删除**：对自定义技能点击【🗑️ 删除】，不仅清除内存缓存，且同步安全清理磁盘对应物理文件与目录，刷新后绝不再复现。

### 3. 终极目的战略管理 (Goals)
在【设置】->【🚩 终极战略目的】中：
- 查看已保存的联系人列表与其对应的终极目的；
- 随时修改或添加目标，并在「建议」面板中直观高亮展示。

### 4. 数据、SQLite 与偏好管理
在【设置】->【🎨 偏好与系统设置】中：
- **外观主题**：自由切换深色（Dark）、浅色（Light）与跟随系统（System）；
- **悬浮窗透明度**：支持调节 60%~100% 窗口透明度；
- **OCR 采样放大倍率**：高分屏或小字体推荐设为 2x / 3x 以提升文字识别准确率；
- **己方微信昵称**：配置本人的微信昵称，确保历史对话中发言方精准识别与证据去重；
- **本地 SQLite 数据库**：聊天消息、多维画像、技能与设置统一持久化存储在 `%APPDATA%\WeChatCopilot\copilot.db` 中。开启 WAL 模式支持高并发并发读写；冷热链路消息通过 SHA256 唯一指纹幂等去重；即便未启动 TraceMemo，亦可直接秒级读取本地离线历史进行画像蒸馏与 AI 建议；
- **数据目录**：点击【📁 打开数据文件夹】，一键打开 `%APPDATA%\WeChatCopilot\`，便捷查看数据库文件 `copilot.db`、配置文件与异常日志；
- **缓存重置**：支持一键清空当前气泡识别缓存。

---

## ⌨️ 快捷键指南

| 快捷键 | 功能 | 说明 |
|---|---|---|
| <kbd>Alt</kbd> + <kbd>W</kbd> | **显示 / 隐藏悬浮窗** | 全局切换助手面板可见性 |
| <kbd>Alt</kbd> + <kbd>E</kbd> | **重新吸附微信** | 强制重新检索微信窗口并调整对其位置 |
| <kbd>Alt</kbd> + <kbd>D</kbd> | **读取当前对话** | 立即截图并执行视觉 OCR 读取最新消息 |
| <kbd>Alt</kbd> + <kbd>O</kbd> | **整窗 OCR 原文识别** | 截取完整聊天区域并展示所有文本原始识别流 |

*注：亦可通过悬浮窗顶部标题栏拖拽移动；点击【📌 吸附中/已固定】可切换自由摆放模式；鼠标移至窗口四周边缘可随意缩放窗口大小。*

---

## 🔒 隐私、安全与日志

- **绝无云端中转**：本项目完全开源，不设任何自建中转服务器，所有 AI 调用直接发送至用户配置的端点。
- **免杀与账号安全**：采用无侵入的屏幕图像处理技术与 TraceMemo 离线服务，**不注入 DLL、不 Hook 内存、不修改微信文件**，彻底杜绝封号隐患。
- **凭据硬件隔离**：API Key 经 Windows DPAPI（绑定当前 Windows 登录账户密钥派生）强加密存储，配置文件移至其他设备均无法解密。
- **全局异常拦截与日志**：
  - 运行期未捕获异常自动写入 `%APPDATA%\WeChatCopilot\crash.log`；
  - 遇到未知错误时自动穿透并提取底层原因，并提供弹窗一键直达日志文件夹。

---

## 🧪 自动化测试

项目内置完善的自动化测试矩阵，覆盖提示词工程、算法去重、大模型响应容错解析、SQLite 存储与迁移、XAML 静态/动态资源契约防御与 DPAPI 加密：

```bash
dotnet test
```

测试覆盖概览（**170 项全通过**）：
- `SqliteStorageTests`：SQLite 数据库初始化与 WAL 模式、消息 SHA256 指纹幂等去重、按时间顺序检索、画像与战略目的存取以及键值配置测试；
- `SkillStoreTests`：Antigravity 风格技能文件夹绑定、多层递归自动扫描、Markdown YAML 元数据解析、当前技能持久化与物理安全删除测试；
- `PersonaStoreTests`：画像本地持久化、SQLite 优先读取与已有旧版 JSON 文件的透明双向自动迁移测试；
- `OverlayWindowXamlResourceTests`：**XAML 静态与动态资源契约自动巡检**，守护 `OverlayWindow`、`AdviceAnalyticsWindow`、`PersonaAnalyticsWindow` 资源完整性与 `ThemeManager` 画刷全覆盖；
- `PersonaDistillerTests`：多技能心智蒸馏、自定义 Skill 动态装配、事实证据链过滤、可信度解析与 Map-Reduce 去重；
- `AiOutputParserTests`：一体化 AI 建议多风格容错解析、动态博弈维度提取、推进力与安全度量化评分；
- `PromptBuilderTests`：多轮语境深度拼接、角色标注、动态博弈维度提示词工程与装配；
- `TraceMemoClientTests`：系统号过滤（`notifymessage`/`gh_*`等）、端点探测与数据安全适配；
- `MessageSegmenterTests` & `ChatRegionCropperTests`：左右气泡几何计算与区域剪裁；
- `SettingsStoreTests` & `DpapiProtectorTests`：本地数据加解密、主题模式读写与用户隔离验证。

---

## 📄 开源许可证

本项目采用 [MIT License](LICENSE) 开源许可证。

---

## ⚠️ 免责声明

1. 本软件仅供技术交流、学习及个人日常沟通辅助使用。
2. 本软件遵守微信官方相关使用规范，不提供任何自动回复、消息群发、机器人代发等可能破坏生态平衡的功能。
3. 用户因自行配置的大模型所产生的内容合规性与使用责任由用户自行承担。

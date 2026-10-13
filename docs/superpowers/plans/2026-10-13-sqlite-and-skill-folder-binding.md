# SQLite 本地持久化与技能文件夹绑定实现计划 (SQLite & Skill Folder Binding Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 SQLite 轻量数据库本地持久化（聊天历史离线加载/增量写入、画像与技能存储）并重构技能中心为 Antigravity 风格的技能文件夹绑定机制（彻底移除手动新建/单个导入，支持递归自动发现与物理安全删除）。

**Architecture:** 
1. 引入 `Microsoft.Data.Sqlite` 统一构建 `SqliteStorage` 模块，存放于 `%AppData%/WeChatCopilot/copilot.db`，建立 `messages`（带唯一指纹去重）、`personas`、`skills`、`app_settings` 表；
2. 改造「刷新历史」与「读取当前对话」：接入 SQLite 缓存，TraceMemo 在线时增量同步入库，离线时秒级回退加载本地历史，解决断开 TraceMemo 无法查看历史与蒸馏画像的痛点；
3. 重构技能中心 UI 与 `SkillStore`：彻底去除单个文件导入与手动表单，改为绑定工作区技能目录，支持自动递归扫描展示，并实现删除时物理文件与数据库状态同步清理。

**Tech Stack:** .NET 10 (WPF), C# 13, `Microsoft.Data.Sqlite`, SQLite3 WAL Mode, xUnit.

**Spec:** 《需求文档.md》FR-3 / FR-6 / FR-7 / M6 数据持久化规格。

## Global Constraints

- 框架版本：`net10.0-windows10.0.19041.0`
- 数据库连接：使用 WAL 模式与参数化 SQL，避免锁定与注入
- 兼容性：保留对已有 JSON 画像文件的平滑迁移兼容（读 SQLite 为先，回退读取 JSON）
- 现有测试：保持既有 159 项单元测试 100% 通过，并为新增 SQLite 与技能绑定新增完整测试用例

## Review Focus

1. TraceMemo 未启动或网络超时：应用绝不崩溃或白屏，优雅降级为本地 SQLite 历史数据展示，并给出清晰提示。
2. 历史消息重复同步：重复点击「刷新历史」或「读取当前对话」时，基于 `msg_hash`（或 contact+role+text+timestamp 组合）去重，不产生重复条目。
3. 技能物理删除：删除绑定文件夹下的自定义技能时，不仅从内存/数据库移除，且安全清理对应目录或文件，防止刷新后再次自动发现重现。
4. 数据库并发访问与连接生命周期：确保连接与命令正确释放，避免文件句柄占用导致测试或应用退出异常。
5. 技能文件夹路径记忆：重启应用后自动加载并绑定上次选定的技能文件夹路径。

---

### Task 1: 技能管理重构与删除逻辑修复 (Skill Folder Binding & Safe Deletion)

**Files:**
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml`
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml.cs`
- Modify: `src/WeChatCopilot.Data/SkillStore.cs`
- Modify: `tests/WeChatCopilot.Tests/SkillStoreTests.cs`

- [x] **Step 1.1**: 在 `SkillStore.cs` 中增加技能文件夹绑定路径持久化与物理删除支持：
  - 添加 `BoundDirectory` 配置读写（默认工作区 `.agents/skills` 或 `%AppData%/WeChatCopilot/skills`）。
  - 在 `DistillSkill` 中记录源物理路径 `SourceFilePath`。
  - 重写 `DeleteCustomSkill(string id)`：若技能具有物理路径且位于绑定目录下，安全删除对应物理目录或文件，并清理缓存，防止刷新后死灰复燃。
- [x] **Step 1.2**: 编写单元测试验证文件夹绑定、扫描与物理删除后不再复现。
- [x] **Step 1.3**: 在 `OverlayWindow.xaml` 中精简技能中心 UI：
  - 移除单个导入按钮、手动新建按钮与手动新建卡片 `SettingCustomSkillCard`。
  - 新增技能文件夹路径横幅与操作按钮：`📂 绑定/切换技能文件夹`、`🔄 重新扫描`、`📁 打开文件夹`。
- [x] **Step 1.4**: 在 `OverlayWindow.xaml.cs` 中实现绑定文件夹选择、实时重扫与 UI 响应。
- [x] **Step 1.5**: 运行测试并确保 Task 1 逻辑通过。

---

### Task 2: SQLite 轻量数据库基础设施实现 (SqliteStorage Infrastructure)

**Files:**
- Modify: `src/WeChatCopilot.Data/WeChatCopilot.Data.csproj`
- Create: `src/WeChatCopilot.Data/SqliteStorage.cs`
- Create: `tests/WeChatCopilot.Tests/SqliteStorageTests.cs`

- [x] **Step 2.1**: 确认 `WeChatCopilot.Data.csproj` 包含 `Microsoft.Data.Sqlite` 依赖并还原成功。
- [x] **Step 2.2**: 编写 `SqliteStorageTests.cs`（TDD）：
  - 测试数据库建表与初始化。
  - 测试消息写入（去重指纹校验与按时间排序检索）。
  - 测试画像 Save/Load/List 往返。
  - 测试 Key-Value 设置读写。
- [x] **Step 2.3**: 实现 `SqliteStorage.cs`：
  - 支持初始化建表：`messages`、`personas`、`skills`、`app_settings`。
  - 实现 `SaveMessages`、`GetMessages`、`GetLatestMessageTimestamp`。
  - 实现 `SavePersona`、`GetPersona`、`ListPersonaNames`。
  - 实现 `SetSetting`、`GetSetting`。
- [x] **Step 2.4**: 运行 `SqliteStorageTests` 并验证全部通过。

---

### Task 3: 对话历史与画像的 SQLite 离线/在线联动 (History & Persona Offline Sync)

**Files:**
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml.cs`
- Modify: `src/WeChatCopilot.Data/PersonaStore.cs`
- Modify: `tests/WeChatCopilot.Tests/PersonaStoreTests.cs`

- [x] **Step 3.1**: 增强 `PersonaStore.cs`：优先写入并读取 `SqliteStorage`，保持与已有本地 JSON 兼容迁移。
- [x] **Step 3.2**: 在 `OverlayWindow.xaml.cs` 中重构「⚡ 刷新历史」逻辑：
  - 尝试调用 TraceMemo API 拉取最新消息；
  - 若 TraceMemo 在线：批量入库 SQLite 并展示；
  - 若 TraceMemo 离线（未启动/网络断开）：自动回退查询 SQLite 本地库并展示，提示“⚠️ TraceMemo 未启动，已自动从本地离线数据库加载历史”，蒸馏按钮正常可用！
- [x] **Step 3.3**: 在「读取当前对话」/ OCR 识别时：将捕获到的当前双方发言异步 Upsert 到 SQLite `messages` 表，确保即使不点 TraceMemo 刷新也能沉淀最新对话。
- [x] **Step 3.4**: 验证测试与离线状态下的 UI 响应。

---

### Task 4: 端到端集成验证与测试 (Verification & Regression Testing)

**Files:**
- Verify: `tests/WeChatCopilot.Tests`
- Verify: `src/WeChatCopilot.App`

- [x] **Step 4.1**: 运行 `dotnet test`，确保所有既有 159 项测试 + 新增测试全部通过（>165 项，实测 170 项 100% 通过）。
- [x] **Step 4.2**: 运行 `dotnet build` 确认 0 警告、0 错误。
- [x] **Step 4.3**: 向用户汇报完成成果与操作演示说明。

# 微信 4.x WCDB4 原生历史记录直读集成计划 (WCDB4 Native History Integration Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 WeChat Copilot 中内置与 TraceMemo 同源的微信 4.x (`xwechat_files`) WCDB 4.0 本地数据库直读引擎，无需打开 TraceMemo 客户端或通过 HTTP 6131 API 即可自动解析联系人并毫秒级拉取完整微信聊天记录。

**Architecture:**
1. 在 `WeChatCopilot.Core` 抽象统一的 `IChatHistoryProvider` 接口并扩展 `AiSettings`（支持 `WeChatDbRoot`、`EncryptedWeChatDbKey`、`NativeWcdbDllDir`）；
2. 在 `WeChatCopilot.Data` 实现 `WeChatDbKeyAndRootProvider`（自动发现 `xwechat_files` 账号根目录、处理非 ASCII 目录联接桥接、自动解密复用 `%AppData%/TraceMemo/database-keys/*.bin` 或读取 DPAPI 加密配置密钥）、`Wcdb4MessageDecoder`（基于 `ZstdSharp.Port` 解压 `0xFD2FB528` 压缩字段并清洗微信 4.x 富媒体 XML）与 `Wcdb4NativeInterop` / `Wcdb4HistoryProvider`（通过 P/Invoke 调用 `wcdb_api.dll` 只读游标接口）；
3. 通过 `CompositeChatHistoryProvider` 实现「WCDB4 原生直读 -> TraceMemo HTTP API 兜底 -> 本地 SQLite 离线缓存 -> 屏幕视觉 OCR」四级无感降级，并在设置页提供微信 4.x 本地直连状态自检与一键导入配置。

**Tech Stack:** .NET 10 (WPF), C# 13, P/Invoke (`NativeLibrary`), `ZstdSharp.Port`, `Microsoft.Data.Sqlite`, Windows DPAPI + AES-256-GCM, xUnit.

**Spec:** 用户确认的《微信 4.x + 方案 A（WCDB 4.0 原生直读）落地设计方案》。

## Global Constraints

- 框架版本：`net10.0-windows10.0.19041.0`，启用 `<Nullable>enable</Nullable>`，零警告编译（`CS0162;CS0168;CS0219` 视为错误）。
- 零侵入与只读安全：不植入任何微信进程内存操作代码；仅通过 `wcdb_api.dll` 对本地 `xwechat_files` 数据库执行查询操作，严禁执行任何 DDL/DML 写操作。
- 内存安全：每次 P/Invoke 获取的 `outJson` 非托管字符串指针必须在 `finally` 块中调用 `wcdb_free_string(ptr)` 释放；`InitProtection` 与 `wcdb_init` 全进程仅初始化一次（避免 `-1006` 错误）。
- 冷热链路隔离：任何冷链路读取失败均返回 `ChatHistoryResult(false, Empty, error)`，绝不向上抛出未捕获异常影响热链路。

## Review Focus

1. 非托管指针释放与并发访问：`wcdb_api.dll` 调用通过进程级异步锁串行化保护，`outJson` 指针在 JSON 解析异常时也必须经 `wcdb_free_string` 释放。
2. 微信 4.x Zstd 压缩消息解码：`compress_content` 为 Hex、Base64 或 raw bytes 且带 Zstd 魔数 `0xFD2FB528` 时正确解压为 UTF-8，损坏字节流回退为空串而非抛异常。
3. 路径大小写与非 ASCII 字符：`database-keys` 文件名哈希严格按 `path.resolve(accountRoot).toLowerCase()` 计算 SHA256；含非 ASCII 字符的目录自动建立 ASCII Junction 桥接。
4. 自身发送消息方向判定：同时识别 `is_send` / `computed_is_send` / `mesDes` 以及 `sender` 与当前账号 `wxid`（含去除 `_xxxx` 后缀）的匹配。
5. Native DLL 缺失或密钥未配置时的降级：自动回退至 `TraceMemoClient` 及本地 `SqliteStorage`，UI 状态栏给出清晰可读提示。

---

### Task 1: 核心接口抽象与配置模型扩展 (`IChatHistoryProvider` & `AiSettings`)

**Files:**
- Create: `src/WeChatCopilot.Core/Abstractions/IChatHistoryProvider.cs`
- Modify: `src/WeChatCopilot.Core/Models/AiSettings.cs`
- Modify: `tests/WeChatCopilot.Tests/SettingsStoreTests.cs`

- [x] **Step 1.1**: 在 `SettingsStoreTests.cs` 中新增测试验证 `AiSettings` 新增字段（`WeChatDbRoot`、`EncryptedWeChatDbKey`、`NativeWcdbDllDir`）的默认值与持久化往返。
- [x] **Step 1.2**: 创建 `IChatHistoryProvider.cs`（定义 `ChatHistoryResult`、`ChatConnectionStatus`、`FetchHistoryAsync`、`GetActiveContactNameAsync`、`TestConnectionAsync`）并扩展 `AiSettings.cs`。
- [x] **Step 1.3**: 运行 `dotnet test --filter SettingsStoreTests` 验证通过。

---

### Task 2: 微信 4.x 账号目录探测与数据库密钥提供器 (`WeChatDbKeyAndRootProvider`)

**Files:**
- Create: `src/WeChatCopilot.Data/WeChatDbKeyAndRootProvider.cs`
- Create: `tests/WeChatCopilot.Tests/WeChatDbKeyAndRootProviderTests.cs`

- [x] **Step 2.1**: 编写 `WeChatDbKeyAndRootProviderTests.cs`（TDD）：
  - 测试从模拟 `xwechat_files/wxid_abc123_4567/db_storage/session/session.db` 目录结构中自动解析 `accountRoot` 与 `wxid`（`wxid_abc123`）；
  - 测试按 `path.GetFullPath(accountRoot).ToLowerInvariant()` 计算 SHA256 并用 DPAPI + AES-256-GCM (`v10` 前缀) 解密 `database-keys/<sha256>.bin` 得到 64 位 Hex 密钥；
  - 测试从 `%AppData%/TraceMemo/settings.json` 读取 `dbRoot`；
  - 测试优先读取 `AiSettings.WeChatDbRoot` 与 `AiSettings.EncryptedWeChatDbKey`。
- [x] **Step 2.2**: 实现 `WeChatDbKeyAndRootProvider.cs`，包含目录探测、密钥解密提取、密钥格式校验（`^[0-9a-fA-F]{64}$`）与非 ASCII 路径桥接。
- [x] **Step 2.3**: 运行 `dotnet test --filter WeChatDbKeyAndRootProviderTests` 验证通过。

---

### Task 3: 微信 4.x Zstd 消息解压与联系人/富媒体解析器 (`Wcdb4MessageDecoder`)

**Files:**
- Modify: `src/WeChatCopilot.Data/WeChatCopilot.Data.csproj`
- Create: `src/WeChatCopilot.Data/Wcdb4MessageDecoder.cs`
- Create: `tests/WeChatCopilot.Tests/Wcdb4MessageDecoderTests.cs`

- [x] **Step 3.1**: 在 `WeChatCopilot.Data.csproj` 引入 `ZstdSharp.Port` NuGet 包。
- [x] **Step 3.2**: 编写 `Wcdb4MessageDecoderTests.cs`（TDD）：
  - 测试 Zstd 压缩数据（含 byte[]、Hex 字符串、Base64 字符串，魔数 `0xFD2FB528`）解压还原中文消息文本；
  - 测试收发角色判定（`is_send=1`、`mesDes=0`、或 `sender` 匹配当前账号 `wxid` 均为 `MessageRole.Outgoing`）；
  - 测试富媒体 XML 归一化（`<refermsg>` 引用回复提取正文、`<appmsg>` 分享卡片标题提取、`<patinfo>` 拍一拍、图片/语音/动画表情标签）；
  - 测试联系人解析（精确匹配与包含匹配 `remark`、`wechatNickname`、`nickname`、`wechatId`、`wxid`）与活跃单聊联系人提取（过滤 `@chatroom`、`gh_` 及系统号）。
- [x] **Step 3.3**: 实现 `Wcdb4MessageDecoder.cs`。
- [x] **Step 3.4**: 运行 `dotnet test --filter Wcdb4MessageDecoderTests` 验证通过。

---

### Task 4: WCDB 4.0 原生 P/Invoke 封装与组合历史提供器 (`Wcdb4NativeInterop`, `Wcdb4HistoryProvider`, `CompositeChatHistoryProvider`)

**Files:**
- Create: `src/WeChatCopilot.Data/Wcdb4NativeInterop.cs`
- Create: `src/WeChatCopilot.Data/Wcdb4HistoryProvider.cs`
- Create: `src/WeChatCopilot.Data/CompositeChatHistoryProvider.cs`
- Modify: `src/WeChatCopilot.Data/TraceMemoClient.cs`
- Create: `tests/WeChatCopilot.Tests/CompositeChatHistoryProviderTests.cs`

- [x] **Step 4.1**: 编写 `CompositeChatHistoryProviderTests.cs`（TDD）：
  - 测试主提供器（WCDB4 直读）成功时直接返回结果且不触发备用提供器；
  - 测试主提供器未就绪或失败时自动回退至备用提供器（`TraceMemoClient`）；
  - 测试两者均失败时返回合并诊断信息且不抛出异常。
- [x] **Step 4.2**: 实现 `Wcdb4NativeInterop.cs`：
  - 自动定位 `wcdb_api.dll`（优先检查配置目录、应用目录 `resources/wcdb/win32/x64`、以及本机已安装的 TraceMemo 资源目录如 `F:\software\TraceMemo\resources\resources\wcdb\win32\x64` 或注册表卸载路径）；
  - 预加载 `vcruntime140.dll`、`msvcp140.dll`、`SDL2.dll`、`WCDB.dll`，单次执行 `InitProtection` 与 `wcdb_init`；
  - 封装 `OpenAccount`、`SetMyWxid`、`GetSessionsJson`、`GetDisplayNamesJson`、`FetchMessagesByCursorJson`、`GetMessagesJson`、`ExecQueryJson`，确保每次 `wcdb_free_string` 释放。
- [x] **Step 4.3**: 实现 `Wcdb4HistoryProvider.cs`、`CompositeChatHistoryProvider.cs`，并令 `TraceMemoClient` 实现 `IChatHistoryProvider`。
- [x] **Step 4.4**: 将本机 WCDB 原生运行库复制到应用 `resources/wcdb/win32/x64` 与 `resources/runtime/win32` 并在 `WeChatCopilot.Data.csproj` 配置构建输出复制，确保脱离外部目录也可独立运行。
- [x] **Step 4.5**: 运行 `dotnet test --filter CompositeChatHistoryProviderTests` 验证通过。

---

### Task 5: 应用层调用切换与设置界面升级 (`WeChatCopilot.App`)

**Files:**
- Modify: `src/WeChatCopilot.App/OverlayWindow.Chat.cs`
- Modify: `src/WeChatCopilot.App/OverlayWindow.Persona.cs`
- Modify: `src/WeChatCopilot.App/OverlayWindow.Settings.cs`
- Modify: `src/WeChatCopilot.App/OverlayWindow.xaml`
- Modify: `src/WeChatCopilot.App/SettingsWindow.xaml.cs`

- [x] **Step 5.1**: 在 `OverlayWindow.Chat.cs` 与 `OverlayWindow.Persona.cs` 中，将 `TraceMemoClient` 替换为 `CompositeChatHistoryProvider`，支持直读微信 4.x 本地数据库并自动持久化到 `SqliteStorage`，更新状态栏文案以反映“微信 4.x 本地库直读”或“TraceMemo API”来源。
- [x] **Step 5.2**: 在 `OverlayWindow.xaml`、`OverlayWindow.Settings.cs`、`SettingsWindow.xaml.cs` 中新增微信 4.x 本地数据库直连配置项（账号数据目录 `WeChatDbRoot`、64 位数据库密钥 `WeChatDbKey`、以及「🔍 自动探测本机配置」和「⚡ 测试微信 4.x 直连」按钮），保留所有字段在保存时不丢失。
- [x] **Step 5.3**: 运行 `dotnet build` 与 `dotnet test` 验证 UI 编译与现有测试通过。

---

### Task 6: 真机端到端直读验证与回归测试

**Files:**
- Modify: `tools/TmProbe/Program.cs`

- [x] **Step 6.1**: 更新 `tools/TmProbe/Program.cs` 增加 `--wcdb4` 本地直读验证模式，在不依赖 6131 端口的情况下直接测试读取本机 `E:\xwechat_files\wxid_4ev1rkdk0jgm22_1889` 的活跃联系人与最近聊天记录。
- [x] **Step 6.2**: 运行全量单元测试 `dotnet test` 与构建验证 `dotnet build`。

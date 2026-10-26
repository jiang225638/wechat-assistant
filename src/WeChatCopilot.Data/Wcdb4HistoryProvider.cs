using System.Text.Json;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// 微信 4.x (xwechat_files) WCDB 4.0 原生本地数据库直读提供器（方案 A 主数据源）。
/// 无需打开 TraceMemo 客户端或通过 HTTP 端口，直接在进程内调用 WCDB 4.0 只读游标解析会话与聊天记录。
/// </summary>
public sealed class Wcdb4HistoryProvider : IChatHistoryProvider
{
    private static readonly object s_cacheLock = new();
    private static string s_cachedAccountRoot = string.Empty;
    private static DateTime s_cachedContactsAt = DateTime.MinValue;
    private static string? s_cachedContactRowsJson;

    private readonly AiSettings _settings;

    public string SourceName => "微信 4.x 本地库直读";

    public Wcdb4HistoryProvider(AiSettings? settings = null)
    {
        _settings = settings ?? new SettingsStore().Load();
    }

    public async Task<ChatHistoryResult> FetchHistoryAsync(string contactName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), "联系人名称不能为空", SourceName);
        }

        try
        {
            return await Task.Run(() => FetchHistoryCore(contactName.Trim()), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), ex.Message, SourceName);
        }
    }

    public async Task<string?> GetActiveContactNameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(GetActiveContactNameCore, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WeChatSessionSummary>> GetRecentSessionsAsync(int maxCount = 40, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => GetRecentSessionsCore(maxCount), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<WeChatSessionSummary>();
        }
    }

    public async Task<ChatConnectionStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(TestConnectionCore, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ChatConnectionStatus(false, string.Empty, string.Empty, 0, ex.Message, SourceName);
        }
    }

    public void Dispose()
    {
    }

    private ChatHistoryResult FetchHistoryCore(string contactName)
    {
        if (!WeChatDbKeyAndRootProvider.TryResolveConfiguration(
            _settings,
            out string accountRoot,
            out string sessionDbPath,
            out string wxid,
            out string key,
            out string? cfgError))
        {
            return new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), cfgError, SourceName);
        }

        if (!Wcdb4NativeInterop.TryGetOrCreate(_settings.NativeWcdbDllDir, out var interop, out string? dllError) || interop is null)
        {
            return new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), dllError, SourceName);
        }

        string nativeSessionDb = GetNativeSessionDbPath(accountRoot, sessionDbPath);
        long handle = interop.EnsureAccountOpen(nativeSessionDb, key, wxid);

        var sessions = LoadHydratedSessions(interop, handle, accountRoot);
        string? targetUsername = Wcdb4MessageDecoder.ResolveSessionUsername(sessions, contactName);
        if (string.IsNullOrWhiteSpace(targetUsername))
        {
            return new ChatHistoryResult(
                false,
                Array.Empty<HistoryMessage>(),
                $"微信 4.x 本地数据库中未找到联系人：{contactName}",
                SourceName);
        }

        string rawAccountName = Path.GetFileName(accountRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string[] myCandidates = new[] { wxid, rawAccountName, WeChatDbKeyAndRootProvider.CleanAccountDirName(rawAccountName) }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var batches = interop.FetchMessageBatchesJson(handle, targetUsername, maxRows: 2000);
        var parsedMessages = new List<(HistoryMessage Msg, int Index)>();
        int idx = 0;

        foreach (string batchJson in batches)
        {
            try
            {
                using var doc = JsonDocument.Parse(batchJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var row in doc.RootElement.EnumerateArray())
                {
                    var msg = Wcdb4MessageDecoder.ParseMessageRow(row, myCandidates);
                    if (msg is not null)
                    {
                        parsedMessages.Add((msg, idx++));
                    }
                }
            }
            catch
            {
            }
        }

        var sorted = parsedMessages
            .OrderBy(x => x.Msg.Timestamp ?? DateTime.MinValue)
            .ThenBy(x => x.Index)
            .Select(x => x.Msg)
            .ToList();

        return new ChatHistoryResult(true, sorted, null, SourceName);
    }

    private string? GetActiveContactNameCore()
    {
        if (!WeChatDbKeyAndRootProvider.TryResolveConfiguration(
            _settings,
            out string accountRoot,
            out string sessionDbPath,
            out string wxid,
            out string key,
            out _))
        {
            return null;
        }

        if (!Wcdb4NativeInterop.TryGetOrCreate(_settings.NativeWcdbDllDir, out var interop, out _) || interop is null)
        {
            return null;
        }

        string nativeSessionDb = GetNativeSessionDbPath(accountRoot, sessionDbPath);
        long handle = interop.EnsureAccountOpen(nativeSessionDb, key, wxid);

        var sessions = LoadHydratedSessions(interop, handle, accountRoot, activeOnly: true);
        return Wcdb4MessageDecoder.SelectActiveSingleChatContact(sessions, _settings.SelfNickname);
    }

    private IReadOnlyList<WeChatSessionSummary> GetRecentSessionsCore(int maxCount)
    {
        if (!WeChatDbKeyAndRootProvider.TryResolveConfiguration(
            _settings,
            out string accountRoot,
            out string sessionDbPath,
            out string wxid,
            out string key,
            out _))
        {
            return Array.Empty<WeChatSessionSummary>();
        }

        if (!Wcdb4NativeInterop.TryGetOrCreate(_settings.NativeWcdbDllDir, out var interop, out _) || interop is null)
        {
            return Array.Empty<WeChatSessionSummary>();
        }

        string nativeSessionDb = GetNativeSessionDbPath(accountRoot, sessionDbPath);
        long handle = interop.EnsureAccountOpen(nativeSessionDb, key, wxid);

        var sessions = LoadHydratedSessions(interop, handle, accountRoot, activeOnly: maxCount <= 40);
        return Wcdb4MessageDecoder.BuildRecentSessionSummaries(sessions, _settings.SelfNickname, maxCount);
    }

    private ChatConnectionStatus TestConnectionCore()
    {
        if (!WeChatDbKeyAndRootProvider.TryResolveConfiguration(
            _settings,
            out string accountRoot,
            out string sessionDbPath,
            out string wxid,
            out string key,
            out string? cfgError))
        {
            return new ChatConnectionStatus(false, accountRoot, wxid, 0, cfgError ?? "配置未就绪", SourceName);
        }

        if (!Wcdb4NativeInterop.TryGetOrCreate(_settings.NativeWcdbDllDir, out var interop, out string? dllError) || interop is null)
        {
            return new ChatConnectionStatus(false, accountRoot, wxid, 0, dllError ?? "WCDB4 组件未就绪", SourceName);
        }

        string nativeSessionDb = GetNativeSessionDbPath(accountRoot, sessionDbPath);
        long handle = interop.EnsureAccountOpen(nativeSessionDb, key, wxid);
        var sessions = LoadHydratedSessions(interop, handle, accountRoot);

        return new ChatConnectionStatus(
            true,
            accountRoot,
            wxid,
            sessions.Count,
            $"已成功直连微信 4.x 本地数据库！\n• 账号 wxid：{wxid}\n• 数据目录：{accountRoot}\n• 识别会话/联系人数：{sessions.Count} 个\n• 原生引擎：{interop.LoadedLibraryPath}",
            SourceName);
    }

    private static List<Wcdb4SessionItem> LoadHydratedSessions(
        Wcdb4NativeInterop interop,
        long handle,
        string accountRoot,
        bool activeOnly = false)
    {
        string sessionsJson = interop.GetSessionsJson(handle);
        var rawSessions = Wcdb4MessageDecoder.ParseSessionsJson(sessionsJson);

        List<Wcdb4SessionItem> targetSessions;
        if (activeOnly)
        {
            // 优先筛选非群聊、非公众号的真实单聊活跃会话
            targetSessions = rawSessions
                .Where(s => !string.IsNullOrWhiteSpace(s.Username) &&
                            !s.Username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) &&
                            !s.Username.StartsWith("gh_", StringComparison.OrdinalIgnoreCase) &&
                            !TraceMemoClient.IsSystemContact(s.Username, s.Nickname))
                .OrderByDescending(s => s.LastTimestamp)
                .Take(60)
                .ToList();
        }
        else
        {
            targetSessions = rawSessions;
        }

        string? contactRowsJson = GetCachedOrQueryContactsJson(interop, handle, accountRoot);

        // 提取候选人的用户名列表，批量获取显示名
        string[] candidateUsernames = targetSessions
            .Take(40)
            .Select(s => s.Username)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string? displayNamesJson = candidateUsernames.Length > 0
            ? interop.GetDisplayNamesJson(handle, JsonSerializer.Serialize(candidateUsernames))
            : null;

        var enriched = Wcdb4MessageDecoder.EnrichSessions(
            targetSessions,
            displayNamesJson,
            contactRowsJson,
            includeAllContacts: !activeOnly);

        return enriched;
    }

    private static string? GetCachedOrQueryContactsJson(Wcdb4NativeInterop interop, long handle, string accountRoot)
    {
        lock (s_cacheLock)
        {
            if (s_cachedContactRowsJson is not null &&
                string.Equals(s_cachedAccountRoot, accountRoot, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.UtcNow - s_cachedContactsAt).TotalSeconds < 120)
            {
                return s_cachedContactRowsJson;
            }
        }

        string? queried = interop.ExecQueryJson(
            handle,
            "contact",
            string.Empty,
            "SELECT username, nick_name, remark, alias FROM contact");

        if (!string.IsNullOrWhiteSpace(queried))
        {
            lock (s_cacheLock)
            {
                s_cachedAccountRoot = accountRoot;
                s_cachedContactsAt = DateTime.UtcNow;
                s_cachedContactRowsJson = queried;
            }
        }

        return queried;
    }

    private static string GetNativeSessionDbPath(string accountRoot, string sessionDbPath)
    {
        string nativeRoot = WeChatDbKeyAndRootProvider.ResolveWindowsNativeAccountRoot(accountRoot);
        string rel = Path.GetRelativePath(accountRoot, sessionDbPath);
        return Path.Combine(nativeRoot, rel);
    }
}

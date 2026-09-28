using System.Net.Http.Headers;
using System.Text.Json;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// M5 冷链路主来源（FR-8）：TraceMemo Local HTTP API 客户端。
/// 支持两种协议规范：
/// 1. TraceMemo 原生 API (推荐)：
///    - GET api/v1/resolve?q={name} 或 api/v1/contact 获取联系人 wxid
///    - GET api/v1/chatlog?talker={wxid} 获取完整聊天记录
///    - 自动提取并注入 TraceMemo safeStorage Bearer Token
/// 2. 通用 REST 端点 (兜底/测试)：
///    - GET api/contacts
///    - GET api/contacts/{id}/messages
/// 任何异常都降级为 Success=false + Error，不抛出：冷链路故障不影响热链路。
/// </summary>
public sealed class TraceMemoClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="baseUrl">TraceMemo Local API 基址（如 http://127.0.0.1:6131）。</param>
    /// <param name="token">Bearer Token；若为 null 则自动通过 <see cref="TraceMemoTokenProvider"/> 提取。</param>
    /// <param name="handler">测试用自定义 handler；缺省自建 HttpClient。</param>
    public TraceMemoClient(string baseUrl, string? token = null, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = handler is null;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);

        if (string.IsNullOrEmpty(token) && handler is null)
        {
            if (TraceMemoTokenProvider.TryGetToken(out string detectedToken, out _))
            {
                token = detectedToken;
            }
        }

        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>便捷重载：不指定 token，提供自定义 handler（供单元测试使用）。</summary>
    public TraceMemoClient(string baseUrl, HttpMessageHandler? handler)
        : this(baseUrl, null, handler)
    {
    }

    /// <summary>拉取结果：成功带消息列表；失败带错误描述。</summary>
    public sealed record TraceMemoHistory(bool Success, IReadOnlyList<HistoryMessage> Messages, string? Error);

    /// <summary>按联系人名拉取完整历史：先解析联系人 id，再取消息。</summary>
    public async Task<TraceMemoHistory> FetchHistoryAsync(string contactName, CancellationToken cancellationToken = default)
    {
        try
        {
            string? id = await ResolveContactIdAsync(contactName, cancellationToken);
            if (id is null)
            {
                return Fail("TraceMemo 中未找到联系人：" + contactName);
            }

            var messages = await FetchMessagesAsync(id, cancellationToken);
            return new TraceMemoHistory(true, messages, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<string?> ResolveContactIdAsync(string contactName, CancellationToken cancellationToken)
    {
        if (_ownsHttp)
        {
            // 生产环境：优先 TraceMemo 原生 api/v1/resolve: api/v1/resolve?q=...
            try
            {
                using var resolveDoc = await TryGetJsonAsync("api/v1/resolve?q=" + Uri.EscapeDataString(contactName), cancellationToken);
                if (resolveDoc is not null && resolveDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string id = GetStringAny(resolveDoc.RootElement, "wxid", "id", "contactId", "m_nsUsrName");
                    if (!string.IsNullOrEmpty(id))
                    {
                        return id;
                    }
                }
            }
            catch
            {
                // 忽略，尝试列表搜索
            }

            // 尝试 api/v1/contact
            try
            {
                using var v1Contacts = await TryGetJsonAsync("api/v1/contact", cancellationToken);
                if (v1Contacts is not null)
                {
                    string? match = MatchContactInJson(v1Contacts.RootElement, contactName);
                    if (match is not null)
                    {
                        return match;
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }

        // 通用列表端点：api/contacts
        using var contacts = await GetJsonAsync("api/contacts", cancellationToken);
        return MatchContactInJson(contacts.RootElement, contactName);
    }

    private static string? MatchContactInJson(JsonElement root, string contactName)
    {
        JsonElement.ArrayEnumerator enumerator = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("contacts", out var cArr) && cArr.ValueKind == JsonValueKind.Array => cArr.EnumerateArray(),
            JsonValueKind.Object when root.TryGetProperty("items", out var iArr) && iArr.ValueKind == JsonValueKind.Array => iArr.EnumerateArray(),
            _ => default
        };

        foreach (JsonElement el in enumerator)
        {
            string name = GetStringAny(el, "name", "nickname", "remark", "m_nsNickName", "m_nsRemark", "alias");
            if (string.Equals(name, contactName, StringComparison.OrdinalIgnoreCase))
            {
                string id = GetStringAny(el, "id", "wxid", "contactId", "m_nsUsrName");
                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<HistoryMessage>> FetchMessagesAsync(string id, CancellationToken cancellationToken)
    {
        JsonDocument? messagesDoc = null;

        if (_ownsHttp)
        {
            // 生产环境：优先 TraceMemo 原生 api/v1/chatlog?talker=...
            try
            {
                messagesDoc = await TryGetJsonAsync("api/v1/chatlog?talker=" + Uri.EscapeDataString(id), cancellationToken);
            }
            catch
            {
                // 忽略，回退到通用端点
            }
        }

        // 回退到通用 REST 端点
        messagesDoc ??= await GetJsonAsync("api/contacts/" + Uri.EscapeDataString(id) + "/messages", cancellationToken);


        using (messagesDoc)
        {
            var list = new List<HistoryMessage>();
            JsonElement root = messagesDoc.RootElement;
            JsonElement.ArrayEnumerator enumerator = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray(),
                JsonValueKind.Object when root.TryGetProperty("messages", out var mArr) && mArr.ValueKind == JsonValueKind.Array => mArr.EnumerateArray(),
                JsonValueKind.Object when root.TryGetProperty("items", out var iArr) && iArr.ValueKind == JsonValueKind.Array => iArr.EnumerateArray(),
                _ => default
            };

            foreach (JsonElement el in enumerator)
            {
                string text = GetStringAny(el, "text", "content", "message", "msg", "m_nsContent");
                if (text.Length == 0 && el.TryGetProperty("contentData", out var cd) && cd.ValueKind == JsonValueKind.Object)
                {
                    text = GetStringAny(cd, "title", "des", "text");
                }

                if (text.Length == 0)
                {
                    continue;
                }

                DateTime? ts = null;
                string tsRaw = GetStringAny(el, "ts", "time", "timestamp", "CreateTime", "m_uiCreateTime", "createTime");
                if (tsRaw.Length > 0 && long.TryParse(tsRaw, out long epochSeconds))
                {
                    ts = DateTime.UnixEpoch.AddSeconds(epochSeconds);
                }
                else if (tsRaw.Length > 0 && DateTime.TryParse(tsRaw, out DateTime parsed))
                {
                    ts = parsed;
                }

                MessageRole role = ExtractRole(el);
                list.Add(new HistoryMessage(role, text, ts));
            }

            return list;
        }
    }

    /// <summary>
    /// 从 TraceMemo 的 recent_chat 活跃会话流中获取当前正在沟通的单聊好友昵称（排在首位的非群聊、非公众号好友）。
    /// </summary>
    public async Task<string?> GetActiveContactNameAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await TryGetJsonAsync("api/v1/recent_chat", cancellationToken);
            if (doc is null)
            {
                return null;
            }

            JsonElement root = doc.RootElement;
            JsonElement.ArrayEnumerator enumerator = root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray(),
                JsonValueKind.Object when root.TryGetProperty("items", out var iArr) && iArr.ValueKind == JsonValueKind.Array => iArr.EnumerateArray(),
                _ => default
            };

            foreach (JsonElement item in enumerator)
            {
                string type = GetStringAny(item, "type");
                if (type.Equals("group", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("isOfficialAccount", out var isOfficial) && isOfficial.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                string nick = GetStringAny(item, "m_nsNickName", "remark", "nickname", "name");
                string wxid = GetStringAny(item, "wxid", "m_nsUsrName");

                if (wxid is "brandsessionholder" or "fmessage" or "medianote" or "floatbottle" or "qmessage" or "weixin" or "newsapp")
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(nick))
                {
                    return nick;
                }
            }
        }
        catch
        {
            // 忽略读取异常
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage resp = await _http.GetAsync(path, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        string body = await resp.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage resp = await _http.GetAsync(path, cancellationToken);
        string body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Truncate(body)}");
        }

        return JsonDocument.Parse(body);
    }

    private static TraceMemoHistory Fail(string error) =>
        new(false, Array.Empty<HistoryMessage>(), error);

    private static string Truncate(string s) =>
        s.Length <= 300 ? s : s[..300] + "...";

    private static string GetStringAny(JsonElement el, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (el.TryGetProperty(key, out JsonElement v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                return v.ToString();
            }
        }

        return string.Empty;
    }

    private static MessageRole ExtractRole(JsonElement el)
    {
        // 1. 优先检查布尔/整型字段: isSender / is_sender / isSend
        string[] senderKeys = { "isSender", "is_sender", "isSend", "issender", "issend" };
        foreach (string key in senderKeys)
        {
            if (el.TryGetProperty(key, out JsonElement val))
            {
                if (val.ValueKind == JsonValueKind.True) return MessageRole.Outgoing;
                if (val.ValueKind == JsonValueKind.False) return MessageRole.Incoming;
                if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int n))
                {
                    return n == 1 ? MessageRole.Outgoing : MessageRole.Incoming;
                }
                if (val.ValueKind == JsonValueKind.String)
                {
                    string s = val.GetString()?.Trim().ToLowerInvariant() ?? "";
                    if (s is "1" or "true" or "yes" or "self" or "outgoing" or "我") return MessageRole.Outgoing;
                    if (s is "0" or "false" or "no" or "other" or "incoming" or "对方") return MessageRole.Incoming;
                }
            }
        }

        // 2. 检查微信底层 Des 字段（0=发出，1=接收）
        if (el.TryGetProperty("Des", out var desVal) && desVal.ValueKind == JsonValueKind.Number && desVal.TryGetInt32(out int des))
        {
            return des == 0 ? MessageRole.Outgoing : MessageRole.Incoming;
        }

        // 3. 检查字符串 role / sender 描述
        string roleStr = GetStringAny(el, "role", "sender");
        if (!string.IsNullOrWhiteSpace(roleStr))
        {
            var mapped = MapRole(roleStr);
            if (mapped != MessageRole.Unknown)
            {
                return mapped;
            }
        }

        return MessageRole.Incoming;
    }

    private static MessageRole MapRole(string role) => role.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "outgoing" or "self" or "me" or "我" or "send" or "sent" => MessageRole.Outgoing,
        "0" or "false" or "incoming" or "other" or "friend" or "对方" or "recv" or "received" => MessageRole.Incoming,
        _ => MessageRole.Unknown
    };
}

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using ZstdSharp;

namespace WeChatCopilot.Data;

/// <summary>
/// 微信 4.x WCDB 会话项模型。
/// </summary>
public sealed record Wcdb4SessionItem(
    string Username,
    string Nickname,
    string Remark,
    string Alias,
    long LastTimestamp,
    int UnreadCount = 0,
    string Summary = "");

/// <summary>
/// 微信 4.x (WCDB 4.0) 消息解压（Zstd 0xFD2FB528）、富媒体 XML 清洗与会话联系人解析器。
/// 完整对齐 TraceMemo 的 wcdb4-client.ts 与 chat-service.ts 解析行为。
/// </summary>
public static class Wcdb4MessageDecoder
{
    private const uint ZstdMagicNumber = 0xFD2FB528u;

    private static readonly Regex s_hexRegex = new("^[0-9a-fA-F]+$", RegexOptions.Compiled);
    private static readonly Regex s_base64Regex = new("^[A-Za-z0-9+/]+={0,2}$", RegexOptions.Compiled);
    private static readonly Regex s_xmlTitleRegex = new(@"<title>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_referMsgBlockRegex = new(@"<refermsg\b[^>]*>(.*?)</refermsg>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_referContentRegex = new(@"<content>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</content>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_referTypeRegex = new(@"<type>(\d+)</type>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_emojiMd5Regex = new(@"md5=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_emojiCdnRegex = new(@"cdnurl=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_imgMd5Regex = new(@"md5=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_xmlUrlRegex = new(@"<url>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</url>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_xmlDesRegex = new(@"<des>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</des>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 解码可能经过 Zstd 压缩（或 Hex / Base64 编码）的微信 4.x 消息内容。
    /// </summary>
    public static string DecodeMaybeCompressed(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string trimmed = raw.Trim();
        if (trimmed.All(char.IsDigit))
        {
            return trimmed;
        }

        if (trimmed.Length > 16 && LooksLikeHex(trimmed))
        {
            try
            {
                byte[] bytes = Convert.FromHexString(trimmed);
                if (IsZstdPayload(bytes))
                {
                    return DecompressZstd(bytes);
                }

                string decoded = DecodeReadableUtf8(bytes);
                if (decoded.Length > 0)
                {
                    return decoded;
                }
            }
            catch
            {
                // 回退到原始字符串
            }
        }

        if (trimmed.Length > 16 && LooksLikeBase64(trimmed))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(trimmed);
                if (IsZstdPayload(bytes))
                {
                    return DecompressZstd(bytes);
                }

                string decoded = DecodeReadableUtf8(bytes);
                if (decoded.Length > 0)
                {
                    return decoded;
                }
            }
            catch
            {
                // 回退到原始字符串
            }
        }

        return trimmed;
    }

    /// <summary>
    /// 解码 JsonElement 字段（支持 string、number、Buffer byte 数组）。
    /// </summary>
    public static string DecodeJsonValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return DecodeMaybeCompressed(element.GetString());

            case JsonValueKind.Number:
                return element.ToString();

            case JsonValueKind.Array:
            {
                byte[]? bytes = TryReadByteArray(element);
                return bytes is not null ? DecodeBinaryContent(bytes) : string.Empty;
            }

            case JsonValueKind.Object:
            {
                if (element.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    byte[]? bytes = TryReadByteArray(dataProp);
                    return bytes is not null ? DecodeBinaryContent(bytes) : string.Empty;
                }
                return string.Empty;
            }

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// 解析单行 WCDB4 消息 JSON 为 <see cref="HistoryMessage"/>。
    /// </summary>
    public static HistoryMessage? ParseMessageRow(JsonElement row, IReadOnlyList<string> myUsernameCandidates)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // 1. 解码 compress_content 与 message_content（优先使用 compress_content）
        string content = string.Empty;
        if (TryGetPropertyIgnoreCase(row, out var compressEl,
            "compress_content", "compressContent", "compressed_content",
            "msg_compress_content", "msgCompressContent", "WCDB_CT_compress_content", "WCDB_CT_compressContent"))
        {
            content = DecodeJsonValue(compressEl);
        }

        if (string.IsNullOrWhiteSpace(content) &&
            TryGetPropertyIgnoreCase(row, out var msgEl,
                "message_content", "messageContent", "content", "msg_content", "msgContent", "WCDB_CT_message_content"))
        {
            content = DecodeJsonValue(msgEl);
        }

        // 2. 消息类型（微信 4.x 引用消息等可能带高 32 位标志，取低 32 位）
        int msgType = NormalizeMsgType(PickString(row,
            "local_type", "localType", "message_type", "messageType", "msg_type", "msgType", "type", "WCDB_CT_local_type"));

        // 3. 提取发送者与判定收发方向
        string sender = PickString(row,
            "sender_username", "senderUsername", "sender", "fromUsername", "from_username", "WCDB_CT_sender_username");
        if (string.IsNullOrEmpty(sender))
        {
            sender = ExtractSenderFromContentPrefix(content, myUsernameCandidates);
        }

        bool isSend = ExtractIsSend(row, sender, myUsernameCandidates);

        // 4. 清洗消息内容（去除 wxid 前缀、解析引用消息/分享卡片/表情/语音标签）
        string cleanedText = NormalizeWeChat4MessageContent(content, msgType);
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            return null;
        }

        // 5. 解析时间戳
        long createTimeSec = PickTimestampSeconds(row,
            "create_time", "createTime", "msg_create_time", "msgCreateTime", "time", "WCDB_CT_create_time");
        DateTime? timestamp = createTimeSec > 0
            ? DateTimeOffset.FromUnixTimeSeconds(createTimeSec).UtcDateTime
            : null;

        return new HistoryMessage(
            isSend ? MessageRole.Outgoing : MessageRole.Incoming,
            cleanedText,
            timestamp);
    }

    /// <summary>
    /// 解析 wcdb_get_sessions 返回的 JSON 为会话列表。
    /// </summary>
    public static List<Wcdb4SessionItem> ParseSessionsJson(string? sessionsJson)
    {
        var list = new List<Wcdb4SessionItem>();
        if (string.IsNullOrWhiteSpace(sessionsJson))
        {
            return list;
        }

        try
        {
            using var doc = JsonDocument.Parse(sessionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string username = PickString(row,
                    "username", "user_name", "userName", "m_nsUsrName", "usrName", "UsrName", "talker", "sessionId", "session_id");
                if (string.IsNullOrWhiteSpace(username))
                {
                    continue;
                }

                string nickname = PickString(row,
                    "nickname", "nickName", "displayName", "display_name", "wechatNickname", "wechat_nickname", "m_nsNickName", "name");
                string remark = PickString(row,
                    "remark", "remarkName", "remark_name", "contactRemark", "contact_remark");
                string alias = PickString(row,
                    "alias", "aliasName", "alias_name", "m_nsAlias", "wechatId", "wechat_id", "account");
                long lastTs = PickTimestampSeconds(row,
                    "last_timestamp", "lastTimestamp", "sort_timestamp", "sortTimestamp", "timestamp");
                int unreadCount = 0;
                if (int.TryParse(PickString(row, "unread_count", "unreadCount", "m_uUnReadCount", "unReadCount"), out int parsedUnread) && parsedUnread > 0)
                {
                    unreadCount = parsedUnread;
                }
                string summary = PickString(row, "summary", "content", "last_msg_summary", "lastMessageSummary");

                list.Add(new Wcdb4SessionItem(username, nickname, remark, alias, lastTs, unreadCount, summary));
            }
        }
        catch
        {
        }

        return list;
    }

    /// <summary>
    /// 将 wcdb_get_display_names 和 contact 表查询出的昵称/备注/微信号合并到会话列表中。
    /// </summary>
    public static List<Wcdb4SessionItem> EnrichSessions(
        IReadOnlyList<Wcdb4SessionItem> sessions,
        string? displayNamesJson,
        string? contactRowsJson,
        bool includeAllContacts = false)
    {
        var displayMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(displayNamesJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(displayNamesJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        string val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            displayMap[prop.Name] = val.Trim();
                        }
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string u = PickString(el, "username", "user_name", "userName", "m_nsUsrName");
                        string n = PickString(el, "nickname", "displayName", "display_name", "remark", "name");
                        if (!string.IsNullOrEmpty(u) && !string.IsNullOrEmpty(n))
                        {
                            displayMap[u] = n;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        var contactMap = new Dictionary<string, (string Nick, string Remark, string Alias)>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(contactRowsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(contactRowsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string u = PickString(el, "username", "user_name", "userName", "m_nsUsrName");
                        if (string.IsNullOrEmpty(u))
                        {
                            continue;
                        }

                        string nick = PickString(el, "nick_name", "nickName", "nickname", "m_nsNickName");
                        string remark = PickString(el, "remark", "remark_name", "remarkName");
                        string alias = PickString(el, "alias", "wechat_id", "wechatId");
                        contactMap[u] = (nick, remark, alias);
                    }
                }
            }
            catch
            {
            }
        }

        var enriched = new List<Wcdb4SessionItem>(sessions.Count);
        foreach (var s in sessions)
        {
            string nick = s.Nickname;
            string remark = s.Remark;
            string alias = s.Alias;

            if (contactMap.TryGetValue(s.Username, out var cInfo))
            {
                if (!string.IsNullOrWhiteSpace(cInfo.Nick)) nick = cInfo.Nick;
                if (!string.IsNullOrWhiteSpace(cInfo.Remark)) remark = cInfo.Remark;
                if (!string.IsNullOrWhiteSpace(cInfo.Alias)) alias = cInfo.Alias;
            }

            if (displayMap.TryGetValue(s.Username, out string? disp) && !string.IsNullOrWhiteSpace(disp))
            {
                if (string.IsNullOrWhiteSpace(nick) || nick == s.Username)
                {
                    nick = disp;
                }
            }

            enriched.Add(new Wcdb4SessionItem(s.Username, nick, remark, alias, s.LastTimestamp, s.UnreadCount, s.Summary));
        }

        // 若允许，将 contact 表中尚未出现在近期 session 里的联系人追加进去以便按名字搜索历史
        if (includeAllContacts)
        {
            var existingUsers = new HashSet<string>(enriched.Select(x => x.Username), StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in contactMap)
            {
                if (!existingUsers.Contains(kvp.Key))
                {
                    enriched.Add(new Wcdb4SessionItem(kvp.Key, kvp.Value.Nick, kvp.Value.Remark, kvp.Value.Alias, 0, 0, string.Empty));
                }
            }
        }

        return enriched;
    }

    /// <summary>
    /// 根据输入的联系人名称（备注、昵称、微信号或 wxid）解析对应的会话 username (wxid)。
    /// 优先精确匹配，其次包含匹配（与 TraceMemo resolveMd5 保持一致）。
    /// </summary>
    public static string? ResolveSessionUsername(IReadOnlyList<Wcdb4SessionItem> sessions, string? query)
    {
        string trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0 || sessions.Count == 0)
        {
            return null;
        }

        // 1. 精确匹配
        foreach (var s in sessions)
        {
            if (string.Equals(s.Remark, trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Nickname, trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Username, trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Alias, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return s.Username;
            }
        }

        // 2. 包含匹配
        foreach (var s in sessions)
        {
            if ((!string.IsNullOrEmpty(s.Remark) && s.Remark.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(s.Nickname) && s.Nickname.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(s.Alias) && s.Alias.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) ||
                s.Username.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return s.Username;
            }
        }

        return null;
    }

    /// <summary>
    /// 从会话列表中选出当前最活跃的真实单聊好友显示名（优先返回备注，过滤群聊、公众号与系统号）。
    /// </summary>
    public static string? SelectActiveSingleChatContact(IReadOnlyList<Wcdb4SessionItem> sessions, string? selfNickname = null)
    {
        foreach (var s in sessions)
        {
            if (string.IsNullOrWhiteSpace(s.Username))
            {
                continue;
            }

            if (s.Username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) ||
                s.Username.StartsWith("gh_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TraceMemoClient.IsSystemContact(s.Username, s.Nickname) ||
                TraceMemoClient.IsSystemContact(s.Username, s.Remark))
            {
                continue;
            }

            string candidate = !string.IsNullOrWhiteSpace(s.Remark) ? s.Remark.Trim() : s.Nickname.Trim();
            if (string.IsNullOrWhiteSpace(candidate) || candidate == s.Username)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selfNickname) &&
                string.Equals(candidate, selfNickname.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// 从已补全的会话列表中构建最近单聊好友下拉摘要列表（含备注名、昵称、未读数与最近活跃时间，自动过滤群聊与系统号）。
    /// </summary>
    public static List<WeChatSessionSummary> BuildRecentSessionSummaries(
        IReadOnlyList<Wcdb4SessionItem> sessions,
        string? selfNickname = null,
        int maxCount = 40)
    {
        var result = new List<WeChatSessionSummary>();
        if (sessions.Count == 0 || maxCount <= 0)
        {
            return result;
        }

        var ordered = sessions
            .Where(s => !string.IsNullOrWhiteSpace(s.Username))
            .Where(s => s.LastTimestamp > 0)
            .OrderByDescending(s => s.LastTimestamp);

        foreach (var s in ordered)
        {
            if (result.Count >= maxCount)
            {
                break;
            }

            if (s.Username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase) ||
                s.Username.StartsWith("gh_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TraceMemoClient.IsSystemContact(s.Username, s.Nickname) ||
                TraceMemoClient.IsSystemContact(s.Username, s.Remark))
            {
                continue;
            }

            string remark = (s.Remark ?? string.Empty).Trim();
            string nickname = (s.Nickname ?? string.Empty).Trim();
            string displayName = !string.IsNullOrWhiteSpace(remark) ? remark : nickname;
            if (string.IsNullOrWhiteSpace(displayName) || displayName == s.Username)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(selfNickname) &&
                string.Equals(displayName, selfNickname.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTime? lastActive = s.LastTimestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(s.LastTimestamp).UtcDateTime
                : null;

            result.Add(new WeChatSessionSummary(
                s.Username,
                displayName,
                nickname,
                remark,
                s.UnreadCount,
                s.Summary ?? string.Empty,
                lastActive));
        }

        return result;
    }

    private static string NormalizeWeChat4MessageContent(string rawContent, int msgType)
    {
        string text = (rawContent ?? string.Empty).Trim();

        // 去除可能附带的发送者前缀 (如 "wxid_xxx:\n正文")
        int newlineIdx = text.IndexOf('\n');
        if (newlineIdx > 0 && newlineIdx < 80)
        {
            string firstLine = text[..newlineIdx].Trim();
            if (firstLine.EndsWith(':') && !firstLine.Contains(' ') && !firstLine.Contains('<'))
            {
                string prefixId = firstLine[..^1];
                if (prefixId.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) || prefixId.Contains('@'))
                {
                    text = text[(newlineIdx + 1)..].Trim();
                }
            }
        }

        // 拍一拍系统消息
        if (text.Contains("<patinfo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<sysmsg type=\"pat\"", StringComparison.OrdinalIgnoreCase))
        {
            return "[拍一拍]";
        }

        // 包含 XML 的富媒体/引用回复/卡片
        if (text.Contains("<appmsg", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<msg", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<refermsg", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("<emoji", StringComparison.OrdinalIgnoreCase) || msgType == 47)
            {
                Match mMd5 = s_emojiMd5Regex.Match(text);
                Match mCdn = s_emojiCdnRegex.Match(text);
                string md5 = mMd5.Success ? mMd5.Groups[1].Value.Trim() : string.Empty;
                string cdn = mCdn.Success ? mCdn.Groups[1].Value.Trim() : string.Empty;
                if (!string.IsNullOrEmpty(md5) || !string.IsNullOrEmpty(cdn))
                {
                    return $"[动画表情: md5={md5}&cdn={cdn}]";
                }
                return "[动画表情]";
            }

            if (text.Contains("<img", StringComparison.OrdinalIgnoreCase) || msgType == 3)
            {
                Match mMd5 = s_imgMd5Regex.Match(text);
                string md5 = mMd5.Success ? mMd5.Groups[1].Value.Trim() : string.Empty;
                if (!string.IsNullOrEmpty(md5))
                {
                    return $"[图片: md5={md5}]";
                }
                return "[图片]";
            }

            Match mTitle = s_xmlTitleRegex.Match(text);
            string title = mTitle.Success ? mTitle.Groups[1].Value.Trim() : string.Empty;

            // 引用回复消息 (<refermsg>)
            Match mRefer = s_referMsgBlockRegex.Match(text);
            if (mRefer.Success && !string.IsNullOrWhiteSpace(title))
            {
                string? refText = ExtractReferencedText(mRefer.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(refText))
                {
                    return $"{title} (引用: 「{refText}」)";
                }
                return title;
            }

            Match mUrl = s_xmlUrlRegex.Match(text);
            Match mDes = s_xmlDesRegex.Match(text);
            string url = mUrl.Success ? mUrl.Groups[1].Value.Trim() : string.Empty;
            string des = mDes.Success ? mDes.Groups[1].Value.Trim() : string.Empty;

            if (!string.IsNullOrWhiteSpace(url))
            {
                string safeTitle = !string.IsNullOrWhiteSpace(title) ? title : "网页链接";
                return $"[分享卡片: url={url}|title={safeTitle}|des={des}]";
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                return "[分享链接] " + title;
            }

            return "[分享卡片]";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return msgType switch
            {
                47 => "[动画表情]",
                3 => "[图片]",
                34 => "[语音消息]",
                43 => "[视频]",
                49 => "[分享链接/小程序]",
                10000 or 10002 => "[系统通知]",
                _ => string.Empty
            };
        }

        if (msgType == 34)
        {
            return "[语音消息]";
        }

        return text;
    }

    private static string? ExtractReferencedText(string referBlockXml)
    {
        if (string.IsNullOrWhiteSpace(referBlockXml)) return null;

        Match mRefContent = s_referContentRegex.Match(referBlockXml);
        string rawRef = mRefContent.Success ? mRefContent.Groups[1].Value.Trim() : string.Empty;

        Match mRefType = s_referTypeRegex.Match(referBlockXml);
        int refType = mRefType.Success && int.TryParse(mRefType.Groups[1].Value, out int rt) ? rt : 0;

        if (string.IsNullOrWhiteSpace(rawRef))
        {
            return refType switch
            {
                3 => "[图片]",
                34 => "[语音消息]",
                43 => "[视频]",
                47 => "[动画表情]",
                _ => null
            };
        }

        // HTML 实体解码（如 &lt;msg&gt; -> <msg>）
        string decoded = System.Net.WebUtility.HtmlDecode(rawRef).Trim();
        if (decoded.Contains("&lt;", StringComparison.OrdinalIgnoreCase))
        {
            decoded = System.Net.WebUtility.HtmlDecode(decoded).Trim();
        }

        // 剥除发送者前缀，如 wxid_xxx:\n
        int newlineIdx = decoded.IndexOf('\n');
        if (newlineIdx > 0 && newlineIdx < 80)
        {
            string firstLine = decoded[..newlineIdx].Trim();
            if (firstLine.EndsWith(':') && !firstLine.Contains(' ') && !firstLine.Contains('<'))
            {
                string prefixId = firstLine[..^1];
                if (prefixId.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) || prefixId.Contains('@'))
                {
                    decoded = decoded[(newlineIdx + 1)..].Trim();
                }
            }
        }

        // 若被引用的消息本身也是一条 XML 富媒体或引用回复
        if (decoded.Contains("<appmsg", StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains("<msg", StringComparison.OrdinalIgnoreCase))
        {
            if (decoded.Contains("<emoji", StringComparison.OrdinalIgnoreCase))
            {
                return "[动画表情]";
            }

            Match nestedTitle = s_xmlTitleRegex.Match(decoded);
            if (nestedTitle.Success)
            {
                string nestedTitleText = System.Net.WebUtility.HtmlDecode(nestedTitle.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(nestedTitleText) &&
                    !nestedTitleText.StartsWith("<", StringComparison.Ordinal) &&
                    !nestedTitleText.StartsWith("&lt;", StringComparison.OrdinalIgnoreCase))
                {
                    return TruncateRefText(nestedTitleText);
                }
            }

            return refType switch
            {
                3 => "[图片]",
                34 => "[语音消息]",
                43 => "[视频]",
                47 => "[动画表情]",
                _ => "[引用内容]"
            };
        }

        if (refType == 3 || decoded.Contains("<img", StringComparison.OrdinalIgnoreCase)) return "[图片]";
        if (refType == 34) return "[语音消息]";
        if (refType == 43) return "[视频]";
        if (refType == 47) return "[动画表情]";

        // 移除可能存在的 XML 标签残留
        if (decoded.Contains('<') && decoded.Contains('>'))
        {
            decoded = Regex.Replace(decoded, @"<[^>]+>", string.Empty).Trim();
        }

        if (string.IsNullOrWhiteSpace(decoded) || decoded.StartsWith("&lt;", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TruncateRefText(decoded);
    }

    private static string TruncateRefText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = text.Replace("\r", "").Replace("\n", " ").Trim();
        if (text.Length > 40)
        {
            text = text[..40] + "...";
        }
        return text;
    }

    private static bool ExtractIsSend(JsonElement row, string sender, IReadOnlyList<string> myCandidates)
    {
        if (!string.IsNullOrWhiteSpace(sender) && myCandidates.Count > 0)
        {
            string right = sender.Trim().ToLowerInvariant();
            foreach (string candidate in myCandidates)
            {
                string left = (candidate ?? string.Empty).Trim().ToLowerInvariant();
                if (left.Length == 0) continue;
                if (left == right || left.StartsWith(right + "_") || right.StartsWith(left + "_"))
                {
                    return true;
                }
            }
        }

        string[] isSendKeys = { "computed_is_send", "computedIsSend", "is_send", "isSend", "WCDB_CT_is_send" };
        foreach (string key in isSendKeys)
        {
            if (TryGetPropertyIgnoreCase(row, out var val, key))
            {
                if (val.ValueKind == JsonValueKind.True) return true;
                if (val.ValueKind == JsonValueKind.False) return false;
                if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out int n)) return n == 1;
                if (val.ValueKind == JsonValueKind.String)
                {
                    string s = val.GetString()?.Trim() ?? "";
                    if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }

        if (TryGetPropertyIgnoreCase(row, out var desVal, "mesDes", "Des"))
        {
            if (desVal.ValueKind == JsonValueKind.Number && desVal.TryGetInt32(out int des))
            {
                return des == 0;
            }
        }

        return false;
    }

    private static string ExtractSenderFromContentPrefix(string content, IReadOnlyList<string> myCandidates)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        int idx = content.IndexOf('\n');
        string firstLine = (idx >= 0 ? content[..idx] : content).Trim();
        if (firstLine.Length == 0 || firstLine.Length > 128 || firstLine.Contains('<') || firstLine.Contains('>'))
        {
            return string.Empty;
        }

        string candidate = firstLine.TrimEnd(':').Trim();
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace))
        {
            return string.Empty;
        }

        if (candidate.Contains('@') ||
            candidate.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith("gh_", StringComparison.OrdinalIgnoreCase))
        {
            return candidate;
        }

        return myCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase) ? candidate : string.Empty;
    }

    private static int NormalizeMsgType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        if (long.TryParse(raw.Trim(), out long parsed))
        {
            int low32 = (int)(parsed & 0xFFFFFFFFL);
            return low32 != 0 ? low32 : (int)parsed;
        }

        return 0;
    }

    private static string DecodeBinaryContent(byte[] data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        if (IsZstdPayload(data))
        {
            return DecompressZstd(data);
        }

        return DecodeReadableUtf8(data);
    }

    private static bool IsZstdPayload(byte[] data)
    {
        return data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)) == ZstdMagicNumber;
    }

    private static string DecompressZstd(byte[] data)
    {
        try
        {
            using var decompressor = new Decompressor();
            Span<byte> decompressed = decompressor.Unwrap(data);
            return Encoding.UTF8.GetString(decompressed);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DecodeReadableUtf8(byte[] data)
    {
        try
        {
            string decoded = Encoding.UTF8.GetString(data);
            if (decoded.Length == 0)
            {
                return string.Empty;
            }

            int replacementCount = decoded.Count(c => c == '\uFFFD');
            int readableCount = decoded.Count(c => c == '\t' || c == '\n' || c == '\r' || c >= 0x20);

            if (replacementCount < decoded.Length * 0.2 &&
                (double)readableCount / decoded.Length > 0.85)
            {
                return decoded.Replace("\uFFFD", string.Empty);
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static byte[]? TryReadByteArray(JsonElement arrayEl)
    {
        try
        {
            var bytes = new List<byte>();
            foreach (var item in arrayEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int b) && b is >= 0 and <= 255)
                {
                    bytes.Add((byte)b);
                }
                else
                {
                    return null;
                }
            }
            return bytes.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeHex(string value)
    {
        return value.Length % 2 == 0 && s_hexRegex.IsMatch(value);
    }

    private static bool LooksLikeBase64(string value)
    {
        return value.Length % 4 == 0 && s_base64Regex.IsMatch(value);
    }

    private static string PickString(JsonElement row, params string[] keys)
    {
        if (TryGetPropertyIgnoreCase(row, out var val, keys))
        {
            if (val.ValueKind == JsonValueKind.String)
            {
                return val.GetString()?.Trim() ?? string.Empty;
            }
            if (val.ValueKind == JsonValueKind.Number)
            {
                return val.ToString();
            }
        }
        return string.Empty;
    }

    private static long PickTimestampSeconds(JsonElement row, params string[] keys)
    {
        if (TryGetPropertyIgnoreCase(row, out var val, keys))
        {
            long raw = 0;
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt64(out long n))
            {
                raw = n;
            }
            else if (val.ValueKind == JsonValueKind.String && long.TryParse(val.GetString()?.Trim(), out long parsed))
            {
                raw = parsed;
            }

            if (raw > 0)
            {
                return raw > 1_000_000_000_000L ? raw / 1000L : raw;
            }
        }
        return 0;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement row, out JsonElement value, params string[] keys)
    {
        if (row.ValueKind == JsonValueKind.Object)
        {
            foreach (string key in keys)
            {
                if (row.TryGetProperty(key, out value))
                {
                    return true;
                }
            }

            foreach (var prop in row.EnumerateObject())
            {
                foreach (string key in keys)
                {
                    if (string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }
    /// <summary>
    /// 将带元数据的富媒体标记清洗为纯文本摘要（如 [动画表情: md5=...] -> [动画表情]），供 AI 提示词与统计使用。
    /// </summary>
    public static string StripMediaMarkup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string res = text;
        res = Regex.Replace(res, @"\[动画表情:\s*md5=[^\]]*\]", "[动画表情]");
        res = Regex.Replace(res, @"\[图片:\s*md5=[^\]]*\]", "[图片]");
        res = Regex.Replace(res, @"\[分享卡片:\s*url=[^|]*\|title=([^|]*)\|[^\]]*\]", "[分享] $1");
        return res;
    }

}

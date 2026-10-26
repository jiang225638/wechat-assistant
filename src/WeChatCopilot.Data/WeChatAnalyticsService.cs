using System.Text.RegularExpressions;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// 关系冷热看板与互动统计结果模型。
/// </summary>
public sealed record RelationshipStats(
    string ContactName,
    int TotalMessages,
    int OutgoingCount,
    int IncomingCount,
    double OutgoingRatio,
    int LateNightCount,
    double AverageResponseMinutes,
    int ActiveDays,
    int TemperatureScore,
    string TemperatureLevel,
    string SummaryAdvice);

/// <summary>
/// 未读消息主动提醒与破冰跟进雷达模型。
/// </summary>
public sealed record UnreadRadarItem(
    string Username,
    string DisplayName,
    int UnreadCount,
    DateTime? LastActiveTime,
    string Summary,
    string FollowUpPriority);

/// <summary>
/// 聊天关键事件、约定与承诺提取条目模型。
/// </summary>
public sealed record ChatCommitmentItem(
    MessageRole Role,
    string Text,
    DateTime? Timestamp,
    string Tag,
    string TimeContext);

/// <summary>
/// 跨会话消息检索条目模型。
/// </summary>
public sealed record ChatSearchResultItem(
    string ContactName,
    MessageRole Role,
    string Snippet,
    DateTime? Timestamp);

/// <summary>
/// 微信本地数据库深度分析与中枢服务：
/// 涵盖关系冷热统计、未读主动雷达、约定与承诺提取、跨会话搜索。
/// </summary>
public static class WeChatAnalyticsService
{
    private static readonly Regex s_appointmentRegex = new(
        @"(明天|后天|今晚|下周|周末|周[一二三四五六日天]|(?:\d{1,2}月\d{1,2}日)|(?:\d{1,2}点(?:半|\d{1,2}分)?))[^。！？\n]*?(见面|吃饭|聚|喝咖啡|改天|请客|赴约|逛街|看电影|聊聊|过来)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_deliveryRegex = new(
        @"(发你|发给我|发一下|转你|发送|发送给|文件|资料|合同|方案|发票|报告|ppt|pdf|word|excel|链接)[^。！？\n]*?(发|传|寄|查收|看下|过目|整理好)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_promiseRegex = new(
        @"(答应|保证|记得|一定|肯定|别忘|约好|晚点|随后|一会儿|尽快|马上|等下)[^。！？\n]{2,30}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 计算指定联系人的关系冷热看板与互动统计。
    /// </summary>
    public static RelationshipStats AnalyzeRelationship(string contactName, IReadOnlyList<HistoryMessage> messages)
    {
        if (messages.Count == 0)
        {
            return new RelationshipStats(
                contactName,
                0, 0, 0, 50.0, 0, 0.0, 0,
                0, "❄️ 暂无互动", "本地暂无该联系人的历史对话，点击【读取当前对话】同步后即可洞察关系温度。");
        }

        int total = messages.Count;
        int outgoing = messages.Count(m => m.Role == MessageRole.Outgoing);
        int incoming = messages.Count(m => m.Role == MessageRole.Incoming);
        double outRatio = total > 0 ? Math.Round((double)outgoing / total * 100.0, 1) : 50.0;

        int lateNight = 0;
        var activeDates = new HashSet<DateTime>();
        var responseDelaysMinutes = new List<double>();

        HistoryMessage? lastIncoming = null;

        foreach (var m in messages)
        {
            if (m.Timestamp.HasValue)
            {
                var local = m.Timestamp.Value.ToLocalTime();
                activeDates.Add(local.Date);

                if (local.Hour is >= 23 or < 6)
                {
                    lateNight++;
                }

                if (m.Role == MessageRole.Incoming)
                {
                    lastIncoming = m;
                }
                else if (m.Role == MessageRole.Outgoing && lastIncoming?.Timestamp.HasValue == true)
                {
                    double diffMin = (m.Timestamp.Value - lastIncoming.Timestamp.Value).TotalMinutes;
                    if (diffMin is > 0 and <= 180)
                    {
                        responseDelaysMinutes.Add(diffMin);
                    }
                    lastIncoming = null;
                }
            }
        }

        double avgReplyMinutes = responseDelaysMinutes.Count > 0
            ? Math.Round(responseDelaysMinutes.Average(), 1)
            : 0.0;

        // 计算关系温度分 (0~100)
        // 1. 活跃天数占比 (上限 30 分)
        double scoreDays = Math.Min(30, activeDates.Count * 3);
        // 2. 总消息密度 (上限 25 分)
        double scoreVol = Math.Min(25, total * 0.5);
        // 3. 均衡度得分 (上限 25 分): 比例越接近 50% 得分越高
        double balanceOffset = Math.Abs(50.0 - outRatio);
        double scoreBalance = Math.Max(0, 25.0 - balanceOffset * 0.6);
        // 4. 深夜亲密度加成 (上限 20 分)
        double scoreNight = Math.Min(20, lateNight * 3.5);

        int temperature = (int)Math.Clamp(Math.Round(scoreDays + scoreVol + scoreBalance + scoreNight), 5, 99);

        string level;
        string advice;
        if (temperature >= 80)
        {
            level = $"🔥 极度密切 ({temperature}°C)";
            advice = "双方互动高频且往来均衡，有深夜畅聊基础，处于高信任、高粘性核心人际圈。";
        }
        else if (temperature >= 60)
        {
            level = $"☀️ 熟络热络 ({temperature}°C)";
            advice = "互动顺畅，沟通节奏良好，彼此反馈积极，关系稳步升温中。";
        }
        else if (temperature >= 40)
        {
            level = $"🌤️ 稳健温和 ({temperature}°C)";
            advice = "常规联系偏事务或偶发交流，交流平和克制，建议结合战略目的适度制造互动话题。";
        }
        else
        {
            level = $"❄️ 渐趋冰点 ({temperature}°C)";
            advice = "近期互动频率偏低或存在单向等待，建议借助【未读与破冰雷达】选择轻量话题破冰。";
        }

        return new RelationshipStats(
            contactName,
            total,
            outgoing,
            incoming,
            outRatio,
            lateNight,
            avgReplyMinutes,
            activeDates.Count,
            temperature,
            level,
            advice);
    }

    /// <summary>
    /// 从微信会话列表中提炼未读消息与待破冰主动跟进雷达清单。
    /// </summary>
    public static List<UnreadRadarItem> BuildUnreadRadar(IReadOnlyList<WeChatSessionSummary> sessions)
    {
        var list = new List<UnreadRadarItem>();

        foreach (var s in sessions)
        {
            if (s.UnreadCount > 0)
            {
                string priority = s.UnreadCount >= 5 ? "🔴 紧急未读" : "🟠 普通未读";
                list.Add(new UnreadRadarItem(
                    s.Username,
                    s.DisplayName,
                    s.UnreadCount,
                    s.LastActiveTime,
                    string.IsNullOrWhiteSpace(s.Summary) ? "有新消息等待查收" : s.Summary,
                    priority));
            }
        }

        return list.OrderByDescending(x => x.UnreadCount).ToList();
    }

    /// <summary>
    /// 从历史消息中扫描提炼关键约定、承诺与待办事项。
    /// </summary>
    public static List<ChatCommitmentItem> ExtractCommitments(IReadOnlyList<HistoryMessage> messages, int maxCount = 20)
    {
        var list = new List<ChatCommitmentItem>();
        if (messages.Count == 0)
        {
            return list;
        }

        foreach (var m in messages.Reverse())
        {
            if (list.Count >= maxCount)
            {
                break;
            }

            string text = (m.Text ?? string.Empty).Trim();
            if (text.Length < 4 || text.Length > 200 || text.StartsWith('['))
            {
                continue;
            }

            var mAppoint = s_appointmentRegex.Match(text);
            if (mAppoint.Success)
            {
                list.Add(new ChatCommitmentItem(
                    m.Role,
                    text,
                    m.Timestamp,
                    "📅 聚会约定",
                    mAppoint.Groups[1].Value));
                continue;
            }

            var mDelivery = s_deliveryRegex.Match(text);
            if (mDelivery.Success)
            {
                list.Add(new ChatCommitmentItem(
                    m.Role,
                    text,
                    m.Timestamp,
                    "📑 交付承诺",
                    mDelivery.Groups[1].Value));
                continue;
            }

            var mPromise = s_promiseRegex.Match(text);
            if (mPromise.Success)
            {
                list.Add(new ChatCommitmentItem(
                    m.Role,
                    text,
                    m.Timestamp,
                    "🤝 承诺表态",
                    mPromise.Groups[1].Value));
            }
        }

        return list;
    }

    /// <summary>
    /// 在已载入的消息列表或本地历史中执行关键词搜索。
    /// </summary>
    public static List<ChatSearchResultItem> SearchMessages(
        string keyword,
        IReadOnlyList<HistoryMessage> currentMessages,
        string currentContact,
        int maxCount = 30)
    {
        var list = new List<ChatSearchResultItem>();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return list;
        }

        string q = keyword.Trim();
        foreach (var m in currentMessages.Reverse())
        {
            if (list.Count >= maxCount)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(m.Text) && m.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new ChatSearchResultItem(
                    currentContact,
                    m.Role,
                    m.Text.Trim(),
                    m.Timestamp));
            }
        }

        return list;
    }
}

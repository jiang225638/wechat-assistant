using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Abstractions;

/// <summary>
/// 聊天历史拉取结果（冷链路统一返回契约）。
/// </summary>
/// <param name="Success">是否拉取成功。</param>
/// <param name="Messages">按时间升序排列的历史消息列表。</param>
/// <param name="Error">失败时的诊断信息。</param>
/// <param name="SourceName">实际命中的数据源名称（例如 "WCDB4 本地直读" 或 "TraceMemo API"）。</param>
public sealed record ChatHistoryResult(
    bool Success,
    IReadOnlyList<HistoryMessage> Messages,
    string? Error,
    string SourceName = "");

/// <summary>
/// 本地聊天数据库连接自检状态。
/// </summary>
/// <param name="Success">连接是否就绪。</param>
/// <param name="AccountRoot">识别到的微信账号数据根目录。</param>
/// <param name="Wxid">识别到的当前登录微信号 wxid。</param>
/// <param name="SessionCount">识别到的会话数量。</param>
/// <param name="Message">状态说明或错误诊断。</param>
/// <param name="SourceName">命中的数据源名称。</param>
public sealed record ChatConnectionStatus(
    bool Success,
    string AccountRoot,
    string Wxid,
    int SessionCount,
    string Message,
    string SourceName = "");

/// <summary>
/// 微信最近会话简要信息（供顶部下拉快选与未读提醒使用）。
/// </summary>
/// <param name="Username">微信会话 ID (wxid)。</param>
/// <param name="DisplayName">首选显示名（备注优先，其次昵称）。</param>
/// <param name="Nickname">微信昵称。</param>
/// <param name="Remark">备注名。</param>
/// <param name="UnreadCount">未读消息条数。</param>
/// <param name="Summary">最新消息摘要。</param>
/// <param name="LastActiveTime">最近活跃时间（UTC）。</param>
public sealed record WeChatSessionSummary(
    string Username,
    string DisplayName,
    string Nickname,
    string Remark,
    int UnreadCount = 0,
    string Summary = "",
    DateTime? LastActiveTime = null)
{
    /// <summary>下拉框展示文本（带未读红点、备注+昵称、活跃时间）。</summary>
    public string DropdownLabel
    {
        get
        {
            string badge = UnreadCount > 0 ? $"🔴[{UnreadCount}未读] " : string.Empty;
            string namePart = !string.IsNullOrWhiteSpace(Remark) &&
                              !string.IsNullOrWhiteSpace(Nickname) &&
                              !string.Equals(Remark, Nickname, StringComparison.OrdinalIgnoreCase)
                ? $"{Remark} ({Nickname})"
                : DisplayName;
            string timePart = string.Empty;
            if (LastActiveTime.HasValue)
            {
                var local = LastActiveTime.Value.ToLocalTime();
                var today = DateTime.Now.Date;
                timePart = local.Date == today
                    ? $" · {local:HH:mm}"
                    : $" · {local:MM-dd HH:mm}";
            }
            return $"{badge}{namePart}{timePart}";
        }
    }

    public override string ToString() => DropdownLabel;
}

/// <summary>
/// 冷链路聊天历史读取提供器统一抽象：
/// 支持微信 4.x WCDB4 本地数据库直读、TraceMemo Local HTTP API 等多种数据源。
/// 任何实现均遵循零异常契约：内部故障降级为 Success=false + Error，绝不干扰热链路。
/// </summary>
public interface IChatHistoryProvider : IDisposable
{
    /// <summary>数据源显示名称。</summary>
    string SourceName { get; }

    /// <summary>按联系人昵称/备注/wxid 拉取完整历史消息。</summary>
    Task<ChatHistoryResult> FetchHistoryAsync(string contactName, CancellationToken cancellationToken = default);

    /// <summary>获取当前排在首位的活跃单聊好友名称（已过滤群聊、公众号与系统服务号）。</summary>
    Task<string?> GetActiveContactNameAsync(CancellationToken cancellationToken = default);

    /// <summary>获取微信最近单聊会话列表（含备注名、昵称、未读数与最近活跃时间，已过滤群聊与系统号）。</summary>
    Task<IReadOnlyList<WeChatSessionSummary>> GetRecentSessionsAsync(int maxCount = 40, CancellationToken cancellationToken = default);

    /// <summary>测试数据源连接状态并返回诊断详情。</summary>
    Task<ChatConnectionStatus> TestConnectionAsync(CancellationToken cancellationToken = default);
}

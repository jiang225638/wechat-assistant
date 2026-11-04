namespace WeChatCopilot.Core.Models;

/// <summary>
/// 一条解析出的聊天消息（热链路产物）。<see cref="Text"/> 保留原始换行（多行气泡以 \n 连接）用于展示；
/// <see cref="Key"/> 为归一化后的稳定标识，用于跨帧去重。
/// </summary>
/// <param name="Role">收发方向（对方/自己/未知）。</param>
/// <param name="Text">消息文本（多行气泡含换行）。</param>
/// <param name="Timestamp">可选消息发送时间（来自本地数据库或历史缓存）。</param>
/// <param name="SenderName">可选发送方显示名称（优先备注名，其次微信昵称）。</param>
public sealed record ChatMessage(MessageRole Role, string Text, DateTime? Timestamp = null, string? SenderName = null)
{
    /// <summary>去重键：方向 + 归一化文本。表达式属性，不参与 record 相等性比较。</summary>
    public string Key => $"{(int)Role}\u0001{MessageNormalizer.Normalize(Text)}";

    /// <summary>该消息包含的行数（按换行计）。</summary>
    public int LineCount => Text.Length == 0 ? 0 : Text.Count(c => c == '\n') + 1;

    /// <summary>是否为对方发来（左气泡）。</summary>
    public bool IsIncoming => Role == MessageRole.Incoming;

    /// <summary>是否为自己发出（右气泡）。</summary>
    public bool IsOutgoing => Role == MessageRole.Outgoing;

    /// <summary>发送方标签（有备注/昵称则优先展示，否则降级为 对方/我）。</summary>
    public string SenderLabel => !string.IsNullOrWhiteSpace(SenderName)
        ? SenderName.Trim()
        : Role switch
        {
            MessageRole.Incoming => "对方",
            MessageRole.Outgoing => "我",
            _ => "未知"
        };

    /// <summary>是否存在有效时间戳。</summary>
    public bool HasTime => Timestamp.HasValue;

    /// <summary>友好格式化时间（完整年月日，如 "2026-11-02 11:47"）。若无时间戳则为空。</summary>
    public string TimeText
    {
        get
        {
            if (!Timestamp.HasValue) return string.Empty;
            var dt = Timestamp.Value.Kind == DateTimeKind.Utc
                ? Timestamp.Value.ToLocalTime()
                : Timestamp.Value;
            return dt.ToString("yyyy-MM-dd HH:mm");
        }
    }

    /// <summary>气泡头部标题（包含发送方标签与时间戳，如 "我 · 11:47" 或 "对方 · 11:47"）。</summary>
    public string HeaderLabel => HasTime ? $"{SenderLabel} · {TimeText}" : SenderLabel;

    /// <summary>气泡对齐方向。</summary>
    public string BubbleAlignment => IsIncoming ? "Left" : "Right";

    /// <summary>气泡背景色。</summary>
    public string BubbleBackground => IsIncoming ? "#27273A" : "#1B4332";

    /// <summary>气泡边框色。</summary>
    public string BubbleBorder => IsIncoming ? "#3D3D58" : "#2D6A4F";

    /// <summary>徽章文字颜色。</summary>
    public string BadgeForeground => IsIncoming ? "#93C5FD" : "#6EE7B7";
}

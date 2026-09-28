namespace WeChatCopilot.Core.Models;

/// <summary>
/// 一条解析出的聊天消息（热链路产物）。<see cref="Text"/> 保留原始换行（多行气泡以 \n 连接）用于展示；
/// <see cref="Key"/> 为归一化后的稳定标识，用于跨帧去重。
/// </summary>
/// <param name="Role">收发方向（对方/自己/未知）。</param>
/// <param name="Text">消息文本（多行气泡含换行）。</param>
public sealed record ChatMessage(MessageRole Role, string Text)
{
    /// <summary>去重键：方向 + 归一化文本。表达式属性，不参与 record 相等性比较。</summary>
    public string Key => $"{(int)Role}\u0001{MessageNormalizer.Normalize(Text)}";

    /// <summary>该消息包含的行数（按换行计）。</summary>
    public int LineCount => Text.Length == 0 ? 0 : Text.Count(c => c == '\n') + 1;

    /// <summary>是否为对方发来（左气泡）。</summary>
    public bool IsIncoming => Role == MessageRole.Incoming;

    /// <summary>是否为自己发出（右气泡）。</summary>
    public bool IsOutgoing => Role == MessageRole.Outgoing;

    /// <summary>发送方标签。</summary>
    public string SenderLabel => Role switch
    {
        MessageRole.Incoming => "对方",
        MessageRole.Outgoing => "我",
        _ => "未知"
    };

    /// <summary>气泡对齐方向。</summary>
    public string BubbleAlignment => IsIncoming ? "Left" : "Right";

    /// <summary>气泡背景色。</summary>
    public string BubbleBackground => IsIncoming ? "#27273A" : "#1B4332";

    /// <summary>气泡边框色。</summary>
    public string BubbleBorder => IsIncoming ? "#3D3D58" : "#2D6A4F";

    /// <summary>徽章文字颜色。</summary>
    public string BadgeForeground => IsIncoming ? "#93C5FD" : "#6EE7B7";
}


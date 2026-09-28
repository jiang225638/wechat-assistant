namespace WeChatCopilot.Core.Models;

/// <summary>
/// 一条聊天消息的收发方向。热链路依据 OCR 气泡的左右位置判定：
/// 微信里对方（接收方）的气泡在左侧，自己（发送方）的气泡在右侧。
/// </summary>
public enum MessageRole
{
    /// <summary>未知/居中——通常是时间戳或系统提示（如“对方撤回了一条消息”）。</summary>
    Unknown = 0,

    /// <summary>对方发来的消息（左气泡）。</summary>
    Incoming = 1,

    /// <summary>自己发出的消息（右气泡）。</summary>
    Outgoing = 2
}

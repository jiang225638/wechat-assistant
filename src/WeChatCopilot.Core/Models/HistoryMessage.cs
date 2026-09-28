namespace WeChatCopilot.Core.Models;

/// <summary>
/// 冷链路历史消息（M5）：来自 TraceMemo API 或 CSV 导入的完整历史记录，
/// 与热链路 <see cref="ChatMessage"/> 区分：历史带时间戳且来源可信（非 OCR）。
/// </summary>
/// <param name="Role">收发方向。</param>
/// <param name="Text">消息文本。</param>
/// <param name="Timestamp">发送时间（可空）。</param>
public sealed record HistoryMessage(MessageRole Role, string Text, DateTime? Timestamp = null);

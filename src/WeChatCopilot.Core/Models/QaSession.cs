namespace WeChatCopilot.Core.Models;

/// <summary>
/// AI 问答单条消息模型（支持事实依据标注）。
/// </summary>
public sealed record QaSessionMessage(
    MessageRole Role,
    string Text,
    DateTime Timestamp,
    string? GroundingInfo = null);

/// <summary>
/// AI 问答历史会话实体。
/// </summary>
public sealed class QaSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新会话";
    public string ContactName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<QaSessionMessage> Messages { get; set; } = new();
}

namespace WeChatCopilot.Core.Models;

/// <summary>
/// 联系人画像（FR-3/FR-7）：结构化多维 persona，可 JSON 持久化（M5 文件存储，M6 迁 SQLite）。
/// </summary>
/// <param name="ContactName">联系人备注名。</param>
/// <param name="Traits">特质集合（按维度组织，含证据与置信度）。</param>
/// <param name="UpdatedAt">最近一次蒸馏时间。</param>
/// <param name="SourceMessageCount">本次蒸馏所用历史消息数。</param>
/// <param name="SkillId">本次蒸馏所采用的技能 ID（如 nuwa, tdskill, workplace 等）。</param>
public sealed record Persona(
    string ContactName,
    IReadOnlyList<PersonaTrait> Traits,
    DateTime UpdatedAt,
    int SourceMessageCount,
    string? SkillId = null);


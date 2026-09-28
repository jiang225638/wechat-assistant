using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Abstractions;

/// <summary>
/// AI 提供商抽象（M3）：与具体厂商无关的补全接口。
/// 实现方负责把 <see cref="AiRequest"/> 发到各自后端并归一化为 <see cref="AiReply"/>。
/// </summary>
public interface IAiProvider
{
    /// <summary>提供商名称（用于诊断展示）。</summary>
    string Name { get; }

    /// <summary>执行一次聊天补全；失败时返回 Success=false 且带 Error，不抛异常。</summary>
    Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default);
}

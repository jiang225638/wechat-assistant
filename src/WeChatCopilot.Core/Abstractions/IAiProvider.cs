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

    /// <summary>
    /// 流式补全接口：按 token/chunk 异步返回流式文本。
    /// 默认实现降级为调用 CompleteAsync 并一次性 yield 返回全部文本。
    /// </summary>
    public async IAsyncEnumerable<string> CompleteStreamAsync(
        AiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reply = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Success && !string.IsNullOrEmpty(reply.Text))
        {
            yield return reply.Text;
        }
    }
}

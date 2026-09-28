using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Abstractions;

/// <summary>
/// 跟踪目标窗口的位置/状态变化。
/// 注意：<see cref="TargetChanged"/> 可能在非 UI 线程触发，消费者需自行切回 UI 线程。
/// </summary>
public interface IWindowTracker : IDisposable
{
    /// <summary>目标窗口位置/状态变化时触发；目标丢失时以 <c>null</c> 触发。</summary>
    event Action<WindowInfo?>? TargetChanged;

    /// <summary>开始跟踪指定窗口。</summary>
    void Start(WindowInfo target);

    /// <summary>停止跟踪。</summary>
    void Stop();

    /// <summary>获取目标窗口最新信息；无目标返回 <c>null</c>。</summary>
    WindowInfo? GetCurrent();
}

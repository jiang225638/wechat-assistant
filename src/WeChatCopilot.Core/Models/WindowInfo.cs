namespace WeChatCopilot.Core.Models;

/// <summary>目标窗口信息快照。</summary>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    WindowBounds Bounds,
    bool IsMinimized,
    bool IsVisible);

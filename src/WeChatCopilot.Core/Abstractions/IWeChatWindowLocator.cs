using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Abstractions;

/// <summary>定位微信主窗口。</summary>
public interface IWeChatWindowLocator
{
    /// <summary>尝试定位当前微信主窗口；未找到返回 <c>null</c>。</summary>
    WindowInfo? FindWeChatMainWindow();
}

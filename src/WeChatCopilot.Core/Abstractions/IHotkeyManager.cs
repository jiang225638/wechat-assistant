using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Abstractions;

/// <summary>
/// 全局热键管理抽象。实现基于 Win32 <c>RegisterHotKey</c>，将系统级热键绑定到某个窗口句柄，
/// 并在窗口收到 <c>WM_HOTKEY</c> 时回调对应动作（回调需在 UI 线程执行）。
/// </summary>
public interface IHotkeyManager : IDisposable
{
    /// <summary>
    /// 注册一个全局热键。返回是否成功——失败通常意味着该组合键已被其它程序占用。
    /// </summary>
    /// <param name="id">热键标识（同一管理器内唯一，用于 <see cref="OnHotkeyMessage"/> 分发）。</param>
    /// <param name="modifiers">修饰键（Ctrl/Alt/Shift/Win 组合）。</param>
    /// <param name="virtualKey">主键的虚拟键码（如 <c>0x57</c> 表示 W）。</param>
    /// <param name="callback">触发时执行的回调。</param>
    bool Register(int id, HotkeyModifiers modifiers, uint virtualKey, Action callback);

    /// <summary>注销指定 id 的热键。</summary>
    void Unregister(int id);

    /// <summary>注销全部已注册热键。</summary>
    void UnregisterAll();

    /// <summary>
    /// 由宿主窗口的消息钩子在收到 <c>WM_HOTKEY</c> 时调用；<paramref name="id"/> 即消息 wParam。
    /// </summary>
    void OnHotkeyMessage(int id);
}

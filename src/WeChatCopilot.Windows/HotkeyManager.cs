using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Windows.Interop;

namespace WeChatCopilot.Windows;

/// <summary>
/// 基于 Win32 <c>RegisterHotKey</c> 的全局热键实现。绑定到给定窗口句柄，
/// 由宿主窗口在收到 <c>WM_HOTKEY</c> 时调用 <see cref="OnHotkeyMessage"/> 分发到对应回调。
/// </summary>
public sealed class HotkeyManager : IHotkeyManager
{
    private readonly nint _hwnd;
    private readonly Dictionary<int, Action> _callbacks = new();
    private bool _disposed;

    /// <param name="hwnd">接收 WM_HOTKEY 的窗口句柄（热键注册到该窗口线程）。</param>
    public HotkeyManager(nint hwnd)
    {
        _hwnd = hwnd;
    }

    public bool Register(int id, HotkeyModifiers modifiers, uint virtualKey, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 若该 id 已注册，先注销避免冲突
        if (_callbacks.ContainsKey(id))
        {
            Unregister(id);
        }

        bool ok = NativeMethods.RegisterHotKey(_hwnd, id, (uint)modifiers, virtualKey);
        if (ok)
        {
            _callbacks[id] = callback;
        }

        return ok;
    }

    public void Unregister(int id)
    {
        if (_callbacks.Remove(id))
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }
    }

    public void UnregisterAll()
    {
        foreach (int id in _callbacks.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        _callbacks.Clear();
    }

    public void OnHotkeyMessage(int id)
    {
        if (_callbacks.TryGetValue(id, out var callback))
        {
            callback();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        UnregisterAll();
        _disposed = true;
    }
}

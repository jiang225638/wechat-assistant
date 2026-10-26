using WeChatCopilot.Windows.Interop;

namespace WeChatCopilot.Windows;

/// <summary>
/// 以物理像素坐标定位窗口，规避 WPF 的 DIP/DPI 换算，保证与 GetWindowRect 坐标一致。
/// </summary>
public static class WindowPositioner
{
    /// <summary>将窗口移动到指定物理像素坐标并保持置顶（不改变尺寸、不激活）。</summary>
    public static void MoveToTopmost(nint hwnd, int x, int y)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 将窗口移动到指定物理像素坐标，并将 Z 轴层级紧贴在目标窗口之后（同层级跟随，不置顶、不激活）。
    /// </summary>
    public static void MoveRelativeTo(nint hwnd, nint targetHwnd, int x, int y)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            targetHwnd,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 将窗口移动到指定物理像素坐标，保持现有 Z 轴层级不变（不激活）。
    /// </summary>
    public static void MoveKeepZOrder(nint hwnd, int x, int y)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            nint.Zero,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER);
    }

    /// <summary>
    /// 移除窗口的置顶状态（HWND_NOTOPMOST），回归普通层级。
    /// </summary>
    public static void RemoveTopmost(nint hwnd)
    {
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// 获取当前系统的前台活动窗口句柄。
    /// </summary>
    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>
    /// 将副驾窗口移动到与微信对齐的坐标，并根据当前前台焦点状态智能维持 Z 轴层级（紧随微信同一层级，不置顶、不抢焦点）。
    /// </summary>
    public static void MoveCompanion(nint copilotHwnd, nint targetHwnd, int x, int y)
    {
        nint fg = NativeMethods.GetForegroundWindow();
        if (fg == copilotHwnd)
        {
            MoveKeepZOrder(copilotHwnd, x, y);
        }
        else
        {
            MoveRelativeTo(copilotHwnd, targetHwnd, x, y);
        }
    }

    /// <summary>
    /// 将当前剪贴板文本自动聚焦并粘贴到微信聊天输入框（仅填入，绝不自动按回车发送）。
    /// </summary>
    public static async Task<bool> TryPasteToWeChatInputAsync(WeChatCopilot.Core.Models.WindowInfo? targetWindow = null)
    {
        targetWindow ??= new WeChatWindowLocator().FindWeChatMainWindow();
        if (targetWindow is null || targetWindow.IsMinimized || !targetWindow.IsVisible || targetWindow.Handle == nint.Zero)
        {
            return false;
        }

        double xRatio = string.Equals(targetWindow.ClassName, "ChatWnd", StringComparison.OrdinalIgnoreCase) ? 0.50 : 0.62;
        int clickX = targetWindow.Bounds.X + (int)(targetWindow.Bounds.Width * xRatio);
        int bottomOffset = Math.Clamp((int)(targetWindow.Bounds.Height * 0.10), 48, 85);
        int clickY = targetWindow.Bounds.Y + targetWindow.Bounds.Height - bottomOffset;

        bool hasSavedCursor = NativeMethods.GetCursorPos(out var savedPt);

        NativeMethods.SetForegroundWindow(targetWindow.Handle);
        await Task.Delay(35).ConfigureAwait(false);

        NativeMethods.SetCursorPos(clickX, clickY);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, clickX, clickY, 0, 0);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, clickX, clickY, 0, 0);

        if (hasSavedCursor)
        {
            NativeMethods.SetCursorPos(savedPt.X, savedPt.Y);
        }

        await Task.Delay(55).ConfigureAwait(false);

        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, 0, 0);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, 0, 0);
        NativeMethods.keybd_event(NativeMethods.VK_V, 0, NativeMethods.KEYEVENTF_KEYUP, 0);
        NativeMethods.keybd_event(NativeMethods.VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, 0);

        return true;
    }
}

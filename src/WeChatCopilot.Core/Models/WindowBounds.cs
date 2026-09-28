namespace WeChatCopilot.Core.Models;

/// <summary>
/// 窗口矩形，使用物理像素坐标（跨显示器统一坐标系，与 Win32 GetWindowRect 一致）。
/// </summary>
public readonly record struct WindowBounds(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IsValid => Width > 0 && Height > 0;
}

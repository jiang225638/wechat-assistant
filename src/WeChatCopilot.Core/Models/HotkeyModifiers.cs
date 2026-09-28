namespace WeChatCopilot.Core.Models;

/// <summary>
/// 全局热键修饰键。数值与 Win32 <c>RegisterHotKey</c> 的 <c>MOD_*</c> 常量一致，可直接转换。
/// </summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0x0000,
    Alt = 0x0001,     // MOD_ALT
    Control = 0x0002, // MOD_CONTROL
    Shift = 0x0004,   // MOD_SHIFT
    Win = 0x0008,     // MOD_WIN
    NoRepeat = 0x4000 // MOD_NOREPEAT（按住不重复触发）
}

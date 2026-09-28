using System.Text;

namespace WeChatCopilot.Core.Models;

/// <summary>
/// 消息文本归一化工具：把换行、制表、连续空白折叠为单个空格并去首尾空白。
/// 用于生成稳定的去重键（跨帧同一气泡文本一致），以及比较消息是否等价。
/// </summary>
public static class MessageNormalizer
{
    /// <summary>归一化文本；<c>null</c>/空白返回空串。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}

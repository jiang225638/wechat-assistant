using System.Text;

namespace WeChatCopilot.Core.Models;

/// <summary>
/// 把 OCR 的"词"序列拼接成行文本，并对中日韩（CJK）文本做无空格连接。
/// 背景：Windows.Media.Ocr 对中文按"单字"拆词，且其行文本用空格连接，
/// 会得到"连 吃 大 面"这类带空格的结果；中文书写本无词间空格，故 CJK 相邻时不加空格。
/// 规则：仅当相邻两个词的交界两侧"都不是 CJK"（即拉丁/数字等需要空格分隔的脚本）时才插入空格。
/// </summary>
public static class OcrTextJoiner
{
    /// <summary>将词序列拼接为行文本（CJK 相邻不加空格，拉丁相邻加空格）。</summary>
    public static string Join(IReadOnlyList<OcrWord>? words)
    {
        if (words is null || words.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        char? prevLast = null;

        foreach (OcrWord w in words)
        {
            string t = w.Text ?? string.Empty;
            if (t.Length == 0)
            {
                continue;
            }

            if (prevLast.HasValue && NeedsSpace(prevLast.Value, t[0]))
            {
                sb.Append(' ');
            }

            sb.Append(t);
            prevLast = t[t.Length - 1];
        }

        return sb.ToString();
    }

    /// <summary>仅当两侧都不是 CJK 字符时才需要空格（拉丁/数字之间）。</summary>
    private static bool NeedsSpace(char prev, char next) => !IsCjk(prev) && !IsCjk(next);

    /// <summary>判断字符是否属于 CJK（含扩展、兼容、全角与中文标点）。</summary>
    public static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK 统一表意文字
        (c >= 0x3400 && c <= 0x4DBF) ||   // CJK 扩展 A
        (c >= 0xF900 && c <= 0xFAFF) ||   // CJK 兼容
        (c >= 0x3000 && c <= 0x303F) ||   // CJK 标点（、。《》等）
        (c >= 0xFF00 && c <= 0xFFEF);     // 全角字符（，！？：；等）
}

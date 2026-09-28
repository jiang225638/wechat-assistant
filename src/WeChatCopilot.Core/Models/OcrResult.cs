namespace WeChatCopilot.Core.Models;

/// <summary>
/// 一次 OCR 识别的结果：整段文本、按行/词的结构化输出、所用引擎与耗时。
/// </summary>
/// <param name="Text">引擎返回的整段文本（各引擎自行决定行分隔）。</param>
/// <param name="Lines">结构化行集合（含词的边界框）。</param>
/// <param name="Engine">产生该结果的引擎名称。</param>
/// <param name="Elapsed">识别耗时。</param>
public sealed record OcrResult(string Text, IReadOnlyList<OcrLine> Lines, string Engine, TimeSpan Elapsed)
{
    public int LineCount => Lines.Count;
}

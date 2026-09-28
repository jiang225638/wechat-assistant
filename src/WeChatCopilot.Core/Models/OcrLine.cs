namespace WeChatCopilot.Core.Models;

/// <summary>OCR 识别出的一行文本及其包含的词。行是消息切分的基本单位。</summary>
public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

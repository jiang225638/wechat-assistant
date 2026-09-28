namespace WeChatCopilot.Core.Models;

/// <summary>
/// OCR 识别出的单个词及其在截图内的边界框（像素，相对截图左上角）。
/// 后续热链路用 X 判定气泡左右（收/发方向）。
/// </summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

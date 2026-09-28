using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Parsing;

/// <summary>
/// 把一帧 OCR 结果切分为带收发方向的聊天消息（热链路核心，纯函数、可单测）。
/// 规则：① 依据每行词边界框的左右边缘判定 对方(左)/自己(右)/居中(未知)；
/// ② 相邻同方向且垂直间距小的行合并为同一气泡（多行消息）；
/// ③ 方向变化、间距过大或出现居中行（时间戳/系统提示）即视为消息边界。
/// </summary>
public sealed class MessageSegmenter
{
    private readonly SegmentationOptions _options;

    public MessageSegmenter(SegmentationOptions? options = null)
    {
        _options = options ?? new SegmentationOptions();
    }

    /// <summary>
    /// 切分一帧 OCR 结果。
    /// </summary>
    /// <param name="ocr">OCR 结果（含结构化行/词）。</param>
    /// <param name="captureWidth">截图宽度（像素），用于按比例判定左右气泡。</param>
    /// <returns>按屏幕自上而下顺序的消息列表（已按 <see cref="SegmentationOptions.DropUnknown"/> 过滤居中行）。</returns>
    public IReadOnlyList<ChatMessage> Segment(OcrResult? ocr, int captureWidth)
    {
        var messages = new List<ChatMessage>();
        if (ocr is null || captureWidth <= 0 || ocr.Lines.Count == 0)
        {
            return messages;
        }

        MessageRole currentRole = MessageRole.Unknown;
        var currentLines = new List<string>();
        bool havePrev = false;
        double prevBottom = 0;
        double prevHeight = 0;

        foreach (OcrLine line in ocr.Lines)
        {
            MessageRole role = ClassifyRole(line, captureWidth);
            double top = LineTop(line);
            double height = LineHeight(line);
            double gap = havePrev ? top - prevBottom : 0;

            bool sameBubble =
                havePrev &&
                role == currentRole &&
                role != MessageRole.Unknown &&
                currentLines.Count > 0 &&
                gap <= Math.Max(height, prevHeight) * _options.MaxIntraMessageGapFactor;

            if (sameBubble)
            {
                currentLines.Add(line.Text);
            }
            else
            {
                Flush(messages, currentRole, currentLines);
                currentRole = role;
                currentLines.Clear();
                if (role != MessageRole.Unknown)
                {
                    currentLines.Add(line.Text);
                }
            }

            prevBottom = top + height;
            prevHeight = height;
            havePrev = true;
        }

        Flush(messages, currentRole, currentLines);
        return messages;
    }

    private void Flush(List<ChatMessage> messages, MessageRole role, List<string> lines)
    {
        // 居中/未知行（时间戳、系统提示）仅作为分隔，不产出消息
        if (role == MessageRole.Unknown || lines.Count == 0)
        {
            return;
        }

        string text = string.Join("\n", lines).Trim();
        if (text.Length == 0)
        {
            return;
        }

        messages.Add(new ChatMessage(role, text));
    }

    private MessageRole ClassifyRole(OcrLine line, int captureWidth)
    {
        if (line.Words.Count == 0)
        {
            return MessageRole.Unknown;
        }

        double left = double.MaxValue;
        double right = double.MinValue;
        foreach (OcrWord w in line.Words)
        {
            left = Math.Min(left, w.X);
            right = Math.Max(right, w.X + w.Width);
        }

        double distToLeft = left;
        double distToRight = Math.Max(0, captureWidth - right);

        bool isLeftCandidate = left <= captureWidth * _options.AnchorLeftMaxRatio;
        bool isRightCandidate = right >= captureWidth * _options.AnchorRightMinRatio;

        // 如果只符合一侧候选
        if (isLeftCandidate && !isRightCandidate)
        {
            return MessageRole.Incoming;
        }

        if (isRightCandidate && !isLeftCandidate)
        {
            return MessageRole.Outgoing;
        }

        // 如果两侧都符合（长句子）或处于中间地带，根据与左/右侧边缘的相对间距距离判定
        if (isLeftCandidate && isRightCandidate)
        {
            return distToLeft <= distToRight ? MessageRole.Incoming : MessageRole.Outgoing;
        }

        if (distToRight < distToLeft * 0.75)
        {
            return MessageRole.Outgoing;
        }

        if (distToLeft < distToRight * 0.75)
        {
            return MessageRole.Incoming;
        }

        return MessageRole.Unknown;
    }

    private static double LineTop(OcrLine line)
    {
        double top = double.MaxValue;
        foreach (OcrWord w in line.Words)
        {
            top = Math.Min(top, w.Y);
        }

        return top == double.MaxValue ? 0 : top;
    }

    private static double LineHeight(OcrLine line)
    {
        double h = 0;
        foreach (OcrWord w in line.Words)
        {
            h = Math.Max(h, w.Height);
        }

        return h;
    }
}

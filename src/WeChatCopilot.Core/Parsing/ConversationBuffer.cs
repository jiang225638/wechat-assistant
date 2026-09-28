using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Parsing;

/// <summary>
/// “当前对话”滚动缓冲：把每次读取到的一帧消息（当前可见的若干条，按屏幕自上而下）增量并入历史，
/// 通过“缓冲区尾部 与 帧前缀 的最长重叠”来跨帧去重，从而在向上滚动/重复读取时不重复累积。
/// 纯逻辑、线程不安全（由调用方在单一线程/加锁下使用）。
/// </summary>
public sealed class ConversationBuffer
{
    private readonly List<ChatMessage> _messages = new();
    private readonly int _capacity;

    /// <param name="capacity">最多保留的消息条数（滚动窗口），超出则丢弃最旧的。</param>
    public ConversationBuffer(int capacity = 500)
    {
        _capacity = capacity > 0 ? capacity : 500;
    }

    /// <summary>当前缓冲的消息（只读视图，自上而下）。</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>缓冲中的消息条数。</summary>
    public int Count => _messages.Count;

    /// <summary>清空缓冲（如切换联系人时）。</summary>
    public void Clear() => _messages.Clear();

    /// <summary>
    /// 摄入一帧消息，返回本次“新增”的条数（重叠部分被去重、不计入）。
    /// </summary>
    public int Ingest(IReadOnlyList<ChatMessage>? frame)
    {
        if (frame is null || frame.Count == 0)
        {
            return 0;
        }

        int overlap = FindOverlap(frame);
        int appended = 0;
        for (int i = overlap; i < frame.Count; i++)
        {
            _messages.Add(frame[i]);
            appended++;
        }

        TrimToCapacity();
        return appended;
    }

    /// <summary>
    /// 在缓冲区尾部寻找与帧前缀“最长的匹配长度”k：即 buffer[^k..] 与 frame[0..k] 逐条 Key 相等。
    /// 找不到则返回 0（视为全新内容，全部追加）。
    /// </summary>
    private int FindOverlap(IReadOnlyList<ChatMessage> frame)
    {
        int max = Math.Min(_messages.Count, frame.Count);
        for (int k = max; k > 0; k--)
        {
            int start = _messages.Count - k;
            bool match = true;
            for (int j = 0; j < k; j++)
            {
                if (_messages[start + j].Key != frame[j].Key)
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return k;
            }
        }

        return 0;
    }

    private void TrimToCapacity()
    {
        if (_messages.Count > _capacity)
        {
            _messages.RemoveRange(0, _messages.Count - _capacity);
        }
    }
}

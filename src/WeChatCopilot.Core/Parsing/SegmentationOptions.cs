namespace WeChatCopilot.Core.Parsing;

/// <summary>
/// 消息切分参数。所有比例均相对截图宽度，便于在不同窗口尺寸/DPI 下保持一致。
/// 默认值面向微信 PC 端典型布局，可按实测微调（M2 探针校准）。
/// </summary>
public sealed record SegmentationOptions
{
    /// <summary>行左边缘 X ≤ 宽度×此比例 → 判为对方（左气泡）。</summary>
    public double AnchorLeftMaxRatio { get; init; } = 0.35;

    /// <summary>行右边缘 X ≥ 宽度×此比例 → 判为自己（右气泡）。</summary>
    public double AnchorRightMinRatio { get; init; } = 0.65;

    /// <summary>
    /// 同一气泡内相邻行的最大垂直间距系数：间距 ≤ max(本行高, 上行高)×此值 视为同一气泡续行，
    /// 否则视为新气泡（消息边界）。
    /// </summary>
    public double MaxIntraMessageGapFactor { get; init; } = 1.8;

    /// <summary>是否丢弃居中的时间戳/系统提示（<see cref="Models.MessageRole.Unknown"/>）。默认丢弃，仅作为消息分隔。</summary>
    public bool DropUnknown { get; init; } = true;
}

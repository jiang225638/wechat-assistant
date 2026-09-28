namespace WeChatCopilot.Core.Layout;

/// <summary>
/// 从微信窗口矩形中裁剪出"聊天消息区"的比例配置。
/// 微信 PC 端窗口含左侧会话列表/图标栏、顶部标题与联系人栏、底部输入框与工具栏，
/// 这些都不是对话气泡，若一并 OCR 会污染收发方向判定，故按比例裁掉。
/// 默认值为经验起点，需在真机上按实际布局校准（M2 探针）。
/// </summary>
public sealed record ChatRegionOptions
{
    /// <summary>裁掉的左侧宽度占窗口宽度的比例（会话列表/图标栏）。</summary>
    public double TrimLeftRatio { get; init; } = 0.30;

    /// <summary>裁掉的顶部高度占窗口高度的比例（标题/标签/联系人栏）。</summary>
    public double TrimTopRatio { get; init; } = 0.10;

    /// <summary>裁掉的右侧宽度占窗口宽度的比例（一般无需裁剪）。</summary>
    public double TrimRightRatio { get; init; } = 0.0;

    /// <summary>裁掉的底部高度占窗口高度的比例（输入框/工具栏）。</summary>
    public double TrimBottomRatio { get; init; } = 0.25;
}

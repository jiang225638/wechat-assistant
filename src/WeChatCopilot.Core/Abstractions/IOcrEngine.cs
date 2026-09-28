using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Core.Abstractions;

/// <summary>
/// OCR 引擎抽象。实现需可从 <see cref="CapturedImage"/> 的原始 BGRA 像素识别文本与边界框。
/// M0 提供 Windows.Media.Ocr（系统内置、零依赖）；后续可加 Sdcb.PaddleOCR 对比精度/速度。
/// </summary>
public interface IOcrEngine
{
    /// <summary>引擎名称（用于探针报告与日志）。</summary>
    string Name { get; }

    /// <summary>该引擎在当前系统是否可用（例如所需的中文识别语言包是否已安装）。</summary>
    bool IsAvailable { get; }

    /// <summary>识别一张截图，返回文本与结构化行/词。</summary>
    Task<OcrResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken = default);
}

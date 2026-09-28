using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace WeChatCopilot.Ocr;

/// <summary>
/// 基于系统内置 Windows.Media.Ocr 的 OCR 引擎（零外部依赖，M0 首选跑通对象）。
/// 优先中文（zh-Hans-CN），无中文包时回退到用户配置的识别语言。
/// 依赖系统安装 OCR 语言包：设置 → 时间和语言 → 语言 → 中文 → 语言选项 → 下载“光学字符识别”。
/// 输入的 <see cref="CapturedImage"/> 为顶向下 BGRA；输出行的词边界框坐标相对截图左上角（像素）。
/// </summary>
public sealed class WindowsMediaOcrEngine : IOcrEngine
{
    private const string PreferredLanguageTag = "zh-Hans-CN";

    public string Name => "Windows.Media.Ocr";

    public bool IsAvailable => CreateEngine() is not null;

    /// <summary>当前系统可用的 OCR 识别语言标签（供探针报告展示，便于诊断缺中文包）。</summary>
    public static IReadOnlyList<string> AvailableLanguages() =>
        WinOcr.OcrEngine.AvailableRecognizerLanguages
            .Select(l => l.LanguageTag)
            .ToList();

    public async Task<OcrResult> RecognizeAsync(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsValid)
        {
            throw new ArgumentException("截图数据无效（尺寸与像素缓冲不匹配）。", nameof(image));
        }

        var engine = CreateEngine()
            ?? throw new InvalidOperationException(
                "Windows.Media.Ocr 不可用：未安装任何 OCR 识别语言包（需中文）。可用语言=" +
                (AvailableLanguages().Count > 0 ? string.Join(", ", AvailableLanguages()) : "无"));

        var sw = Stopwatch.StartNew();

        // BGRA + 预乘 alpha；截图已在采集阶段把 alpha 置为不透明，RGB 不受影响。
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            image.BgraPixels.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Premultiplied);

        var winResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        sw.Stop();

        var lines = new List<OcrLine>(winResult.Lines.Count);
        foreach (var line in winResult.Lines)
        {
            var words = new List<OcrWord>(line.Words.Count);
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWord(w.Text, r.X, r.Y, r.Width, r.Height));
            }

            // WinRT 的行文本对中文按字加空格，改用 CJK 感知拼接器重建行文本
            string joined = OcrTextJoiner.Join(words);
            lines.Add(new OcrLine(joined, words));
        }

        string whole = string.Join("\n", lines.Select(l => l.Text));
        return new OcrResult(whole, lines, Name, sw.Elapsed);
    }

    private static WinOcr.OcrEngine? CreateEngine()
    {
        try
        {
            var preferred = new Language(PreferredLanguageTag);
            if (WinOcr.OcrEngine.IsLanguageSupported(preferred))
            {
                return WinOcr.OcrEngine.TryCreateFromLanguage(preferred);
            }
        }
        catch
        {
            // 语言标签无效或不受支持时回退到用户配置语言。
        }

        return WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
    }
}

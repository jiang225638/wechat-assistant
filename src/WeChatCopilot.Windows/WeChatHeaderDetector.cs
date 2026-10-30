using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Windows.Interop;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace WeChatCopilot.Windows;

/// <summary>
/// 微信当前活跃聊天对象自动识别器：
/// 结合 ① 独立聊天窗口标题探测、② 微信主窗口顶部标题栏轻量像素哈希 + Windows.Media.Ocr 视觉识别（与已知会话名单对齐）。
/// 当标题栏像素未发生变化时仅需 ~0.1ms 哈希比对，零 CPU 开销；仅在用户切换聊天界面导致标题栏像素变化时触发 OCR。
/// </summary>
public sealed class WeChatHeaderDetector
{
    private const string PreferredLanguageTag = "zh-Hans-CN";
    private static readonly string[] ProcessNames = { "Weixin", "WeChat" };

    private static readonly HashSet<string> IgnoredChromeTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "微信",
        "WeChat",
        "Weixin",
        "搜索",
        "聊天信息",
        "通讯录",
        "收藏",
        "朋友圈",
        "视频号",
        "小程序",
        "文件传输助手",
        "折叠置顶聊天",
        "折叠的群聊",
        "服务通知",
        "腾讯新闻",
        "微信团队",
        "微信支付"
    };

    private static readonly Regex TrailingCountRegex = new(@"^(.*?)\s*[\(（]\d+[\)）]\s*$", RegexOptions.Compiled);

    private readonly SemaphoreSlim _ocrGate = new(1, 1);
    private WinOcr.OcrEngine? _ocrEngine;
    private bool _ocrInitAttempted;
    private ulong _lastHeaderPixelHash;
    private string? _lastDetectedHeaderContact;

    /// <summary>
    /// 检查当前前景窗口是否为微信独立聊天窗口（ChatWnd 或标题为具体好友名的独立弹窗）。
    /// </summary>
    public static string? TryGetForegroundChatWindowContact(
        IReadOnlyList<string>? knownContacts = null,
        string? selfNickname = null)
    {
        nint fg = NativeMethods.GetForegroundWindow();
        if (fg == nint.Zero || !NativeMethods.IsWindowVisible(fg) || NativeMethods.IsIconic(fg))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == 0 || !IsWeChatProcess(fgPid))
        {
            return null;
        }

        string title = CleanContactTitle(GetWindowText(fg, NativeMethods.GetWindowText));
        if (string.IsNullOrWhiteSpace(title) || IsIgnoredTitle(title, selfNickname))
        {
            return null;
        }

        string className = GetWindowText(fg, NativeMethods.GetClassName);
        if (string.Equals(className, "ChatWnd", StringComparison.OrdinalIgnoreCase))
        {
            if (knownContacts is { Count: > 0 })
            {
                string? resolved = MatchContactFromOcrLines(new[] { title }, knownContacts, selfNickname, allowUnmatchedFallback: true);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
            return title;
        }

        // 微信 4.x 独立聊天窗口：若标题直接匹配已知联系人列表，直接采信
        if (knownContacts is { Count: > 0 })
        {
            string? matched = MatchContactFromOcrLines(new[] { title }, knownContacts, selfNickname, allowUnmatchedFallback: false);
            if (!string.IsNullOrWhiteSpace(matched))
            {
                return matched;
            }
        }

        return null;
    }

    /// <summary>
    /// 从已知会话摘要与附加联系人列表中提取去重候选名列表，并提供别名到首选 DisplayName 的归一映射。
    /// </summary>
    public static (IReadOnlyList<string> Candidates, Dictionary<string, string> CanonicalMap) BuildKnownContactCandidates(
        IReadOnlyList<WeChatSessionSummary>? sessions,
        IReadOnlyList<string>? extraNames = null)
    {
        var candidates = new List<string>();
        var canonicalMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (sessions is not null)
        {
            foreach (var s in sessions)
            {
                string canonical = !string.IsNullOrWhiteSpace(s.DisplayName) ? s.DisplayName.Trim() : s.Nickname.Trim();
                if (string.IsNullOrWhiteSpace(canonical)) continue;

                AddCandidate(canonical, canonical);
                if (!string.IsNullOrWhiteSpace(s.Remark))
                {
                    AddCandidate(s.Remark.Trim(), canonical);
                }
                if (!string.IsNullOrWhiteSpace(s.Nickname))
                {
                    AddCandidate(s.Nickname.Trim(), canonical);
                }
            }
        }

        if (extraNames is not null)
        {
            foreach (string extra in extraNames)
            {
                if (!string.IsNullOrWhiteSpace(extra))
                {
                    string trimmed = extra.Trim();
                    AddCandidate(trimmed, trimmed);
                }
            }
        }

        void AddCandidate(string alias, string canonical)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            if (!canonicalMap.ContainsKey(alias))
            {
                canonicalMap[alias] = canonical;
                candidates.Add(alias);
            }
        }

        return (candidates, canonicalMap);
    }

    /// <summary>
    /// 探测微信主窗口顶部聊天标题栏当前显示的好友名称。
    /// </summary>
    public async Task<(string? ContactName, bool HeaderChanged)> TryDetectMainWindowHeaderContactAsync(
        WindowInfo? mainWindow,
        WindowBounds? excludeOverlayBounds,
        IReadOnlyList<string> knownContacts,
        string? selfNickname = null,
        bool forceOcr = false,
        CancellationToken cancellationToken = default)
    {
        if (mainWindow is null || !mainWindow.IsVisible || mainWindow.IsMinimized)
        {
            return (null, false);
        }

        if (!forceOcr && !mainWindow.IsForeground)
        {
            return (_lastDetectedHeaderContact, false);
        }

        if (mainWindow.Bounds.Width < 420 || mainWindow.Bounds.Height < 320)
        {
            return (null, false);
        }

        // 微信 3.x / 4.x 主窗口右侧聊天区顶部标题栏区域（左导航+会话列表约占左侧 27%~32%）
        int hX = mainWindow.Bounds.X + (int)(mainWindow.Bounds.Width * 0.27);
        int hY = mainWindow.Bounds.Y + (int)(mainWindow.Bounds.Height * 0.012);
        int hW = Math.Max(80, (int)(mainWindow.Bounds.Width * 0.46));
        int hH = Math.Max(28, (int)(mainWindow.Bounds.Height * 0.075));

        var headerBounds = new WindowBounds(hX, hY, hW, hH);

        // 若副驾窗口自身被拖到了微信标题栏正上方遮挡该区域，跳过抓屏避免误识副驾文字
        if (excludeOverlayBounds is { IsValid: true } ov && Intersects(headerBounds, ov))
        {
            return (_lastDetectedHeaderContact, false);
        }

        // 防遮挡校验：若微信窗口当前不在前台，且标题栏区域被其他窗口（如 IDE/浏览器）遮挡，严禁抓屏以防把底层代码文字（如 wcdb_api.dll）误识为好友
        if (!mainWindow.IsForeground && !IsHeaderRegionVisibleOnWeChat(headerBounds, mainWindow.Handle))
        {
            return (_lastDetectedHeaderContact, false);
        }

        byte[]? bgra = CaptureScreenRegionBgra(headerBounds);
        if (bgra is null)
        {
            return (_lastDetectedHeaderContact, false);
        }

        ulong pixelHash = ComputeFastPixelHash(bgra, hW, hH);
        if (!forceOcr && pixelHash == _lastHeaderPixelHash && _lastHeaderPixelHash != 0)
        {
            return (_lastDetectedHeaderContact, false);
        }

        if (!await _ocrGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return (_lastDetectedHeaderContact, false);
        }

        try
        {
            var engine = GetOrCreateOcrEngine();
            if (engine is null)
            {
                _lastHeaderPixelHash = pixelHash;
                return (null, false);
            }

            // 2 倍最近邻放大，大幅提升小字号中英文昵称识别率
            int scaledW = hW * 2;
            int scaledH = hH * 2;
            byte[] scaledBgra = UpscaleNearest2x(bgra, hW, hH);

            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                scaledBgra.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                scaledW,
                scaledH,
                BitmapAlphaMode.Premultiplied);

            var ocrResult = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            var rawLines = new List<string>(ocrResult.Lines.Count);
            foreach (var line in ocrResult.Lines)
            {
                string joined = JoinOcrWordsCjkAware(line.Words.Select(w => w.Text));
                if (!string.IsNullOrWhiteSpace(joined))
                {
                    rawLines.Add(joined);
                }
            }

            // 当本地数据库已加载已知好友列表（knownContacts.Count > 0）时，严格只对齐真实好友，绝不盲目采信未匹配的屏幕杂散文字
            string? matched = MatchContactFromOcrLines(
                rawLines,
                knownContacts,
                selfNickname,
                allowUnmatchedFallback: knownContacts.Count == 0);

            string? prev = _lastDetectedHeaderContact;
            _lastHeaderPixelHash = pixelHash;
            if (!string.IsNullOrWhiteSpace(matched))
            {
                _lastDetectedHeaderContact = matched;
            }

            bool changed = !string.IsNullOrWhiteSpace(matched) &&
                           !string.Equals(prev, matched, StringComparison.OrdinalIgnoreCase);
            return (matched ?? _lastDetectedHeaderContact, changed);
        }
        catch
        {
            return (_lastDetectedHeaderContact, false);
        }
        finally
        {
            _ocrGate.Release();
        }
    }

    /// <summary>
    /// 从 OCR 识别出的标题栏文本行中匹配最佳联系人名称（纯函数，便于单元测试）。
    /// </summary>
    public static string? MatchContactFromOcrLines(
        IReadOnlyList<string> rawLines,
        IReadOnlyList<string> knownContacts,
        string? selfNickname = null,
        bool allowUnmatchedFallback = false)
    {
        if (rawLines is null || rawLines.Count == 0)
        {
            return null;
        }

        var cleanedLines = new List<string>();
        foreach (string raw in rawLines)
        {
            string cleaned = CleanContactTitle(raw);
            if (!string.IsNullOrWhiteSpace(cleaned) && !IsIgnoredTitle(cleaned, selfNickname))
            {
                cleanedLines.Add(cleaned);
            }
        }

        if (cleanedLines.Count == 0)
        {
            return null;
        }

        if (knownContacts is { Count: > 0 })
        {
            var validKnown = knownContacts
                .Where(k => !string.IsNullOrWhiteSpace(k) && !IsIgnoredTitle(k, selfNickname))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 1. 精确匹配（忽略空格与大小写）
            foreach (string line in cleanedLines)
            {
                string compactLine = CompactForCompare(line);
                if (compactLine.Length == 0) continue;

                foreach (string known in validKnown)
                {
                    if (string.Equals(compactLine, CompactForCompare(known), StringComparison.OrdinalIgnoreCase))
                    {
                        return known;
                    }
                }
            }

            // 2. 包含匹配（优先匹配更长的已知联系人名，避免短名字误命中）
            string? bestContained = null;
            int bestContainedLen = 0;
            foreach (string line in cleanedLines)
            {
                string compactLine = CompactForCompare(line);
                if (compactLine.Length < 2) continue;

                foreach (string known in validKnown)
                {
                    string compactKnown = CompactForCompare(known);
                    if (compactKnown.Length < 2) continue;

                    if (compactLine.Contains(compactKnown, StringComparison.OrdinalIgnoreCase))
                    {
                        if (compactKnown.Length > bestContainedLen)
                        {
                            bestContained = known;
                            bestContainedLen = compactKnown.Length;
                        }
                    }
                    else if (compactKnown.Contains(compactLine, StringComparison.OrdinalIgnoreCase) &&
                             compactLine.Length >= Math.Max(2, (int)Math.Ceiling(compactKnown.Length * 0.6)))
                    {
                        if (compactLine.Length > bestContainedLen)
                        {
                            bestContained = known;
                            bestContainedLen = compactLine.Length;
                        }
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(bestContained))
            {
                return bestContained;
            }

            // 3. 针对 3 字及以上中文昵称的容错匹配（允许单个复杂笔画汉字被 OCR 近形误识，要求 >= 75% 顺序字符重合）
            string? bestFuzzy = null;
            double bestScore = 0;
            foreach (string line in cleanedLines)
            {
                string compactLine = CompactForCompare(line);
                if (compactLine.Length < 3) continue;

                foreach (string known in validKnown)
                {
                    string compactKnown = CompactForCompare(known);
                    if (compactKnown.Length < 3) continue;
                    if (Math.Abs(compactLine.Length - compactKnown.Length) > 1) continue;

                    int lcs = ComputeLcsLength(compactLine, compactKnown);
                    double ratio = (double)lcs / Math.Max(compactLine.Length, compactKnown.Length);
                    if (ratio >= 0.75 && ratio > bestScore)
                    {
                        bestScore = ratio;
                        bestFuzzy = known;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(bestFuzzy))
            {
                return bestFuzzy;
            }
        }

        if (allowUnmatchedFallback)
        {
            foreach (string line in cleanedLines)
            {
                if (line.Length is >= 2 and <= 20)
                {
                    return line;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 清洗窗口或标题栏中的联系人名称（剥离末尾未读数/群人数括号与多余符号）。
    /// </summary>
    public static string CleanContactTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string text = raw.Trim();
        var match = TrailingCountRegex.Match(text);
        if (match.Success)
        {
            text = match.Groups[1].Value.Trim();
        }

        // 剥离标题栏右侧可能被一并扫入的“...”或耳麦符号
        text = text.TrimEnd('.', '…', '·', '-', '_', '|', ' ');
        return text.Trim();
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativeMethods.POINT point);

    private static readonly Regex CodeOrFileNoiseRegex = new(
        @"\.(?:dll|exe|cs|cpp|h|py|js|ts|json|xml|xaml|md|txt|log|db|sqlite|sln|slnx|csproj)\b|(?:https?://|[a-zA-Z]:\\|\\\\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsHeaderRegionVisibleOnWeChat(WindowBounds headerBounds, nint weChatHandle)
    {
        if (!headerBounds.IsValid)
        {
            return false;
        }

        uint weChatPid = 0;
        if (weChatHandle != nint.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(weChatHandle, out weChatPid);
        }

        // 采样标题栏左中两个关键点，确保该屏幕区域未被 IDE/浏览器等其他窗口遮挡
        var ptLeft = new NativeMethods.POINT
        {
            X = headerBounds.X + Math.Max(16, headerBounds.Width / 4),
            Y = headerBounds.Y + (headerBounds.Height / 2)
        };
        var ptMid = new NativeMethods.POINT
        {
            X = headerBounds.X + (headerBounds.Width / 2),
            Y = headerBounds.Y + (headerBounds.Height / 2)
        };

        return IsPointBelongingToWeChat(ptLeft, weChatHandle, weChatPid) &&
               IsPointBelongingToWeChat(ptMid, weChatHandle, weChatPid);
    }

    private static bool IsPointBelongingToWeChat(NativeMethods.POINT pt, nint weChatHandle, uint weChatPid)
    {
        nint hwnd = WindowFromPoint(pt);
        if (hwnd == nint.Zero)
        {
            return false;
        }

        if (weChatHandle != nint.Zero && hwnd == weChatHandle)
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (weChatPid != 0 && pid == weChatPid) || IsWeChatProcess(pid);
    }

    private static bool IsIgnoredTitle(string title, string? selfNickname)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        string compact = CompactForCompare(title);
        if (compact.Length == 0 || IgnoredChromeTitles.Contains(title) || IgnoredChromeTitles.Contains(compact))
        {
            return true;
        }

        if (CodeOrFileNoiseRegex.IsMatch(title) || CodeOrFileNoiseRegex.IsMatch(compact))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(selfNickname) &&
            string.Equals(compact, CompactForCompare(selfNickname), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string CompactForCompare(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static int ComputeLcsLength(string a, string b)
    {
        int m = a.Length;
        int n = b.Length;
        int[,] dp = new int[m + 1, n + 1];
        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]))
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                }
                else
                {
                    dp[i, j] = Math.Max(dp[i - 1, j], dp[i, j - 1]);
                }
            }
        }
        return dp[m, n];
    }

    private static string JoinOcrWordsCjkAware(IEnumerable<string> words)
    {
        var sb = new StringBuilder();
        char? prevLast = null;
        foreach (string raw in words)
        {
            string w = raw.Trim();
            if (w.Length == 0) continue;

            if (prevLast.HasValue && IsAsciiWordChar(prevLast.Value) && IsAsciiWordChar(w[0]))
            {
                sb.Append(' ');
            }

            sb.Append(w);
            prevLast = w[^1];
        }
        return sb.ToString();
    }

    private static bool IsAsciiWordChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private WinOcr.OcrEngine? GetOrCreateOcrEngine()
    {
        if (_ocrInitAttempted)
        {
            return _ocrEngine;
        }

        _ocrInitAttempted = true;
        try
        {
            var preferred = new Language(PreferredLanguageTag);
            if (WinOcr.OcrEngine.IsLanguageSupported(preferred))
            {
                _ocrEngine = WinOcr.OcrEngine.TryCreateFromLanguage(preferred);
                if (_ocrEngine is not null)
                {
                    return _ocrEngine;
                }
            }
        }
        catch
        {
            // 回退至系统默认用户语言
        }

        try
        {
            _ocrEngine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            _ocrEngine = null;
        }

        return _ocrEngine;
    }

    private static bool Intersects(WindowBounds a, WindowBounds b)
    {
        return a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;
    }

    private static ulong ComputeFastPixelHash(byte[] bgra, int width, int height)
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        ulong hash = FnvOffset ^ (ulong)width ^ ((ulong)height << 32);
        int step = 16; // 每 4 个像素采样 1 次
        for (int i = 0; i + 2 < bgra.Length; i += step)
        {
            // 量化低 2 位以过滤亚像素抗锯齿微抖动
            ulong sample = (ulong)((bgra[i] >> 2) | ((bgra[i + 1] >> 2) << 8) | ((bgra[i + 2] >> 2) << 16));
            hash ^= sample;
            hash *= FnvPrime;
        }
        return hash;
    }

    private static byte[] UpscaleNearest2x(byte[] src, int width, int height)
    {
        int dstW = width * 2;
        int dstH = height * 2;
        byte[] dst = new byte[dstW * dstH * 4];

        for (int y = 0; y < dstH; y++)
        {
            int srcY = y / 2;
            int srcRowOffset = srcY * width * 4;
            int dstRowOffset = y * dstW * 4;
            for (int x = 0; x < dstW; x++)
            {
                int srcIdx = srcRowOffset + (x / 2) * 4;
                int dstIdx = dstRowOffset + x * 4;
                dst[dstIdx] = src[srcIdx];
                dst[dstIdx + 1] = src[srcIdx + 1];
                dst[dstIdx + 2] = src[srcIdx + 2];
                dst[dstIdx + 3] = 0xFF;
            }
        }

        return dst;
    }

    private static byte[]? CaptureScreenRegionBgra(WindowBounds region)
    {
        if (!region.IsValid)
        {
            return null;
        }

        int width = region.Width;
        int height = region.Height;

        nint screenDc = NativeMethods.GetDC(nint.Zero);
        if (screenDc == nint.Zero)
        {
            return null;
        }

        nint memDc = nint.Zero;
        nint bitmap = nint.Zero;
        nint oldBitmap = nint.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == nint.Zero)
            {
                return null;
            }

            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == nint.Zero)
            {
                return null;
            }

            oldBitmap = NativeMethods.SelectObject(memDc, bitmap);
            if (!NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc, region.X, region.Y, NativeMethods.SRCCOPY))
            {
                return null;
            }

            NativeMethods.SelectObject(memDc, oldBitmap);
            oldBitmap = nint.Zero;

            byte[] pixels = new byte[width * height * 4];
            var header = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            };

            int lines = NativeMethods.GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref header, NativeMethods.DIB_RGB_COLORS);
            if (lines != height)
            {
                return null;
            }

            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
            }

            return pixels;
        }
        finally
        {
            if (oldBitmap != nint.Zero && memDc != nint.Zero)
            {
                NativeMethods.SelectObject(memDc, oldBitmap);
            }
            if (bitmap != nint.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }
            if (memDc != nint.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }
            NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    private static bool IsWeChatProcess(uint pid)
    {
        foreach (string name in ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if ((uint)p.Id == pid)
                    {
                        return true;
                    }
                }
                catch
                {
                    // 忽略进程退出
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return false;
    }

    private static string GetWindowText(nint hwnd, Func<nint, StringBuilder, int, int> getter)
    {
        var sb = new StringBuilder(512);
        getter(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

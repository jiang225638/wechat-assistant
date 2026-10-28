using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace WeChatCopilot.App;

/// <summary>
/// 微信自定义斗图大表情包（Type 47）与远程 CDN 多媒体异步加载与本地缓存管理器。
/// </summary>
public static class WeChatCdnMediaDownloader
{
    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string s_emojiCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeChatCopilot",
        "Cache",
        "Emojis");

    private static readonly ConcurrentDictionary<string, Task<string?>> s_downloadTasks = new();

    static WeChatCdnMediaDownloader()
    {
        try
        {
            if (!Directory.Exists(s_emojiCacheDir))
            {
                Directory.CreateDirectory(s_emojiCacheDir);
            }
        }
        catch
        {
            // 忽略创建目录异常
        }
    }

    /// <summary>
    /// 获取大表情包本地缓存目录。
    /// </summary>
    public static string EmojiCacheDirectory => s_emojiCacheDir;

    /// <summary>
    /// 异步确保大表情包就绪并返回本地图片物理路径。
    /// 优先级：本地缓存 > 微信本地 CustomEmotion 目录 > 异步拉取微信 CDN。
    /// </summary>
    public static async Task<string?> EnsureEmojiCachedAsync(string? md5, string? cdnUrl, string? accountRoot = null)
    {
        string safeMd5 = (md5 ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(safeMd5))
        {
            if (!string.IsNullOrWhiteSpace(cdnUrl))
            {
                safeMd5 = Math.Abs(cdnUrl.GetHashCode()).ToString("x8");
            }
            else
            {
                return null;
            }
        }

        // 1. 检查应用专有本地缓存
        string localFile = Path.Combine(s_emojiCacheDir, safeMd5 + ".gif");
        if (File.Exists(localFile)) return localFile;

        string localPng = Path.Combine(s_emojiCacheDir, safeMd5 + ".png");
        if (File.Exists(localPng)) return localPng;

        string localJpg = Path.Combine(s_emojiCacheDir, safeMd5 + ".jpg");
        if (File.Exists(localJpg)) return localJpg;

        // 2. 检查微信自带的本地 CustomEmotion 目录
        var searchRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(accountRoot) && Directory.Exists(accountRoot))
        {
            searchRoots.Add(accountRoot);
        }

        // 探测“我的文档”默认微信数据根目录兜底
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string[] docCandidates = { Path.Combine(docs, "WeChat Files"), Path.Combine(docs, "xwechat_files") };
            foreach (var dc in docCandidates)
            {
                if (Directory.Exists(dc))
                {
                    foreach (var sub in Directory.GetDirectories(dc))
                    {
                        searchRoots.Add(sub);
                    }
                }
            }
        }
        catch
        {
            // 忽略探测异常
        }

        foreach (string root in searchRoots)
        {
            string[] localDirs =
            {
                Path.Combine(root, "FileStorage", "CustomEmotion"),
                Path.Combine(root, "file_storage", "custom_emotion"),
                Path.Combine(root, "CustomEmotion")
            };

            foreach (string dir in localDirs)
            {
                if (!Directory.Exists(dir)) continue;

                string[] candidates =
                {
                    Path.Combine(dir, safeMd5),
                    Path.Combine(dir, safeMd5 + ".gif"),
                    Path.Combine(dir, safeMd5 + ".png"),
                    Path.Combine(dir, safeMd5 + ".thumb")
                };

                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        return c;
                    }
                }
            }
        }

        // 3. 远程 CDN 异步拉取
        if (string.IsNullOrWhiteSpace(cdnUrl) || !cdnUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await s_downloadTasks.GetOrAdd(safeMd5, async _ =>
        {
            try
            {
                byte[] bytes = await s_httpClient.GetByteArrayAsync(cdnUrl);
                if (bytes.Length == 0) return null;

                string targetExt = ".gif";
                if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50)
                {
                    targetExt = ".png";
                }
                else if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8)
                {
                    targetExt = ".jpg";
                }

                string outPath = Path.Combine(s_emojiCacheDir, safeMd5 + targetExt);
                await File.WriteAllBytesAsync(outPath, bytes);
                return outPath;
            }
            catch
            {
                return null;
            }
        });
    }
}

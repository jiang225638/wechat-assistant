using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WeChatCopilot.Data;

/// <summary>
/// TraceMemo Local HTTP API Token 提取器。
/// 优先从环境变量 TRACEMEMO_TOKEN 读取；
/// 其次从本地 TraceMemo 的 safeStorage (Local State + local-api-token.bin) 解密读取。
/// </summary>
public static class TraceMemoTokenProvider
{
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();
    private static readonly byte[] V10Prefix = "v10"u8.ToArray();

    /// <summary>
    /// 尝试读取 TraceMemo 的 Bearer Token。
    /// </summary>
    public static bool TryGetToken(out string token, out string? error)
    {
        // 1. 优先读取环境变量
        string? envToken = Environment.GetEnvironmentVariable("TRACEMEMO_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            token = envToken.Trim();
            error = null;
            return true;
        }

        token = string.Empty;

        // 2. 从 %AppData%/TraceMemo/ 读取
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string traceMemoDir = Path.Combine(appData, "TraceMemo");
            string localStatePath = Path.Combine(traceMemoDir, "Local State");
            string tokenBinPath = Path.Combine(traceMemoDir, "local-api-token.bin");

            if (!File.Exists(localStatePath))
            {
                error = $"未找到 TraceMemo Local State 文件: {localStatePath}";
                return false;
            }

            if (!File.Exists(tokenBinPath))
            {
                error = $"未找到 TraceMemo Token 密钥文件: {tokenBinPath}";
                return false;
            }

            // 读取 master key
            string localStateJson = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(localStateJson);
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                !osCrypt.TryGetProperty("encrypted_key", out var encKeyProp))
            {
                error = "Local State 文件中未包含 os_crypt.encrypted_key";
                return false;
            }

            string? b64EncKey = encKeyProp.GetString();
            if (string.IsNullOrWhiteSpace(b64EncKey))
            {
                error = "os_crypt.encrypted_key 为空";
                return false;
            }

            byte[] encKeyBytes = Convert.FromBase64String(b64EncKey);
            if (encKeyBytes.Length < DpapiPrefix.Length || !encKeyBytes.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
            {
                error = "encrypted_key 格式不符合预期 (缺少 DPAPI 前缀)";
                return false;
            }

            byte[] cipherMasterKey = encKeyBytes[DpapiPrefix.Length..];
            byte[] masterKey = ProtectedData.Unprotect(cipherMasterKey, null, DataProtectionScope.CurrentUser);

            // 读取并解密 local-api-token.bin
            byte[] tokenBin = File.ReadAllBytes(tokenBinPath);
            const int tagLength = 16;
            const int nonceLength = 12;
            int minLength = V10Prefix.Length + nonceLength + tagLength;

            if (tokenBin.Length < minLength)
            {
                error = $"local-api-token.bin 长度不足 (长度: {tokenBin.Length})";
                return false;
            }

            if (!tokenBin.AsSpan(0, V10Prefix.Length).SequenceEqual(V10Prefix))
            {
                error = "local-api-token.bin 缺少 v10 前缀";
                return false;
            }

            byte[] nonce = tokenBin[V10Prefix.Length..(V10Prefix.Length + nonceLength)];
            int cipherTextLength = tokenBin.Length - V10Prefix.Length - nonceLength - tagLength;
            byte[] cipherText = tokenBin[(V10Prefix.Length + nonceLength)..(V10Prefix.Length + nonceLength + cipherTextLength)];
            byte[] tag = tokenBin[^tagLength..];

            byte[] plainBytes = new byte[cipherTextLength];
            using var aesGcm = new AesGcm(masterKey, tagLength);
            aesGcm.Decrypt(nonce, cipherText, tag, plainBytes);

            token = Encoding.UTF8.GetString(plainBytes).Trim();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "解密 TraceMemo Token 失败: " + ex.Message;
            return false;
        }
    }
}

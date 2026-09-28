using System.Security.Cryptography;
using System.Text;

namespace WeChatCopilot.Data.Security;

/// <summary>
/// 基于 Windows DPAPI (Data Protection API) 的密钥保护辅助类。
/// 将明文 API Key 加密为 Base64 密文，仅限当前 Windows 用户解密，避免明文落盘。
/// </summary>
public static class DpapiProtector
{
    /// <summary>
    /// 加密明文字符串为 Base64 密文。
    /// </summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    /// 解密 Base64 密文为明文字符串。失败或无效时返回空串。
    /// </summary>
    public static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(cipherText);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

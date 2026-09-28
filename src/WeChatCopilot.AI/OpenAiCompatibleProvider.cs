using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.AI;

/// <summary>
/// OpenAI 兼容聊天补全提供商（M3）：走标准 POST {base}/chat/completions。
/// 适用于 OpenAI / DeepSeek / Moonshot / 本地 vLLM 等任何 OpenAI 兼容后端。
/// API Key 通过 <paramref name="apiKeyGetter"/> 惰性获取（调用时才解密），不长期持有明文。
/// 失败不抛异常，统一返回 <see cref="AiReply"/>（Success=false + Error）。
/// </summary>
public sealed class OpenAiCompatibleProvider : IAiProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string> _apiKeyGetter;
    private bool _disposed;

    /// <param name="baseUrl">接口基址（含 /v1），如 https://api.deepseek.com/v1。</param>
    /// <param name="apiKeyGetter">返回明文 API Key 的委托（通常内部做 DPAPI 解密）。</param>
    /// <param name="handler">可选自定义 handler（测试用）。</param>
    public OpenAiCompatibleProvider(string baseUrl, Func<string> apiKeyGetter, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(apiKeyGetter);

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(60);
        _apiKeyGetter = apiKeyGetter;
    }

    public string Name => "OpenAI-Compatible";

    public async Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var payload = new
        {
            model = request.Model,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            }
        };

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            string key = _apiKeyGetter();
            if (string.IsNullOrWhiteSpace(key))
            {
                return new AiReply(false, string.Empty, "未配置 API Key，请先在「AI 设置」中填写。", request.Model);
            }

            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            msg.Content = JsonContent.Create(payload);

            using var resp = await _http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return new AiReply(false, string.Empty, $"HTTP {(int)resp.StatusCode}: {Truncate(body)}", request.Model);
            }

            using var doc = JsonDocument.Parse(body);
            string text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return new AiReply(true, text, null, request.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AiReply(false, string.Empty, ex.Message, request.Model);
        }
    }

    private static string Truncate(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _http.Dispose();
        _disposed = true;
    }
}

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
    private static readonly SocketsHttpHandler s_sharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 20
    };

    private readonly HttpClient _http;
    private readonly Func<string> _apiKeyGetter;
    private bool _disposed;

    /// <param name="baseUrl">接口基址（含 /v1），如 https://api.deepseek.com/v1。</param>
    /// <param name="apiKeyGetter">返回明文 API Key 的委托（通常内部做 DPAPI 解密）。</param>
    /// <param name="handler">可选自定义 handler（测试用）。未指定时复用进程级共享 SocketsHttpHandler 连接池。</param>
    /// <param name="timeout">可选请求超时时间；未指定时默认 180 秒（3 分钟）。</param>
    public OpenAiCompatibleProvider(
        string baseUrl,
        Func<string> apiKeyGetter,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(apiKeyGetter);

        _http = handler is null
            ? new HttpClient(s_sharedHandler, disposeHandler: false)
            : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(180);
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
            messages = BuildMessagesPayload(request)
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            int sec = (int)_http.Timeout.TotalSeconds;
            return new AiReply(false, string.Empty,
                $"请求超时（超过 {sec} 秒未收到大模型响应）：分析长历史记录或深度推理时间较长。建议在设置中调大超时时间或稍后重试。",
                request.Model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AiReply(false, string.Empty, ex.Message, request.Model);
        }
    }

    /// <summary>
    /// 以 Server-Sent Events (SSE) 协议执行真实流式聊天补全。
    /// 走 POST {base}/chat/completions (stream: true)，按 token / chunk 异步返回增量内容。
    /// </summary>
    public async IAsyncEnumerable<string> CompleteStreamAsync(
        AiRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string key = _apiKeyGetter();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("未配置 API Key，请先在「AI 设置」中填写。");
        }

        var payload = new
        {
            model = request.Model,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            stream = true,
            messages = BuildMessagesPayload(request)
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        msg.Content = JsonContent.Create(payload);

        // 必须使用 HttpCompletionOption.ResponseHeadersRead 才能开启即时流式响应
        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            string errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Truncate(errBody)}");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null) break;

            line = line.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                string data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                string? chunk = ExtractDeltaContent(data);
                if (!string.IsNullOrEmpty(chunk))
                {
                    yield return chunk;
                }
            }
        }
    }

    private static List<object> BuildMessagesPayload(AiRequest request)
    {
        var list = new List<object>(2 + (request.HistoryMessages?.Count ?? 0))
        {
            new { role = "system", content = request.SystemPrompt }
        };

        if (request.HistoryMessages is { Count: > 0 })
        {
            foreach (var msg in request.HistoryMessages)
            {
                if (!string.IsNullOrWhiteSpace(msg.Content))
                {
                    string role = msg.Role is "assistant" or "user" or "system" ? msg.Role : "user";
                    list.Add(new { role, content = msg.Content });
                }
            }
        }

        list.Add(new { role = "user", content = request.UserPrompt });
        return list;
    }

    private static string? ExtractDeltaContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice0 = choices[0];
            if (!choice0.TryGetProperty("delta", out var delta))
            {
                return null;
            }

            if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
            {
                return contentProp.GetString();
            }

            return null;
        }
        catch
        {
            return null;
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

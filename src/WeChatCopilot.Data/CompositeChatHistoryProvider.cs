using WeChatCopilot.Core.Abstractions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// 组合式聊天历史提供器：
/// 优先使用微信 4.x WCDB4 本地数据库直读 (<see cref="Wcdb4HistoryProvider"/>)，
/// 若本地直读未就绪或失败，则自动无感回退至 <see cref="TraceMemoClient"/> (HTTP API)。
/// </summary>
public sealed class CompositeChatHistoryProvider : IChatHistoryProvider
{
    private readonly IReadOnlyList<IChatHistoryProvider> _providers;

    public string SourceName => _providers.Count > 0 ? _providers[0].SourceName : "Composite";

    public CompositeChatHistoryProvider(params IChatHistoryProvider[] providers)
    {
        _providers = providers ?? Array.Empty<IChatHistoryProvider>();
    }

    /// <summary>
    /// 按当前配置创建默认组合提供器（WCDB4 本地直读优先 -> TraceMemo HTTP API 兜底）。
    /// </summary>
    public static CompositeChatHistoryProvider CreateDefault(AiSettings? settings = null)
    {
        var resolvedSettings = settings ?? new SettingsStore().Load();
        return new CompositeChatHistoryProvider(new Wcdb4HistoryProvider(resolvedSettings));
    }

    public async Task<ChatHistoryResult> FetchHistoryAsync(string contactName, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        foreach (var provider in _providers)
        {
            try
            {
                var res = await provider.FetchHistoryAsync(contactName, cancellationToken).ConfigureAwait(false);
                if (res.Success)
                {
                    return string.IsNullOrEmpty(res.SourceName)
                        ? res with { SourceName = provider.SourceName }
                        : res;
                }

                if (!string.IsNullOrWhiteSpace(res.Error))
                {
                    errors.Add($"[{provider.SourceName}] {res.Error}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"[{provider.SourceName}] {ex.Message}");
            }
        }

        string combinedError = errors.Count > 0
            ? string.Join("；", errors)
            : "未能从任何历史数据源读取到聊天记录";

        return new ChatHistoryResult(false, Array.Empty<HistoryMessage>(), combinedError, SourceName);
    }

    public async Task<string?> GetActiveContactNameAsync(CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                string? name = await provider.GetActiveContactNameAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<WeChatSessionSummary>> GetRecentSessionsAsync(int maxCount = 40, CancellationToken cancellationToken = default)
    {
        foreach (var provider in _providers)
        {
            try
            {
                var sessions = await provider.GetRecentSessionsAsync(maxCount, cancellationToken).ConfigureAwait(false);
                if (sessions.Count > 0)
                {
                    return sessions;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        return Array.Empty<WeChatSessionSummary>();
    }

    public async Task<ChatConnectionStatus> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        ChatConnectionStatus? firstFailure = null;

        foreach (var provider in _providers)
        {
            try
            {
                var status = await provider.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (status.Success)
                {
                    return status;
                }
                firstFailure ??= status;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailure ??= new ChatConnectionStatus(false, string.Empty, string.Empty, 0, ex.Message, provider.SourceName);
            }
        }

        return firstFailure ?? new ChatConnectionStatus(false, string.Empty, string.Empty, 0, "未配置任何历史数据源", SourceName);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            try
            {
                provider.Dispose();
            }
            catch
            {
            }
        }
    }
}

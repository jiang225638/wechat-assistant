using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace WeChatCopilot.Data;

/// <summary>
/// GitHub Releases 在线版本检查结果。
/// </summary>
public sealed record UpdateCheckResult(
    bool Success,
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseTitle,
    string ReleaseNotes,
    string HtmlUrl,
    string? SetupDownloadUrl,
    string? PortableDownloadUrl,
    string? Error = null);

/// <summary>
/// GitHub Release 在线检查更新与安装包下载服务（仓库：jiang225638/wechat-assistant）。
/// </summary>
public sealed class GitHubReleaseUpdater : IDisposable
{
    public const string DefaultRepoOwner = "jiang225638";
    public const string DefaultRepoName = "wechat-assistant";
    public const string ReleasesPageUrl = $"https://github.com/{DefaultRepoOwner}/{DefaultRepoName}/releases";

    private static readonly string LatestReleaseApiUrl =
        $"https://api.github.com/repos/{DefaultRepoOwner}/{DefaultRepoName}/releases/latest";

    private static readonly string AllReleasesApiUrl =
        $"https://api.github.com/repos/{DefaultRepoOwner}/{DefaultRepoName}/releases?per_page=1";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GitHubReleaseUpdater(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = handler is null;
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WeChatCopilot", "3.2"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// 获取当前运行程序的版本号（例如 3.2.16）。
    /// </summary>
    public static string GetCurrentAppVersion()
    {
        try
        {
            var entry = Assembly.GetEntryAssembly() ?? typeof(GitHubReleaseUpdater).Assembly;
            string? infoVer = entry.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                return CleanVersionString(infoVer);
            }

            Version? ver = entry.GetName().Version;
            if (ver is not null)
            {
                return ver.Revision > 0
                    ? $"{ver.Major}.{ver.Minor}.{ver.Build}.{ver.Revision}"
                    : $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
            }
        }
        catch
        {
        }

        return "3.2.0";
    }

    /// <summary>
    /// 检查 GitHub Releases 是否存在比当前版本更新的正式发布版本。
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(
        string? currentVersion = null,
        CancellationToken cancellationToken = default)
    {
        string curVer = CleanVersionString(string.IsNullOrWhiteSpace(currentVersion) ? GetCurrentAppVersion() : currentVersion!);

        try
        {
            using var response = await _http.GetAsync(LatestReleaseApiUrl, cancellationToken).ConfigureAwait(false);
            JsonElement releaseEl;
            JsonDocument? doc = null;

            try
            {
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    doc = JsonDocument.Parse(json);
                    releaseEl = doc.RootElement;
                }
                else if ((int)response.StatusCode == 404)
                {
                    using var listResp = await _http.GetAsync(AllReleasesApiUrl, cancellationToken).ConfigureAwait(false);
                    if (!listResp.IsSuccessStatusCode)
                    {
                        return new UpdateCheckResult(
                            false, false, curVer, curVer, string.Empty, string.Empty, ReleasesPageUrl, null, null,
                            $"GitHub 暂未发布任何 Release 版本 (HTTP {(int)listResp.StatusCode})");
                    }

                    string listJson = await listResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    doc = JsonDocument.Parse(listJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                    {
                        return new UpdateCheckResult(
                            false, false, curVer, curVer, string.Empty, string.Empty, ReleasesPageUrl, null, null,
                            "仓库尚无已发布的 Release 版本");
                    }

                    releaseEl = doc.RootElement[0];
                }
                else
                {
                    return new UpdateCheckResult(
                        false, false, curVer, curVer, string.Empty, string.Empty, ReleasesPageUrl, null, null,
                        $"连接 GitHub Release 接口返回 HTTP {(int)response.StatusCode}");
                }

                string tagName = GetString(releaseEl, "tag_name");
                string latestVer = CleanVersionString(tagName);
                if (string.IsNullOrWhiteSpace(latestVer))
                {
                    return new UpdateCheckResult(
                        false, false, curVer, curVer, string.Empty, string.Empty, ReleasesPageUrl, null, null,
                        "未能从 GitHub Release 解析出有效版本号");
                }

                string title = GetString(releaseEl, "name");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = $"v{latestVer}";
                }

                string body = GetString(releaseEl, "body");
                string htmlUrl = GetString(releaseEl, "html_url");
                if (string.IsNullOrWhiteSpace(htmlUrl))
                {
                    htmlUrl = ReleasesPageUrl;
                }

                string? setupUrl = null;
                string? portableUrl = null;

                if (releaseEl.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        string assetName = GetString(asset, "name");
                        string downloadUrl = GetString(asset, "browser_download_url");
                        if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                        {
                            continue;
                        }

                        if (assetName.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
                            assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            setupUrl = downloadUrl;
                        }
                        else if (assetName.Contains("Portable", StringComparison.OrdinalIgnoreCase) &&
                                 assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            portableUrl = downloadUrl;
                        }
                        else if (setupUrl is null && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            setupUrl = downloadUrl;
                        }
                    }
                }

                bool hasUpdate = IsNewerVersion(curVer, latestVer);
                return new UpdateCheckResult(
                    Success: true,
                    HasUpdate: hasUpdate,
                    CurrentVersion: curVer,
                    LatestVersion: latestVer,
                    ReleaseTitle: title,
                    ReleaseNotes: body,
                    HtmlUrl: htmlUrl,
                    SetupDownloadUrl: setupUrl,
                    PortableDownloadUrl: portableUrl);
            }
            finally
            {
                doc?.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new UpdateCheckResult(
                false, false, curVer, curVer, string.Empty, string.Empty, ReleasesPageUrl, null, null,
                "检查更新失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 下载更新安装包到指定本地路径，并实时报告下载进度（0 ~ 100）。
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        string destinationFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempFilePath = destinationFilePath + ".part";
        if (File.Exists(tempFilePath))
        {
            try { File.Delete(tempFilePath); } catch { }
        }

        using var response = await _http.GetAsync(
            downloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalRead += bytesRead;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double pct = Math.Clamp((double)totalRead / totalBytes.Value * 100.0, 0.0, 100.0);
                    progress?.Report(pct);
                }
            }
        }

        if (File.Exists(destinationFilePath))
        {
            File.Delete(destinationFilePath);
        }

        File.Move(tempFilePath, destinationFilePath);
        progress?.Report(100.0);
        return destinationFilePath;
    }

    /// <summary>
    /// 判断 <paramref name="latestVersion"/> 是否严格大于 <paramref name="currentVersion"/>。
    /// </summary>
    public static bool IsNewerVersion(string? currentVersion, string? latestVersion)
    {
        int[] curParts = ParseVersionParts(currentVersion);
        int[] latParts = ParseVersionParts(latestVersion);

        int maxLen = Math.Max(curParts.Length, latParts.Length);
        for (int i = 0; i < maxLen; i++)
        {
            int c = i < curParts.Length ? curParts[i] : 0;
            int l = i < latParts.Length ? latParts[i] : 0;
            if (l > c) return true;
            if (l < c) return false;
        }

        return false;
    }

    public static string CleanVersionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string s = raw.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s[1..].Trim();
        }

        int plusIdx = s.IndexOf('+');
        if (plusIdx >= 0)
        {
            s = s[..plusIdx];
        }

        int dashIdx = s.IndexOf('-');
        if (dashIdx >= 0)
        {
            s = s[..dashIdx];
        }

        return s.Trim();
    }

    private static int[] ParseVersionParts(string? raw)
    {
        string cleaned = CleanVersionString(raw);
        if (string.IsNullOrEmpty(cleaned))
        {
            return new[] { 0, 0, 0 };
        }

        string[] tokens = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var nums = new List<int>(tokens.Length);
        foreach (string token in tokens)
        {
            if (int.TryParse(token, out int n))
            {
                nums.Add(n);
            }
        }

        return nums.Count > 0 ? nums.ToArray() : new[] { 0, 0, 0 };
    }

    private static string GetString(JsonElement el, string propName)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(propName, out var val) &&
            val.ValueKind == JsonValueKind.String)
        {
            return val.GetString()?.Trim() ?? string.Empty;
        }

        return string.Empty;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}

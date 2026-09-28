// TraceMemo 真机校准探针：验证 token 解密（不回显明文），并在 Bearer 鉴权下探测各端点参数/响应形状。
using System.Net.Http.Headers;
using System.Text.Json;
using WeChatCopilot.Data;

if (!TraceMemoTokenProvider.TryGetToken(out string token, out string? error))
{
    Console.WriteLine("TOKEN FAIL: " + error);
    return 1;
}

Console.WriteLine($"TOKEN OK len={token.Length}");

using var http = new HttpClient
{
    BaseAddress = new Uri("http://127.0.0.1:6131/"),
    Timeout = TimeSpan.FromSeconds(5)
};
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

string[] paths = args.Length > 0 ? args : new[] { "api/v1/contact", "api/v1/recent_chat", "api/v1/resolve", "api/v1/chatlog" };
foreach (string path in paths)
{
    try
    {
        using HttpResponseMessage resp = await http.GetAsync(path);
        string body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"--- GET {path} => {(int)resp.StatusCode}: " + Cut(body));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"--- GET {path} EX: " + ex.Message);
    }
}

if (args.Length == 0)
{
    // 流程探测：优先单聊会话 → resolve?q=昵称 → chatlog 参数组合试错（群聊全量历史会超时）
    using JsonDocument recent = await GetJson(http, "api/v1/recent_chat");
    JsonElement first = recent.RootElement.GetProperty("items").EnumerateArray()
        .First(e => e.GetProperty("type").GetString() != "group");
    string nick = first.GetProperty("m_nsNickName").GetString() ?? "";
    string wxid = first.GetProperty("wxid").GetString() ?? "";
    Console.WriteLine($"=== flow target: nick={nick} wxid={wxid} type={first.GetProperty("type").GetString()}");
    using HttpResponseMessage rr = await http.GetAsync("api/v1/resolve?q=" + Uri.EscapeDataString(nick));
    Console.WriteLine($"--- resolve?q => {(int)rr.StatusCode}: " + Cut(await rr.Content.ReadAsStringAsync()));

    using var slow = new HttpClient { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromSeconds(8) };
    slow.DefaultRequestHeaders.Authorization = http.DefaultRequestHeaders.Authorization;
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    string[] queries =
    {
        "talker=" + Uri.EscapeDataString(wxid) + "&limit=5",
        "talker=" + Uri.EscapeDataString(wxid) + "&start=" + (now - 86400 * 7) + "&end=" + now,
        "talker=" + Uri.EscapeDataString(wxid) + "&days=1",
    };
    foreach (string q in queries)
    {
        try
        {
            using HttpResponseMessage cr = await slow.GetAsync("api/v1/chatlog?" + q);
            Console.WriteLine($"--- chatlog?{q} => {(int)cr.StatusCode}: " + Cut(await cr.Content.ReadAsStringAsync()));
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine($"--- chatlog?{q} => TIMEOUT(8s)");
        }
    }
}

return 0;

static async Task<System.Text.Json.JsonDocument> GetJson(HttpClient http, string path)
{
    using HttpResponseMessage resp = await http.GetAsync(path);
    resp.EnsureSuccessStatusCode();
    return System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
}

static string Cut(string s) => s.Length <= 1500 ? s : s[..1500] + "...[cut]";

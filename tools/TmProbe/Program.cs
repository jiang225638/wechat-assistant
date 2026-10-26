// WCDB4 微信 4.x 本地直读 & TraceMemo 真机校准探针
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using WeChatCopilot.Core.Models;
using WeChatCopilot.Data;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0 || args[0] == "--wcdb4")
{
    Console.WriteLine("=== [1] WCDB4 微信 4.x 本地数据库直连探测 (无需启动 TraceMemo) ===");
    var settings = new SettingsStore().Load();
    bool resolved = WeChatDbKeyAndRootProvider.TryResolveConfiguration(
        settings,
        out string accountRoot,
        out string sessionDbPath,
        out string wxid,
        out string key,
        out string? configError);

    if (!resolved)
    {
        Console.WriteLine($"CONFIG FAIL: {configError}");
        return 1;
    }

    Console.WriteLine($"CONFIG OK: accountRoot={accountRoot}, wxid={wxid}, keyLen={key.Length}");

    using var wcdb = new Wcdb4HistoryProvider(settings);
    var sw = Stopwatch.StartNew();
    var status = await wcdb.TestConnectionAsync();
    sw.Stop();
    Console.WriteLine($"TEST CONNECTION ({sw.ElapsedMilliseconds}ms): Success={status.Success}, Sessions={status.SessionCount}, Msg={status.Message}");
    if (!status.Success)
    {
        return 2;
    }

    string? activeContact = await wcdb.GetActiveContactNameAsync();
    Console.WriteLine($"ACTIVE CONTACT: {activeContact ?? "(none)"}");

    string targetContact = args.Length > 1 ? args[1] : (activeContact ?? string.Empty);
    if (!string.IsNullOrEmpty(targetContact))
    {
        sw.Restart();
        var history = await wcdb.FetchHistoryAsync(targetContact);
        sw.Stop();
        Console.WriteLine($"FETCH HISTORY [{targetContact}] ({sw.ElapsedMilliseconds}ms): Success={history.Success}, Count={history.Messages.Count}, Source={history.SourceName}, Error={history.Error}");
        foreach (var msg in history.Messages.TakeLast(5))
        {
            Console.WriteLine($"  [{msg.Timestamp:yyyy-MM-dd HH:mm:ss}] [{msg.Role}] {Cut(msg.Text, 100)}");
        }
    }

    if (args.Length > 0 && args[0] == "--wcdb4")
    {
        return 0;
    }
}

return 0;

static string Cut(string s, int max = 1500) => s.Length <= max ? s : s[..max] + "...[cut]";

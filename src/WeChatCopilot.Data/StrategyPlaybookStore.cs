using System.IO;
using System.Text.Json;
using WeChatCopilot.Core.Ai;

namespace WeChatCopilot.Data;

/// <summary>
/// 策略手册缓存：%AppData%/WeChatCopilot/strategy_cache/{key}.json。
/// 键由 <see cref="StrategyPlaybookGenerator.ComputeCacheKey"/> 生成，画像/技能/目的/对话变化后自动失效。
/// </summary>
public sealed class StrategyPlaybookStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly string _dir;

    public StrategyPlaybookStore(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot");
        _dir = Path.Combine(directory, "strategy_cache");
        Directory.CreateDirectory(_dir);
    }

    public StrategyPlaybook? Load(string key)
    {
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StrategyPlaybook>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public void Save(string key, StrategyPlaybook playbook)
    {
        try
        {
            File.WriteAllText(PathFor(key), JsonSerializer.Serialize(playbook, s_jsonOptions));
        }
        catch
        {
            // 缓存写入失败不影响主流程
        }
    }

    private string PathFor(string key) => Path.Combine(_dir, key + ".json");
}

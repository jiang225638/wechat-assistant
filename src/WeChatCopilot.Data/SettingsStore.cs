using System.Text.Json;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// AI 设置持久化（M3）：把 <see cref="AiSettings"/> 以 JSON 存到
/// %AppData%/WeChatCopilot/settings.json。API Key 字段本身已是 DPAPI 密文，落盘安全。
/// 读取失败/文件缺失时返回默认设置，保证应用可用。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    /// <param name="directory">设置目录；缺省为 %AppData%/WeChatCopilot。</param>
    public SettingsStore(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
    }

    /// <summary>设置文件完整路径（供诊断展示）。</summary>
    public string FilePath => _path;

    /// <summary>加载设置；缺失或损坏时返回默认值。</summary>
    public AiSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AiSettings();
        }

        try
        {
            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AiSettings>(json) ?? new AiSettings();
        }
        catch (JsonException)
        {
            return new AiSettings();
        }
    }

    /// <summary>保存设置（覆盖写）。</summary>
    public void Save(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, s_jsonOptions));
    }
}

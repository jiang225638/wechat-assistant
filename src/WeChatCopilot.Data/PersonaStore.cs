using System.Text.Json;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// M5 画像持久化：persona 以 JSON 存 %AppData%/WeChatCopilot/personas/{联系人}.json（M6 迁 SQLite）。
/// 文件名对非法字符做净化；读取失败/缺失返回 null，保证应用可用。
/// </summary>
public sealed class PersonaStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    /// <param name="directory">画像目录；缺省为 %AppData%/WeChatCopilot/personas。</param>
    public PersonaStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot", "personas");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>画像目录完整路径（供诊断展示）。</summary>
    public string DirectoryPath => _directory;

    /// <summary>加载某联系人画像；缺失或损坏返回 null。</summary>
    public Persona? Load(string contactName)
    {
        string path = PathFor(contactName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Persona>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>保存画像（按 ContactName 覆盖写）。</summary>
    public void Save(Persona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        File.WriteAllText(PathFor(persona.ContactName), JsonSerializer.Serialize(persona, s_jsonOptions));
    }

    /// <summary>列出已存画像的联系人名（文件名去扩展名）。</summary>
    public IReadOnlyList<string> List() =>
        System.IO.Directory.GetFiles(_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .ToList();

    private string PathFor(string contactName) =>
        Path.Combine(_directory, Sanitize(contactName) + ".json");

    /// <summary>文件名净化：非法字符替换为下划线；空名归为"未知"。</summary>
    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "未知";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString().Trim();
    }
}

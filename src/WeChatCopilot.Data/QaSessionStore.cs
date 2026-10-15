using System.IO;
using System.Text.Json;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// AI 问答历史会话持久化存储：%AppData%/WeChatCopilot/qa_sessions.json。
/// </summary>
public sealed class QaSessionStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _lock = new();

    public QaSessionStore(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "qa_sessions.json");
    }

    public string FilePath => _path;

    public List<QaSession> LoadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return new List<QaSession>();
            try
            {
                string json = File.ReadAllText(_path);
                var list = JsonSerializer.Deserialize<List<QaSession>>(json);
                return list ?? new List<QaSession>();
            }
            catch
            {
                return new List<QaSession>();
            }
        }
    }

    public void SaveAll(List<QaSession> sessions)
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(sessions, s_jsonOptions);
                File.WriteAllText(_path, json);
            }
            catch
            {
                // 忽略非致命写入异常
            }
        }
    }
}

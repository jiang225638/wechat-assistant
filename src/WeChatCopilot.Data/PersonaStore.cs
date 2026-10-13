using System.Text.Json;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// 画像持久化（M5 文件存储，M6 迁移至 SQLite 本地数据库）：
/// 优先通过 <see cref="SqliteStorage"/> 读写 SQLite 数据库，同时保持与旧版 JSON 文件的平滑双写/回退迁移兼容。
/// </summary>
public sealed class PersonaStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly SqliteStorage _sqliteStorage;

    /// <param name="directory">画像 JSON 备份目录；缺省为 %AppData%/WeChatCopilot/personas。</param>
    /// <param name="sqliteStorage">SQLite 存储实例；若为 null 则自动创建（若指定了自定义 directory 则在其内部创建 copilot.db 以实现测试隔离）。</param>
    public PersonaStore(string? directory = null, SqliteStorage? sqliteStorage = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot", "personas");
        Directory.CreateDirectory(_directory);

        if (sqliteStorage != null)
        {
            _sqliteStorage = sqliteStorage;
        }
        else if (directory != null)
        {
            _sqliteStorage = new SqliteStorage(Path.Combine(_directory, "copilot.db"));
        }
        else
        {
            _sqliteStorage = new SqliteStorage();
        }
    }

    /// <summary>画像 JSON 目录完整路径（供诊断展示）。</summary>
    public string DirectoryPath => _directory;

    /// <summary>底层的 SQLite 存储实例。</summary>
    public SqliteStorage SqliteStorage => _sqliteStorage;

    /// <summary>加载某联系人画像：优先从 SQLite 读取；若未命中则回退从 JSON 加载并自动同步迁移至 SQLite。</summary>
    public Persona? Load(string contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return null;
        }

        // 1. 优先查 SQLite 数据库
        var sqlitePersona = _sqliteStorage.GetPersona(contactName);
        if (sqlitePersona is not null)
        {
            return sqlitePersona;
        }

        // 2. 数据库未命中：回退读取 JSON 备份
        string path = PathFor(contactName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var jsonPersona = JsonSerializer.Deserialize<Persona>(File.ReadAllText(path));
            if (jsonPersona is not null)
            {
                // 自动迁移入库 SQLite
                _sqliteStorage.SavePersona(jsonPersona);
                return jsonPersona;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>保存画像：同步持久化至 SQLite 数据库与 JSON 备份文件。</summary>
    public void Save(Persona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        // 1. 写入 SQLite
        _sqliteStorage.SavePersona(persona);

        // 2. 写入 JSON 备份文件
        try
        {
            File.WriteAllText(PathFor(persona.ContactName), JsonSerializer.Serialize(persona, s_jsonOptions));
        }
        catch
        {
            // 忽略文件写异常，保证 SQLite 为权威主库
        }
    }

    /// <summary>删除联系人画像（SQLite 与 JSON 同步清除）。</summary>
    public bool Delete(string contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return false;
        }

        bool deletedSqlite = _sqliteStorage.DeletePersona(contactName);

        string path = PathFor(contactName);
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // 忽略文件系统删除异常
            }
        }

        return deletedSqlite;
    }

    /// <summary>列出已存画像的联系人名（合并 SQLite 与本地 JSON 文件）。</summary>
    public IReadOnlyList<string> List()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _sqliteStorage.ListPersonaNames())
        {
            set.Add(name);
        }

        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.GetFiles(_directory, "*.json"))
            {
                string fn = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(fn))
                {
                    set.Add(fn);
                }
            }
        }

        return set.ToList();
    }

    /// <summary>获取所有已保存的画像对象集合，按更新时间倒序排列。</summary>
    public IReadOnlyList<Persona> GetAllPersonas()
    {
        var list = _sqliteStorage.GetAllPersonas();
        if (list.Count > 0)
        {
            return list;
        }

        // 若 SQLite 暂无，回退从本地 JSON 批量加载并自动入库
        var fromJson = List()
            .Select(Load)
            .Where(p => p is not null)
            .Select(p => p!)
            .OrderByDescending(p => p.UpdatedAt)
            .ToList();

        return fromJson;
    }

    /// <summary>更新某联系人的最终聊天目的（若画像存在则保存新的 UltimateGoal）。</summary>
    public bool UpdateUltimateGoal(string contactName, string? ultimateGoal)
    {
        var existing = Load(contactName);
        if (existing == null)
        {
            return false;
        }

        string? cleanGoal = string.IsNullOrWhiteSpace(ultimateGoal) ? null : ultimateGoal.Trim();
        var updated = existing with { UltimateGoal = cleanGoal };
        Save(updated);
        return true;
    }

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

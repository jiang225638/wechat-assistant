using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// M6 SQLite 本地轻量化存储基础设施：
/// 统一管理历史消息（带指纹去重与离线加载）、画像（Persona）、自定义技能（Skill）与应用配置。
/// 数据库默认位于 %AppData%/WeChatCopilot/copilot.db，开启 WAL 模式以支持高吞吐与高并发。
/// </summary>
public sealed class SqliteStorage : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    /// <param name="dbPath">数据库路径；若为 null 则缺省存放在 %AppData%/WeChatCopilot/copilot.db。</param>
    public SqliteStorage(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot", "copilot.db");

        string? dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        InitializeDatabase();
    }

    /// <summary>当前 SQLite 数据库物理文件路径。</summary>
    public string DatabasePath => _dbPath;

    public void Dispose()
    {
        // 资源通过每次调用的 using 自动释放
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = CreateConnection();

        // 开启 WAL 模式
        using (var walCmd = conn.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            walCmd.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            -- 历史消息表（冷热链路统一持久化，msg_hash 保证幂等去重）
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                contact_name TEXT NOT NULL,
                role INTEGER NOT NULL,
                text TEXT NOT NULL,
                timestamp TEXT,
                msg_hash TEXT NOT NULL UNIQUE
            );
            CREATE INDEX IF NOT EXISTS idx_messages_contact ON messages(contact_name);
            CREATE INDEX IF NOT EXISTS idx_messages_timestamp ON messages(timestamp);

            -- 联系人画像表
            CREATE TABLE IF NOT EXISTS personas (
                contact_name TEXT PRIMARY KEY,
                skill_id TEXT,
                traits_json TEXT NOT NULL,
                ultimate_goal TEXT,
                source_message_count INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            -- 技能表
            CREATE TABLE IF NOT EXISTS skills (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                icon TEXT,
                description TEXT NOT NULL,
                system_prompt TEXT NOT NULL,
                dimensions_json TEXT,
                advice_guideline TEXT,
                suggested_tones_json TEXT,
                is_built_in INTEGER NOT NULL,
                source_file_path TEXT,
                updated_at TEXT NOT NULL
            );

            -- 键值配置表
            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
        ";
        cmd.ExecuteNonQuery();
    }

    #region Messages (历史与实时消息)

    /// <summary>
    /// 批量保存消息并幂等去重。
    /// </summary>
    /// <returns>实际新插入的消息数量。</returns>
    public int SaveMessages(string contactName, IEnumerable<HistoryMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(contactName) || messages == null)
        {
            return 0;
        }

        int insertedCount = 0;
        using var conn = CreateConnection();
        using var transaction = conn.BeginTransaction();
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT OR IGNORE INTO messages (contact_name, role, text, timestamp, msg_hash)
            VALUES (@contact, @role, @text, @timestamp, @hash);
        ";

        var pContact = command.Parameters.Add("@contact", SqliteType.Text);
        var pRole = command.Parameters.Add("@role", SqliteType.Integer);
        var pText = command.Parameters.Add("@text", SqliteType.Text);
        var pTimestamp = command.Parameters.Add("@timestamp", SqliteType.Text);
        var pHash = command.Parameters.Add("@hash", SqliteType.Text);

        foreach (var msg in messages)
        {
            if (string.IsNullOrWhiteSpace(msg.Text))
            {
                continue;
            }

            pContact.Value = contactName.Trim();
            pRole.Value = (int)msg.Role;
            pText.Value = msg.Text;
            pTimestamp.Value = msg.Timestamp.HasValue
                ? msg.Timestamp.Value.ToString("o")
                : (object)DBNull.Value;
            pHash.Value = ComputeMessageHash(contactName, msg.Role, msg.Text, msg.Timestamp);

            insertedCount += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return insertedCount;
    }

    /// <summary>
    /// 获取指定联系人的历史消息，按时间升序排列。
    /// </summary>
    public IReadOnlyList<HistoryMessage> GetMessages(string contactName, int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return Array.Empty<HistoryMessage>();
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();

        if (limit.HasValue && limit.Value > 0)
        {
            // 取最近的 limit 条，但按时间升序返回给上层
            command.CommandText = @"
                SELECT role, text, timestamp FROM (
                    SELECT id, role, text, timestamp FROM messages
                    WHERE contact_name = @contact
                    ORDER BY timestamp DESC, id DESC
                    LIMIT @limit
                ) ORDER BY timestamp ASC, id ASC;
            ";
            command.Parameters.AddWithValue("@limit", limit.Value);
        }
        else
        {
            command.CommandText = @"
                SELECT role, text, timestamp FROM messages
                WHERE contact_name = @contact
                ORDER BY timestamp ASC, id ASC;
            ";
        }

        command.Parameters.AddWithValue("@contact", contactName.Trim());

        var list = new List<HistoryMessage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            int roleInt = reader.GetInt32(0);
            string text = reader.GetString(1);
            DateTime? timestamp = null;
            if (!reader.IsDBNull(2))
            {
                string timeStr = reader.GetString(2);
                if (DateTime.TryParse(timeStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                {
                    timestamp = parsed;
                }
            }

            list.Add(new HistoryMessage((MessageRole)roleInt, text, timestamp));
        }

        return list;
    }

    /// <summary>
    /// 获取指定联系人本地库中最新一条带时间戳消息的发送时间。
    /// </summary>
    public DateTime? GetLatestMessageTimestamp(string contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return null;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT MAX(timestamp) FROM messages WHERE contact_name = @contact AND timestamp IS NOT NULL;";
        command.Parameters.AddWithValue("@contact", contactName.Trim());

        var result = command.ExecuteScalar();
        if (result == null || result is DBNull)
        {
            return null;
        }

        if (DateTime.TryParse(result.ToString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
        {
            return dt;
        }

        return null;
    }

    /// <summary>
    /// 获取指定联系人的本地消息总条数。
    /// </summary>
    public int GetMessageCount(string contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return 0;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE contact_name = @contact;";
        command.Parameters.AddWithValue("@contact", contactName.Trim());

        var result = command.ExecuteScalar();
        return result is long l ? (int)l : Convert.ToInt32(result);
    }

    private static string ComputeMessageHash(string contactName, MessageRole role, string text, DateTime? timestamp)
    {
        string ts = timestamp.HasValue
            ? timestamp.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
            : "";
        string payload = $"{contactName.Trim().ToLowerInvariant()}|{(int)role}|{text.Trim()}|{ts}";
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes);
    }

    #endregion

    #region Persona (联系人画像)

    /// <summary>
    /// 保存或更新联系人画像。
    /// </summary>
    public void SavePersona(Persona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = @"
            INSERT INTO personas (contact_name, skill_id, traits_json, ultimate_goal, source_message_count, updated_at)
            VALUES (@contact, @skill, @traits, @goal, @count, @updated)
            ON CONFLICT(contact_name) DO UPDATE SET
                skill_id = excluded.skill_id,
                traits_json = excluded.traits_json,
                ultimate_goal = excluded.ultimate_goal,
                source_message_count = excluded.source_message_count,
                updated_at = excluded.updated_at;
        ";

        command.Parameters.AddWithValue("@contact", persona.ContactName.Trim());
        command.Parameters.AddWithValue("@skill", (object?)persona.SkillId ?? DBNull.Value);
        command.Parameters.AddWithValue("@traits", JsonSerializer.Serialize(persona.Traits));
        command.Parameters.AddWithValue("@goal", (object?)persona.UltimateGoal ?? DBNull.Value);
        command.Parameters.AddWithValue("@count", persona.SourceMessageCount);
        command.Parameters.AddWithValue("@updated", persona.UpdatedAt.ToString("o"));

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 加载指定联系人画像。
    /// </summary>
    public Persona? GetPersona(string contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return null;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT skill_id, traits_json, ultimate_goal, source_message_count, updated_at FROM personas WHERE contact_name = @contact;";
        command.Parameters.AddWithValue("@contact", contactName.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string? skillId = reader.IsDBNull(0) ? null : reader.GetString(0);
        string traitsJson = reader.GetString(1);
        string? ultimateGoal = reader.IsDBNull(2) ? null : reader.GetString(2);
        int sourceCount = reader.GetInt32(3);
        DateTime updatedAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind);

        var traits = JsonSerializer.Deserialize<IReadOnlyList<PersonaTrait>>(traitsJson) ?? Array.Empty<PersonaTrait>();
        return new Persona(contactName.Trim(), traits, updatedAt, sourceCount, skillId, ultimateGoal);
    }

    /// <summary>
    /// 列出所有已存画像的联系人姓名。
    /// </summary>
    public IReadOnlyList<string> ListPersonaNames()
    {
        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT contact_name FROM personas ORDER BY updated_at DESC;";

        var list = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>
    /// 获取所有画像列表，按更新时间倒序。
    /// </summary>
    public IReadOnlyList<Persona> GetAllPersonas()
    {
        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT contact_name, skill_id, traits_json, ultimate_goal, source_message_count, updated_at FROM personas ORDER BY updated_at DESC;";

        var list = new List<Persona>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string contact = reader.GetString(0);
            string? skillId = reader.IsDBNull(1) ? null : reader.GetString(1);
            string traitsJson = reader.GetString(2);
            string? ultimateGoal = reader.IsDBNull(3) ? null : reader.GetString(3);
            int sourceCount = reader.GetInt32(4);
            DateTime updatedAt = DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind);
            var traits = JsonSerializer.Deserialize<IReadOnlyList<PersonaTrait>>(traitsJson) ?? Array.Empty<PersonaTrait>();

            list.Add(new Persona(contact, traits, updatedAt, sourceCount, skillId, ultimateGoal));
        }

        return list;
    }

    /// <summary>
    /// 更新某联系人的战略目的。
    /// </summary>
    public bool UpdateUltimateGoal(string contactName, string? ultimateGoal)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return false;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "UPDATE personas SET ultimate_goal = @goal WHERE contact_name = @contact;";
        command.Parameters.AddWithValue("@contact", contactName.Trim());
        command.Parameters.AddWithValue("@goal", (object?)ultimateGoal?.Trim() ?? DBNull.Value);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 删除某联系人的画像。
    /// </summary>
    public bool DeletePersona(string contactName)
    {
        if (string.IsNullOrWhiteSpace(contactName))
        {
            return false;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM personas WHERE contact_name = @contact;";
        command.Parameters.AddWithValue("@contact", contactName.Trim());

        return command.ExecuteNonQuery() > 0;
    }

    #endregion

    #region App Settings (配置)

    public void SetSetting(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = @"
            INSERT INTO app_settings (key, value) VALUES (@key, @val)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
        ";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@val", value);
        command.ExecuteNonQuery();
    }

    public string? GetSetting(string key, string? defaultValue = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);

        var result = command.ExecuteScalar();
        return result?.ToString() ?? defaultValue;
    }

    #endregion

    #region Skills (技能)

    public void SaveSkill(DistillSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = @"
            INSERT INTO skills (id, name, icon, description, system_prompt, dimensions_json, advice_guideline, suggested_tones_json, is_built_in, source_file_path, updated_at)
            VALUES (@id, @name, @icon, @desc, @prompt, @dims, @advice, @tones, @builtIn, @path, @updated)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                icon = excluded.icon,
                description = excluded.description,
                system_prompt = excluded.system_prompt,
                dimensions_json = excluded.dimensions_json,
                advice_guideline = excluded.advice_guideline,
                suggested_tones_json = excluded.suggested_tones_json,
                is_built_in = excluded.is_built_in,
                source_file_path = excluded.source_file_path,
                updated_at = excluded.updated_at;
        ";

        command.Parameters.AddWithValue("@id", skill.Id);
        command.Parameters.AddWithValue("@name", skill.Name);
        command.Parameters.AddWithValue("@icon", (object?)skill.Icon ?? DBNull.Value);
        command.Parameters.AddWithValue("@desc", skill.Description);
        command.Parameters.AddWithValue("@prompt", skill.SystemPrompt);
        command.Parameters.AddWithValue("@dims", JsonSerializer.Serialize(skill.Dimensions));
        command.Parameters.AddWithValue("@advice", (object?)skill.AdviceGuideline ?? DBNull.Value);
        command.Parameters.AddWithValue("@tones", JsonSerializer.Serialize(skill.SuggestedTones));
        command.Parameters.AddWithValue("@builtIn", skill.IsBuiltIn ? 1 : 0);
        command.Parameters.AddWithValue("@path", (object?)skill.SourceFilePath ?? DBNull.Value);
        command.Parameters.AddWithValue("@updated", DateTime.UtcNow.ToString("o"));

        command.ExecuteNonQuery();
    }

    public DistillSkill? GetSkill(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT id, name, icon, description, system_prompt, dimensions_json, advice_guideline, suggested_tones_json, is_built_in, source_file_path FROM skills WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadSkillFromReader(reader);
    }

    public IReadOnlyList<DistillSkill> GetAllSkills()
    {
        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "SELECT id, name, icon, description, system_prompt, dimensions_json, advice_guideline, suggested_tones_json, is_built_in, source_file_path FROM skills ORDER BY name ASC;";

        var list = new List<DistillSkill>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadSkillFromReader(reader));
        }

        return list;
    }

    public bool DeleteSkill(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        using var conn = CreateConnection();
        using var command = conn.CreateCommand();
        command.CommandText = "DELETE FROM skills WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        return command.ExecuteNonQuery() > 0;
    }

    private static DistillSkill ReadSkillFromReader(SqliteDataReader reader)
    {
        string id = reader.GetString(0);
        string name = reader.GetString(1);
        string icon = reader.IsDBNull(2) ? "💡" : reader.GetString(2);
        string desc = reader.GetString(3);
        string prompt = reader.GetString(4);
        string dimsJson = reader.IsDBNull(5) ? "[]" : reader.GetString(5);
        string? advice = reader.IsDBNull(6) ? null : reader.GetString(6);
        string tonesJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);
        bool isBuiltIn = reader.GetInt32(8) == 1;
        string? sourcePath = reader.IsDBNull(9) ? null : reader.GetString(9);

        var dims = JsonSerializer.Deserialize<IReadOnlyList<string>>(dimsJson) ?? Array.Empty<string>();
        var tones = JsonSerializer.Deserialize<IReadOnlyList<string>>(tonesJson) ?? Array.Empty<string>();

        return new DistillSkill(
            Id: id,
            Name: name,
            Description: desc,
            Icon: icon,
            SystemPrompt: prompt,
            Dimensions: dims,
            IsBuiltIn: isBuiltIn,
            AdviceGuideline: advice,
            SuggestedTones: tones,
            SourceFilePath: sourcePath
        );
    }

    #endregion
}

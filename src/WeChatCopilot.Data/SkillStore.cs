using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// 人格蒸馏技能存储器：管理内置官方技能、工作区扩展技能与用户自定义技能的持久化。
/// 保存位置：%AppData%/WeChatCopilot/custom_skills.json 与 active_skill.txt。
/// </summary>
public sealed class SkillStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _skillsFilePath;
    private readonly string _activeSkillFilePath;

    public SkillStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot");
        Directory.CreateDirectory(_directory);

        _skillsFilePath = Path.Combine(_directory, "custom_skills.json");
        _activeSkillFilePath = Path.Combine(_directory, "active_skill.txt");
    }

    /// <summary>获取当前所有可用技能（内置预设 + 本地工作区发现 + 用户自定义）。</summary>
    public IReadOnlyList<DistillSkill> GetAllSkills()
    {
        var list = new List<DistillSkill>(DistillSkillPresets.AllBuiltIn);

        var workspaceSkills = ScanWorkspaceSkills();
        foreach (var ws in workspaceSkills)
        {
            if (!list.Any(s => s.Id.Equals(ws.Id, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(ws);
            }
        }

        var custom = LoadCustomSkills();
        foreach (var cs in custom)
        {
            if (!list.Any(s => s.Id.Equals(cs.Id, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(cs);
            }
        }
        return list;
    }

    /// <summary>自动扫描工作区 .agents/skills 与 .agent/skills 目录中的扩展技能（如 tdskill 及其子技能 tds-chat）。</summary>
    public IReadOnlyList<DistillSkill> ScanWorkspaceSkills()
    {
        var found = new List<DistillSkill>();
        try
        {
            var roots = GetPotentialSkillRoots();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    string dirName = Path.GetFileName(dir);
                    string skillMd = Path.Combine(dir, "SKILL.md");
                    if (File.Exists(skillMd))
                    {
                        var parsed = ParseSkillMd(dirName, skillMd);
                        if (parsed != null && !found.Any(s => s.Id.Equals(parsed.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            found.Add(parsed);
                        }
                    }

                    // 复合技能的子模块扫描 (如 references/tds-chat 等)
                    string referencesDir = Path.Combine(dir, "references");
                    if (Directory.Exists(referencesDir))
                    {
                        foreach (var subDir in Directory.GetDirectories(referencesDir))
                        {
                            string subName = Path.GetFileName(subDir);
                            string subSkillMd = Path.Combine(subDir, "SKILL.md");
                            if (File.Exists(subSkillMd))
                            {
                                var subParsed = ParseSkillMd(subName, subSkillMd);
                                if (subParsed != null && !found.Any(s => s.Id.Equals(subParsed.Id, StringComparison.OrdinalIgnoreCase)))
                                {
                                    found.Add(subParsed);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略扫描异常
        }

        return found;
    }

    private static IEnumerable<string> GetPotentialSkillRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string current = Directory.GetCurrentDirectory();
        roots.Add(Path.Combine(current, ".agents", "skills"));
        roots.Add(Path.Combine(current, ".agent", "skills"));

        string? baseDir = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(baseDir))
        {
            roots.Add(Path.Combine(baseDir, ".agents", "skills"));
            roots.Add(Path.Combine(baseDir, ".agent", "skills"));

            if (File.Exists(Path.Combine(baseDir, "WeChatCopilot.slnx")) ||
                Directory.Exists(Path.Combine(baseDir, ".git")))
            {
                break;
            }

            var parent = Directory.GetParent(baseDir);
            if (parent is null || parent.FullName == baseDir) break;
            baseDir = parent.FullName;
        }

        return roots;
    }

    /// <summary>从任意本地 SKILL.md 或 Markdown 文件解析提取认知技能元数据与提示词。</summary>
    public static DistillSkill? ParseSkillMdFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        string dirName = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "") ?? "custom";
        if (string.IsNullOrWhiteSpace(dirName) || dirName.Equals("references", StringComparison.OrdinalIgnoreCase))
        {
            dirName = Path.GetFileNameWithoutExtension(filePath);
        }
        return ParseSkillMd(dirName, filePath);
    }

    private static DistillSkill? ParseSkillMd(string folderName, string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath);
            string name = folderName;
            string desc = folderName;
            string icon = folderName.Contains("chat", StringComparison.OrdinalIgnoreCase) ? "💬" :
                          folderName.Contains("attract", StringComparison.OrdinalIgnoreCase) ? "🧲" :
                          folderName.Contains("relat", StringComparison.OrdinalIgnoreCase) ? "🌹" :
                          folderName.Contains("date", StringComparison.OrdinalIgnoreCase) ? "🍷" :
                          folderName.Contains("td", StringComparison.OrdinalIgnoreCase) ? "🐰" : "🧩";

            var match = Regex.Match(content, @"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n(.*)$", RegexOptions.Singleline);
            string body = content;
            if (match.Success)
            {
                string yaml = match.Groups[1].Value;
                body = match.Groups[2].Value.Trim();

                var nameMatch = Regex.Match(yaml, @"name:\s*(.+)$", RegexOptions.Multiline);
                if (nameMatch.Success) name = nameMatch.Groups[1].Value.Trim().Trim('"', '\'');

                var descMatch = Regex.Match(yaml, @"description:\s*(.+?)(?=\r?\n[a-z0-9_-]+:|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (descMatch.Success)
                {
                    desc = descMatch.Groups[1].Value.Trim().Replace("\r", "").Replace("\n", " ").Trim('"', '\'', '|');
                    if (desc.Length > 80) desc = desc[..77] + "...";
                }
            }

            string distillPrompt =
                $"你是基于【{name}】方法论的聊天与对话深度分析专家。\n" +
                $"请依据以下技能指导，分析目标联系人（[对方]）在聊天中所体现的行为模式与认知特质：\n\n" +
                $"{body}\n\n" +
                $"输出格式要求（每项一行，严格使用竖线 | 分隔，严禁将用户发言当成对方特质，证据必须100%摘录对方原话）：\n" +
                $"维度 | 特质描述 | 评分: 0到100的量化分 | 置信度: 0.0到1.0 | 证据: 对方原话引用";

            return new DistillSkill(
                Id: folderName.ToLowerInvariant(),
                Name: name,
                Description: desc,
                Icon: icon,
                SystemPrompt: distillPrompt,
                IsBuiltIn: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>根据技能 ID 检索技能，若不存在则回退至默认女娲技能。</summary>
    public DistillSkill GetSkill(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return DistillSkillPresets.Nuwa;
        }

        var all = GetAllSkills();
        return all.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? DistillSkillPresets.Nuwa;
    }

    /// <summary>获取当前选中的技能 ID（缺省为 nuwa）。</summary>
    public string GetActiveSkillId()
    {
        try
        {
            if (File.Exists(_activeSkillFilePath))
            {
                string id = File.ReadAllText(_activeSkillFilePath).Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }
            }
        }
        catch
        {
            // 忽略文件读取异常
        }

        return DistillSkillPresets.DefaultSkillId;
    }

    /// <summary>持久化当前选中的技能 ID。</summary>
    public void SetActiveSkillId(string id)
    {
        try
        {
            File.WriteAllText(_activeSkillFilePath, id.Trim());
        }
        catch
        {
            // 忽略写入错误
        }
    }

    /// <summary>保存（新增或更新）自定义技能。</summary>
    public void SaveCustomSkill(DistillSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var customList = LoadCustomSkills().ToList();

        // 不允许覆盖内置技能 ID
        if (DistillSkillPresets.AllBuiltIn.Any(b => b.Id.Equals(skill.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"无法覆盖系统内置技能 ID: {skill.Id}");
        }

        int idx = customList.FindIndex(s => s.Id.Equals(skill.Id, StringComparison.OrdinalIgnoreCase));
        var cleanSkill = skill with { IsBuiltIn = false };

        if (idx >= 0)
        {
            customList[idx] = cleanSkill;
        }
        else
        {
            customList.Add(cleanSkill);
        }

        File.WriteAllText(_skillsFilePath, JsonSerializer.Serialize(customList, s_jsonOptions));
    }

    /// <summary>删除自定义技能（内置技能无法删除）。</summary>
    public bool DeleteCustomSkill(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || DistillSkillPresets.AllBuiltIn.Any(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var customList = LoadCustomSkills().ToList();
        int removed = customList.RemoveAll(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            File.WriteAllText(_skillsFilePath, JsonSerializer.Serialize(customList, s_jsonOptions));
            return true;
        }

        return false;
    }

    private IReadOnlyList<DistillSkill> LoadCustomSkills()
    {
        if (!File.Exists(_skillsFilePath))
        {
            return Array.Empty<DistillSkill>();
        }

        try
        {
            string json = File.ReadAllText(_skillsFilePath);
            var list = JsonSerializer.Deserialize<List<DistillSkill>>(json);
            return list ?? (IReadOnlyList<DistillSkill>)Array.Empty<DistillSkill>();
        }
        catch
        {
            return Array.Empty<DistillSkill>();
        }
    }
}

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
    private readonly string _boundDirFilePath;

    public SkillStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WeChatCopilot");
        Directory.CreateDirectory(_directory);

        _skillsFilePath = Path.Combine(_directory, "custom_skills.json");
        _activeSkillFilePath = Path.Combine(_directory, "active_skill.txt");
        _boundDirFilePath = Path.Combine(_directory, "bound_skills_dir.txt");
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

    /// <summary>获取当前绑定的技能库文件夹路径（若未显式指定则自动回退至工作区默认目录）。</summary>
    public string GetBoundSkillsDirectory()
    {
        try
        {
            if (File.Exists(_boundDirFilePath))
            {
                string dir = File.ReadAllText(_boundDirFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    return dir;
                }
            }
        }
        catch
        {
            // 忽略读取异常
        }

        var roots = GetPotentialSkillRoots();
        foreach (var r in roots)
        {
            if (Directory.Exists(r)) return r;
        }

        string defaultDir = Path.Combine(_directory, "skills");
        try
        {
            Directory.CreateDirectory(defaultDir);
        }
        catch (Exception ex)
        {
            WeChatCopilot.Core.Diagnostics.AppLog.Warn("SkillStore", $"创建默认技能目录失败: {defaultDir}", ex);
        }
        return defaultDir;
    }

    /// <summary>持久化绑定技能库文件夹路径。</summary>
    public void SetBoundSkillsDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.WriteAllText(_boundDirFilePath, path.Trim());
        }
        catch (Exception ex)
        {
            WeChatCopilot.Core.Diagnostics.AppLog.Warn("SkillStore", "写入绑定技能目录配置失败", ex);
        }
    }

    /// <summary>自动扫描工作区与绑定目录中的扩展技能（如 local 技能及其子模块）。</summary>
    public IReadOnlyList<DistillSkill> ScanWorkspaceSkills()
    {
        var found = new List<DistillSkill>();
        try
        {
            var roots = GetPotentialSkillRoots();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;

                // 1. 检查根目录本身是否包含 SKILL.md
                string rootSkill = Path.Combine(root, "SKILL.md");
                if (!File.Exists(rootSkill)) rootSkill = Path.Combine(root, "skill.md");
                if (File.Exists(rootSkill))
                {
                    var rootParsed = ParseSkillMd(Path.GetFileName(root), rootSkill);
                    if (rootParsed != null && !found.Any(s => s.Id.Equals(rootParsed.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(rootParsed);
                    }
                }

                // 2. 扫描子目录
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string dirName = Path.GetFileName(dir);
                    string skillMd = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(skillMd)) skillMd = Path.Combine(dir, "skill.md");
                    if (File.Exists(skillMd))
                    {
                        var parsed = ParseSkillMd(dirName, skillMd);
                        if (parsed != null && !found.Any(s => s.Id.Equals(parsed.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            found.Add(parsed);
                        }
                    }

                    // 复合技能的子模块扫描 (如 references/tong-jincheng 等)
                    string referencesDir = Path.Combine(dir, "references");
                    if (Directory.Exists(referencesDir))
                    {
                        foreach (var subDir in Directory.GetDirectories(referencesDir))
                        {
                            string subName = Path.GetFileName(subDir);
                            string subSkillMd = Path.Combine(subDir, "SKILL.md");
                            if (!File.Exists(subSkillMd)) subSkillMd = Path.Combine(subDir, "skill.md");
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

    private IEnumerable<string> GetPotentialSkillRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 优先使用用户绑定的目录
        try
        {
            if (File.Exists(_boundDirFilePath))
            {
                string bound = File.ReadAllText(_boundDirFilePath).Trim();
                if (!string.IsNullOrWhiteSpace(bound) && Directory.Exists(bound))
                {
                    roots.Add(bound);
                }
            }
        }
        catch
        {
            // 忽略
        }

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
            string? yaml = null;
            if (match.Success)
            {
                yaml = match.Groups[1].Value;
                body = match.Groups[2].Value.Trim();

                var nameMatch = Regex.Match(yaml, @"name:\s*(.+)$", RegexOptions.Multiline);
                if (nameMatch.Success) name = nameMatch.Groups[1].Value.Trim().Trim('"', '\'');

                var descMatch = Regex.Match(yaml, @"description:\s*(.+?)(?=\r?\n[a-z0-9_-]+:|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (descMatch.Success)
                {
                    desc = descMatch.Groups[1].Value.Trim().Replace("\r", "").Replace("\n", " ").Trim('"', '\'', '|');
                    if (desc.Length > 240) desc = desc[..237] + "...";
                }
            }

            var dimensions = ExtractDimensionsFromSkill(yaml, body);

            string dimsInstruction = dimensions.Count > 0
                ? $"（特别注意：建议提炼的核心维度包括：【{string.Join("/", dimensions)}】，每维度1行；评分给出10~100量化梯度分）\n"
                : "（特别注意：请精炼出 4~6 个能体现本方法论特色的专属维度，每维度1行；评分给出10~100量化梯度分）\n";

            string distillPrompt =
                $"你是基于【{name}】方法论的聊天与对话深度分析专家。\n" +
                $"请依据以下技能指导，分析目标联系人（[对方]）在聊天中所体现的行为模式与认知特质：\n\n" +
                $"{body}\n\n" +
                $"输出格式要求（每项一行，严格使用竖线 | 分隔，严禁将用户发言当成对方特质，证据必须100%摘录对方原话）：\n" +
                $"维度 | 特质描述 | 评分: 0到100的量化分 | 可信度: 0.0到1.0 | 证据: 对方原话引用\n" +
                dimsInstruction;

            return new DistillSkill(
                Id: folderName.ToLowerInvariant(),
                Name: name,
                Description: desc,
                Icon: icon,
                SystemPrompt: distillPrompt,
                Dimensions: dimensions,
                IsBuiltIn: false,
                SourceFilePath: filePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 智能提取技能中声明或包含的核心评估维度（支持 YAML frontmatter 与 Markdown 正文分析）。
    /// </summary>
    public static IReadOnlyList<string> ExtractDimensionsFromSkill(string? yaml, string? body)
    {
        var dims = new List<string>();

        // 1. 优先从 YAML frontmatter 提取
        if (!string.IsNullOrWhiteSpace(yaml))
        {
            // 模式 1: 块级或数组 dimensions: [a, b, c] 或 dimensions:\n  - a\n  - b
            var blockMatch = Regex.Match(yaml, @"dimensions:\s*(\[[^\]]+\]|[^\r\n]*\r?\n(?:\s*-[^\r\n]+\r?\n?)+)", RegexOptions.IgnoreCase);
            if (blockMatch.Success)
            {
                string block = blockMatch.Groups[1].Value;
                if (block.TrimStart().StartsWith("["))
                {
                    string raw = block.Trim('[', ']', ' ', '\r', '\n');
                    var parts = raw.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var p in parts)
                    {
                        string cleaned = Core.Ai.PersonaDistiller.CleanDimensionName(p);
                        if (cleaned.Length >= 2 && cleaned.Length <= 10) dims.Add(cleaned);
                    }
                }
                else
                {
                    var itemMatches = Regex.Matches(block, @"^\s*-\s*([^\r\n]+)", RegexOptions.Multiline);
                    foreach (Match m in itemMatches)
                    {
                        string cleaned = Core.Ai.PersonaDistiller.CleanDimensionName(m.Groups[1].Value.Trim());
                        if (cleaned.Length >= 2 && cleaned.Length <= 10) dims.Add(cleaned);
                    }
                }
            }

            // 模式 2: 单行非数组 dimensions: a, b, c
            if (dims.Count == 0)
            {
                var singleLineMatch = Regex.Match(yaml, @"dimensions:\s*([^\r\n\[\-]+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (singleLineMatch.Success)
                {
                    var parts = singleLineMatch.Groups[1].Value.Split(new[] { ',', '，', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var p in parts)
                    {
                        string cleaned = Core.Ai.PersonaDistiller.CleanDimensionName(p);
                        if (cleaned.Length >= 2 && cleaned.Length <= 10) dims.Add(cleaned);
                    }
                }
            }
        }

        // 2. 从 Markdown 正文提取
        if (dims.Count == 0 && !string.IsNullOrWhiteSpace(body))
        {
            // 模式 A: 显式标记行，如 "核心维度: A, B, C" 或 "评估维度：A、B、C"
            var textMatch = Regex.Match(body, @"(?:核心维度|评估维度|画像维度|分析维度|六大维度|五大维度|维度)\s*[:：]\s*([^\r\n]+)", RegexOptions.IgnoreCase);
            if (textMatch.Success)
            {
                var parts = textMatch.Groups[1].Value.Split(new[] { ',', '，', '、', ';', '；', '/', '／', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var p in parts)
                {
                    string cleaned = Core.Ai.PersonaDistiller.CleanDimensionName(p);
                    if (cleaned.Length >= 2 && cleaned.Length <= 10) dims.Add(cleaned);
                }
            }

            // 模式 B: 章节列表中的带有维度的编号行，如 "1. 框架强度 (" 或 "1. 表达DNA"
            if (dims.Count == 0)
            {
                var numberedMatches = Regex.Matches(body, @"^\s*(?:[1-9]\.|\*|-)\s*([^\r\n\(（:：]+)[\(（:：]", RegexOptions.Multiline);
                foreach (Match m in numberedMatches)
                {
                    string cand = m.Groups[1].Value.Trim('*', ' ', '`');
                    cand = Core.Ai.PersonaDistiller.CleanDimensionName(cand);
                    if (cand.Length >= 2 && cand.Length <= 10 && !dims.Contains(cand))
                    {
                        dims.Add(cand);
                    }
                    if (dims.Count >= 8) break;
                }
            }
        }

        return dims.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
    }

    /// <summary>
    /// 自动扫描并批量导入指定文件夹及其所有子目录下的 SKILL.md / skill.md 文件。
    /// 自动解析并直接保存到自定义技能库中，返回本次成功导入并保存的技能列表。
    /// </summary>
    public IReadOnlyList<DistillSkill> ScanAndImportFolder(string folderPath)
    {
        var imported = new List<DistillSkill>();
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return imported;
        }

        try
        {
            var files = Directory.GetFiles(folderPath, "*.md", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string name = Path.GetFileName(f);
                    return name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                           name.Equals("skill.md", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var file in files)
            {
                var skill = ParseSkillMdFile(file);
                if (skill != null)
                {
                    // 避免在同一次批量扫描中重复添加
                    if (!imported.Any(s => s.Id.Equals(skill.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        // 内置技能不覆盖
                        if (!DistillSkillPresets.AllBuiltIn.Any(b => b.Id.Equals(skill.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            SaveCustomSkill(skill);
                            imported.Add(skill);
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略外部访问或权限异常
        }

        return imported;
    }

    /// <summary>根据技能 ID 检索技能，若不存在则回退至默认技能。</summary>
    public DistillSkill GetSkill(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return DistillSkillPresets.Intimacy;
        }

        var all = GetAllSkills();
        return all.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
               ?? DistillSkillPresets.Intimacy;
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

    /// <summary>删除自定义技能（内置技能无法删除；若关联本地物理文件则安全删除以防止重新扫描复现）。</summary>
    public bool DeleteCustomSkill(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || DistillSkillPresets.AllBuiltIn.Any(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        bool removedAny = false;

        // 1. 查找全部可用技能中匹配的项，检查是否有本地文件关联
        var targetSkill = GetAllSkills().FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (targetSkill != null && !string.IsNullOrWhiteSpace(targetSkill.SourceFilePath))
        {
            try
            {
                if (File.Exists(targetSkill.SourceFilePath))
                {
                    File.Delete(targetSkill.SourceFilePath);
                    removedAny = true;
                }

                // 若该技能所在目录仅包含该技能文件或为空，安全清理父目录
                string? parentDir = Path.GetDirectoryName(targetSkill.SourceFilePath);
                if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                {
                    var files = Directory.GetFileSystemEntries(parentDir);
                    if (files.Length == 0)
                    {
                        Directory.Delete(parentDir, recursive: true);
                    }
                }
            }
            catch
            {
                // 忽略文件物理删除权限异常
            }
        }

        // 2. 从自定义 JSON 列表中移除
        var customList = LoadCustomSkills().ToList();
        int removedCount = customList.RemoveAll(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (removedCount > 0)
        {
            File.WriteAllText(_skillsFilePath, JsonSerializer.Serialize(customList, s_jsonOptions));
            removedAny = true;
        }

        // 3. 若当前激活的技能恰为被删除的技能，自动回退到默认技能
        if (string.Equals(GetActiveSkillId(), id, StringComparison.OrdinalIgnoreCase))
        {
            SetActiveSkillId(DistillSkillPresets.DefaultSkillId);
        }

        return removedAny;
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

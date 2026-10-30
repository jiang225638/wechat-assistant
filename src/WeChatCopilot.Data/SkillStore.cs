using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeChatCopilot.Core.Diagnostics;
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
    private IReadOnlyList<DistillSkill>? _cachedAllSkills;

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

    /// <summary>使内存中的技能列表缓存失效（在目录切换、导入、增删自定义技能时自动调用）。</summary>
    public void InvalidateCache() => _cachedAllSkills = null;

    /// <summary>获取当前所有可用技能（内置预设 + 本地工作区发现 + 用户自定义）。</summary>
    public IReadOnlyList<DistillSkill> GetAllSkills(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedAllSkills is not null)
        {
            return _cachedAllSkills;
        }

        var list = new List<DistillSkill>(DistillSkillPresets.AllBuiltIn);

        var workspaceSkills = ScanWorkspaceSkillsCore();
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

        _cachedAllSkills = list;
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
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", "读取绑定技能目录配置失败", ex);
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
            AppLog.Warn("SkillStore", $"创建默认技能目录失败: {defaultDir}", ex);
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
            _cachedAllSkills = null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", "写入绑定技能目录配置失败", ex);
        }
    }

    /// <summary>自动扫描工作区与绑定目录中的扩展技能（如 local 技能及其子模块）。</summary>
    public IReadOnlyList<DistillSkill> ScanWorkspaceSkills()
    {
        _cachedAllSkills = null;
        return ScanWorkspaceSkillsCore();
    }

    private IReadOnlyList<DistillSkill> ScanWorkspaceSkillsCore()
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
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", "扫描工作区扩展技能异常", ex);
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
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", "读取潜在技能根目录配置失败", ex);
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

    private static string CapText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string trimmed = text.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "\n...(核心摘要已截断，完整子模块按需渐进式披露)";
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
            string rawBody = content;
            string? yaml = null;
            if (match.Success)
            {
                yaml = match.Groups[1].Value;
                rawBody = match.Groups[2].Value.Trim();

                var nameMatch = Regex.Match(yaml, @"name:\s*(.+)$", RegexOptions.Multiline);
                if (nameMatch.Success) name = nameMatch.Groups[1].Value.Trim().Trim('"', '\'');

                var descMatch = Regex.Match(yaml, @"description:\s*(.+?)(?=\r?\n[a-z0-9_-]+:|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (descMatch.Success)
                {
                    desc = descMatch.Groups[1].Value.Trim().Replace("\r", "").Replace("\n", " ").Trim('"', '\'', '|');
                    if (desc.Length > 240) desc = desc[..237] + "...";
                }
            }

            // 1. 提取评估维度（在净化裁剪前先提取，以免丢失声明式维度）
            var dimensions = ExtractDimensionsFromSkill(yaml, rawBody);
            if (folderName.Equals("tdskill", StringComparison.OrdinalIgnoreCase) ||
                folderName.StartsWith("tds-", StringComparison.OrdinalIgnoreCase))
            {
                dimensions = new List<string>
                {
                    "男女话题与张力",
                    "框架博弈与高位感",
                    "客观话题与舒适度",
                    "需求感与推拉节奏",
                    "基础价值与展示面",
                    "废测识别与应对"
                };
            }

            // 2. 净化主入口正文：剥离 CLI 专属路由说明、新手引导问答与公众号推广尾注
            string cleanedRootBody = DistillSkill.CleanCliSkillNoise(rawBody);
            if (string.IsNullOrWhiteSpace(cleanedRootBody))
            {
                cleanedRootBody = rawBody.Trim();
            }

            // 渐进式披露双轨构建：
            // - sbDistillBody: 供人格画像蒸馏（PersonaDistiller）与作战手册使用的精炼全域框架（控制在 ~12k 字符以内）
            // - sbAdviceGuideline: 供「回复建议」使用的极速专注聊天准则（仅包含核心公理 + 聊天回复子模块，控制在 ~7k 字符以内）
            var sbDistillBody = new StringBuilder(CapText(cleanedRootBody, 3500));
            var sbAdviceGuideline = new StringBuilder();
            sbAdviceGuideline.AppendLine($"【{name} · 核心实战准则】");
            sbAdviceGuideline.AppendLine(CapText(cleanedRootBody, 2200));

            try
            {
                string? baseDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string refDir = Path.Combine(baseDir, "references");
                    if (Directory.Exists(refDir))
                    {
                        // A. 同级 references/ 目录下的顶层 markdown 文件（如 guanxi-skills-main 等标准平铺参考库）
                        var refFiles = Directory.GetFiles(refDir, "*.md", SearchOption.TopDirectoryOnly)
                            .Where(f => !Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                                        !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => f)
                            .ToList();

                        if (refFiles.Count > 0)
                        {
                            var sbRefs = new StringBuilder();
                            sbRefs.AppendLine("\n\n### 【本技能配套实战策略与核心参考资料（References 知识库注入）】：");
                            int flatAdviceAdded = 0;

                            foreach (var rf in refFiles)
                            {
                                try
                                {
                                    string rfName = Path.GetFileNameWithoutExtension(rf);
                                    string rfContent = DistillSkill.CleanCliSkillNoise(File.ReadAllText(rf));
                                    if (!string.IsNullOrWhiteSpace(rfContent))
                                    {
                                        sbRefs.AppendLine($"\n#### [模块：{rfName}]");
                                        sbRefs.AppendLine(CapText(rfContent, 1800));

                                        bool isChatRelated = rfName.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                                                             rfName.Contains("reply", StringComparison.OrdinalIgnoreCase) ||
                                                             rfName.Contains("signal", StringComparison.OrdinalIgnoreCase) ||
                                                             rfName.Contains("stage", StringComparison.OrdinalIgnoreCase) ||
                                                             rfName.Contains("dialogue", StringComparison.OrdinalIgnoreCase) ||
                                                             rfName.Contains("话术", StringComparison.OrdinalIgnoreCase) ||
                                                             rfName.Contains("沟通", StringComparison.OrdinalIgnoreCase);
                                        if ((isChatRelated || refFiles.Count <= 2) && flatAdviceAdded < 2)
                                        {
                                            sbAdviceGuideline.AppendLine($"\n### 【实战参考：{rfName}】");
                                            sbAdviceGuideline.AppendLine(CapText(rfContent, 2400));
                                            flatAdviceAdded++;
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    AppLog.Warn("SkillStore", $"读取参考文件失败: {rf}", ex);
                                }
                            }

                            // 若新 Skill 的参考文件命名未包含 chat/话术 等字样，自动取前 2 个核心参考文件作为默认建议基线
                            if (flatAdviceAdded == 0)
                            {
                                foreach (var rf in refFiles.Take(2))
                                {
                                    try
                                    {
                                        string rfName = Path.GetFileNameWithoutExtension(rf);
                                        string rfContent = DistillSkill.CleanCliSkillNoise(File.ReadAllText(rf));
                                        if (!string.IsNullOrWhiteSpace(rfContent))
                                        {
                                            sbAdviceGuideline.AppendLine($"\n### 【实战参考：{rfName}】");
                                            sbAdviceGuideline.AppendLine(CapText(rfContent, 2400));
                                        }
                                    }
                                    catch
                                    {
                                        // 忽略单个文件异常
                                    }
                                }
                            }

                            sbDistillBody.Append(sbRefs);
                        }

                        // B. 通用概念词典 / 术语表自发现（支持任意包含“词典/概念/glossary/concept”的参考文档）
                        var glossaryFile = Directory.GetFiles(refDir, "*.md", SearchOption.AllDirectories)
                            .FirstOrDefault(f =>
                            {
                                string fn = Path.GetFileNameWithoutExtension(f);
                                return fn.Contains("词典", StringComparison.OrdinalIgnoreCase) ||
                                       fn.Contains("概念", StringComparison.OrdinalIgnoreCase) ||
                                       fn.Contains("glossary", StringComparison.OrdinalIgnoreCase) ||
                                       fn.Contains("concept", StringComparison.OrdinalIgnoreCase);
                            });

                        if (!string.IsNullOrEmpty(glossaryFile) && File.Exists(glossaryFile))
                        {
                            try
                            {
                                string dictText = DistillSkill.CleanCliSkillNoise(File.ReadAllText(glossaryFile));
                                if (!string.IsNullOrWhiteSpace(dictText))
                                {
                                    sbDistillBody.AppendLine("\n\n### 【配套高频概念词典与底层心智体系（核心摘要）】：");
                                    sbDistillBody.AppendLine(CapText(dictText, 2400));

                                    sbAdviceGuideline.AppendLine("\n### 【核心概念速查】：");
                                    sbAdviceGuideline.AppendLine(CapText(dictText, 1200));
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLog.Warn("SkillStore", $"读取概念词典失败: {glossaryFile}", ex);
                            }
                        }

                        // C. 检索 references/ 下的核心沟通/对话方法论文档（支持任意含 聊天/chat/话术/沟通/dialogue 的文档）
                        var chatPlaybooks = Directory.GetFiles(refDir, "*.md", SearchOption.AllDirectories)
                            .Where(f =>
                            {
                                string fn = Path.GetFileName(f);
                                if (fn.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) ||
                                    fn.Equals("README.md", StringComparison.OrdinalIgnoreCase)) return false;
                                if (refFiles.Contains(f)) return false;
                                return fn.Contains("聊天", StringComparison.OrdinalIgnoreCase) ||
                                       fn.Contains("chat", StringComparison.OrdinalIgnoreCase) ||
                                       fn.Contains("话术", StringComparison.OrdinalIgnoreCase) ||
                                       fn.Contains("沟通", StringComparison.OrdinalIgnoreCase) ||
                                       fn.Contains("dialogue", StringComparison.OrdinalIgnoreCase);
                            })
                            .Take(1)
                            .ToList();

                        foreach (var sp in chatPlaybooks)
                        {
                            try
                            {
                                string spName = Path.GetFileNameWithoutExtension(sp);
                                string spContent = DistillSkill.CleanCliSkillNoise(File.ReadAllText(sp));
                                if (!string.IsNullOrWhiteSpace(spContent))
                                {
                                    sbDistillBody.AppendLine($"\n\n### 【核心聊天方法论（{spName}）】：");
                                    sbDistillBody.AppendLine(CapText(spContent, 2200));

                                    sbAdviceGuideline.AppendLine($"\n### 【核心聊天方法论（{spName}）】：");
                                    sbAdviceGuideline.AppendLine(CapText(spContent, 2800));
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLog.Warn("SkillStore", $"读取子方法论失败: {sp}", ex);
                            }
                        }

                        // D. 复合工具箱型技能（如 tdskill 包含 tds-chat, tds-date 等子模块）
                        // 采用渐进式披露：
                        // 1) 回复建议（AdviceGuideline）仅定向注入 tds-chat 聊天诊断核心框架；
                        // 2) 画像蒸馏（SystemPrompt）仅注入各子模块的精要大纲摘要（每模块上限 600 字），彻底告别 246KB 全量拼接。
                        var subDirs = Directory.GetDirectories(refDir)
                            .OrderBy(d => Path.GetFileName(d))
                            .ToList();

                        if (subDirs.Count > 0)
                        {
                            var sbSubSkills = new StringBuilder();
                            sbSubSkills.AppendLine("\n\n### 【本技能配套全域实战专精体系（核心纲要）】：" );
                            foreach (var sDir in subDirs)
                            {
                                string sName = Path.GetFileName(sDir);
                                if (sName.StartsWith('.') ||
                                    sName.Equals("docs", StringComparison.OrdinalIgnoreCase) ||
                                    sName.Equals("知识库", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                string sFile = Path.Combine(sDir, "SKILL.md");
                                if (!File.Exists(sFile)) sFile = Path.Combine(sDir, "skill.md");
                                if (File.Exists(sFile))
                                {
                                    try
                                    {
                                        string subText = DistillSkill.CleanCliSkillNoise(File.ReadAllText(sFile));
                                        if (!string.IsNullOrWhiteSpace(subText))
                                        {
                                            sbSubSkills.AppendLine($"\n#### 【专项能力：{sName}】");
                                            sbSubSkills.AppendLine(CapText(subText, 600));

                                            if (sName.Contains("chat", StringComparison.OrdinalIgnoreCase))
                                            {
                                                sbAdviceGuideline.AppendLine($"\n### 【聊天实战专精（{sName}）】：");
                                                sbAdviceGuideline.AppendLine(CapText(subText, 2400));
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLog.Warn("SkillStore", $"读取子技能失败: {sFile}", ex);
                                    }
                                }
                            }
                            sbDistillBody.Append(sbSubSkills);
                        }
                    }

                    // E. 同级目录下的伴随深度方法论文档（如独立子技能目录下的 *.md）
                    var companionFiles = Directory.GetFiles(baseDir, "*.md", SearchOption.TopDirectoryOnly)
                        .Where(f => !Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                                    !Path.GetFileName(f).Equals("README.md", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f)
                        .ToList();

                    if (companionFiles.Count > 0)
                    {
                        var sbCompanions = new StringBuilder();
                        sbCompanions.AppendLine("\n\n### 【本技能配套深度方法论体系（Companion Playbook 注入）】：");
                        foreach (var cf in companionFiles)
                        {
                            try
                            {
                                string cfName = Path.GetFileNameWithoutExtension(cf);
                                string cfContent = DistillSkill.CleanCliSkillNoise(File.ReadAllText(cf));
                                if (!string.IsNullOrWhiteSpace(cfContent))
                                {
                                    sbCompanions.AppendLine($"\n#### [方法论文档：{cfName}]");
                                    sbCompanions.AppendLine(CapText(cfContent, 2400));

                                    sbAdviceGuideline.AppendLine($"\n### [方法论文档：{cfName}]");
                                    sbAdviceGuideline.AppendLine(CapText(cfContent, 2400));
                                }
                            }
                            catch (Exception ex)
                            {
                                AppLog.Warn("SkillStore", $"读取伴随方法论文档失败: {cf}", ex);
                            }
                        }
                        sbDistillBody.Append(sbCompanions);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("SkillStore", $"聚合技能参考资料异常: {filePath}", ex);
            }

            string dimsInstruction = dimensions.Count > 0
                ? $"（特别注意：建议提炼的核心维度包括：【{string.Join("/", dimensions)}】，每维度1行；评分给出10~100量化梯度分）\n"
                : "（特别注意：请精炼出 4~6 个能体现本方法论特色的专属维度，每维度1行；评分给出10~100量化梯度分）\n";

            string distillPrompt =
                $"你是基于【{name}】方法论的聊天与对话深度分析专家。\n" +
                $"请依据以下技能指导，分析目标联系人（[对方]）在聊天中所体现的行为模式与认知特质：\n\n" +
                $"{sbDistillBody}\n\n" +
                $"输出格式要求（每项一行，严格使用竖线 | 分隔，严禁将用户发言当成对方特质，证据必须100%摘录对方原话）：\n" +
                $"维度 | 特质描述 | 评分: 0到100的量化分 | 可信度: 0.0到1.0 | 证据: 对方原话引用\n" +
                dimsInstruction;

            string adviceGuideline = CapText(sbAdviceGuideline.ToString(), 8500);

            return new DistillSkill(
                Id: folderName.ToLowerInvariant(),
                Name: name,
                Description: desc,
                Icon: icon,
                SystemPrompt: distillPrompt,
                Dimensions: dimensions,
                IsBuiltIn: false,
                SourceFilePath: filePath,
                AdviceGuideline: adviceGuideline);
        }
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", $"解析技能文件失败: {filePath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 智能提取技能中声明或包含的核心评估维度（支持 YAML frontmatter 与 Markdown 正文分析）。
    /// </summary>
    private static readonly HashSet<string> DimensionBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "公众号", "兔子舞", "兔导", "无论回复", "无论", "强制", "规则", "注意", "提示", "警告",
        "第一步", "第二步", "第三步", "第1步", "第2步", "第3步", "步骤", "原则", "前提",
        "Step", "阶段", "说明", "简介", "目录", "概述", "模式", "输出", "要求", "背景",
        "目标", "格式", "作者", "声明", "微信", "关注", "建议", "核心哲学", "强制规则", "判断"
    };

    private static bool IsValidDimension(string dim)
    {
        if (string.IsNullOrWhiteSpace(dim)) return false;
        string cleaned = dim.Trim();
        if (cleaned.Length < 2 || cleaned.Length > 12) return false;
        if (cleaned.StartsWith("Step", StringComparison.OrdinalIgnoreCase)) return false;
        if (cleaned.StartsWith("第") && (cleaned.EndsWith("步") || cleaned.EndsWith("条") || cleaned.EndsWith("阶段"))) return false;
        if (DimensionBlacklist.Any(b => cleaned.Contains(b, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

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
                        if (IsValidDimension(cleaned)) dims.Add(cleaned);
                    }
                }
                else
                {
                    var itemMatches = Regex.Matches(block, @"^\s*-\s*([^\r\n]+)", RegexOptions.Multiline);
                    foreach (Match m in itemMatches)
                    {
                        string cleaned = Core.Ai.PersonaDistiller.CleanDimensionName(m.Groups[1].Value.Trim());
                        if (IsValidDimension(cleaned)) dims.Add(cleaned);
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
                        if (IsValidDimension(cleaned)) dims.Add(cleaned);
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
                    if (IsValidDimension(cleaned)) dims.Add(cleaned);
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
                    if (IsValidDimension(cand) && !dims.Contains(cand, StringComparer.OrdinalIgnoreCase))
                    {
                        dims.Add(cand);
                    }
                    if (dims.Count >= 8) break;
                }
            }

            // 模式 C: 从 Markdown 表格首列提取核心评估项（如 "| 报价博弈力 | 说明 |"）
            if (dims.Count == 0)
            {
                var tableMatches = Regex.Matches(body, @"^\s*\|\s*\*\*?([^|\*\r\n]{2,10})\*\*?\s*\|", RegexOptions.Multiline);
                foreach (Match m in tableMatches)
                {
                    string cand = Core.Ai.PersonaDistiller.CleanDimensionName(m.Groups[1].Value.Trim());
                    if (IsValidDimension(cand) &&
                        !cand.Contains("---") &&
                        !cand.Equals("维度", StringComparison.OrdinalIgnoreCase) &&
                        !cand.Equals("指标", StringComparison.OrdinalIgnoreCase) &&
                        !cand.Equals("模块", StringComparison.OrdinalIgnoreCase) &&
                        !cand.Equals("场景", StringComparison.OrdinalIgnoreCase) &&
                        !dims.Contains(cand, StringComparer.OrdinalIgnoreCase))
                    {
                        dims.Add(cand);
                    }
                    if (dims.Count >= 6) break;
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
                    if (!name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("skill.md", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    // 避免将复合技能 references/ 下的内部子模块（如 references/tds-chat）误注册为独立顶层技能
                    string? parentDir = Path.GetDirectoryName(f);
                    if (parentDir != null)
                    {
                        string relative = Path.GetRelativePath(folderPath, parentDir);
                        var parts = relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Any(p => p.Equals("references", StringComparison.OrdinalIgnoreCase)))
                        {
                            return false;
                        }
                    }

                    return true;
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
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", $"扫描导入文件夹失败: {folderPath}", ex);
        }

        _cachedAllSkills = null;
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

    /// <summary>获取当前选中的技能 ID（缺省为默认技能）。</summary>
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
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", "读取当前选中技能 ID 失败", ex);
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
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", "保存当前选中技能 ID 失败", ex);
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
        _cachedAllSkills = null;
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
                // 严禁物理删除工作区或 git 仓库 (.agents/skills) 中的源代码或包文件！
                string fullPath = Path.GetFullPath(targetSkill.SourceFilePath);
                bool isProtectedWorkspace = fullPath.Contains(Path.DirectorySeparatorChar + ".agent") ||
                                            fullPath.Contains(Path.DirectorySeparatorChar + ".git") ||
                                            fullPath.Contains(Path.AltDirectorySeparatorChar + ".agent");

                if (!isProtectedWorkspace && File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    removedAny = true;

                    // 若该技能所在目录仅包含该技能文件或为空，安全清理父目录
                    string? parentDir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                    {
                        var files = Directory.GetFileSystemEntries(parentDir);
                        if (files.Length == 0)
                        {
                            Directory.Delete(parentDir, recursive: true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("SkillStore", $"删除自定义技能物理文件失败: {targetSkill.SourceFilePath}", ex);
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

        _cachedAllSkills = null;
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
            if (list == null) return Array.Empty<DistillSkill>();

            // 自动过滤旧版本误导入的 references 内部子模块 (如 tds-approach, tds-attraction 等)
            var filtered = list.Where(s =>
                !s.Id.StartsWith("tds-", StringComparison.OrdinalIgnoreCase) &&
                !(s.SourceFilePath != null && (s.SourceFilePath.Contains("\\references\\", StringComparison.OrdinalIgnoreCase) || s.SourceFilePath.Contains("/references/", StringComparison.OrdinalIgnoreCase)))
            ).ToList();

            if (filtered.Count != list.Count)
            {
                // 自动纠偏更新磁盘文件
                try
                {
                    File.WriteAllText(_skillsFilePath, JsonSerializer.Serialize(filtered, s_jsonOptions));
                }
                catch (Exception ex)
                {
                    AppLog.Warn("SkillStore", "回写清理后的 custom_skills.json 失败", ex);
                }
            }

            return filtered;
        }
        catch (Exception ex)
        {
            AppLog.Warn("SkillStore", "读取 custom_skills.json 失败", ex);
            return Array.Empty<DistillSkill>();
        }
    }
}

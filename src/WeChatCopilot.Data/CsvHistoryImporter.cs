using System.Globalization;
using System.Text;
using WeChatCopilot.Core.Models;

namespace WeChatCopilot.Data;

/// <summary>
/// M5 冷链路兜底（FR-8）：CSV 历史导入器。TraceMemo 未运行/不兼容时的替代历史来源。
/// CSV 格式：role,text,ts（表头可选）；role 支持 incoming/outgoing/对方/我/me/self/other 等别名；
/// ts 为 ISO8601 可空；支持引号包裹字段（内含逗号/换行转义 ""）。
/// </summary>
public static class CsvHistoryImporter
{
    /// <summary>解析 CSV 文本为历史消息；坏行跳过不抛异常。</summary>
    public static IReadOnlyList<HistoryMessage> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return Array.Empty<HistoryMessage>();
        }

        var list = new List<HistoryMessage>();
        bool first = true;
        foreach (string rawLine in csv.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            List<string> fields = SplitCsvLine(line);
            if (first)
            {
                first = false;
                string head = fields[0].Trim();
                if (head is "role" or "direction" or "sender" or "角色")
                {
                    continue;  // 表头
                }
            }

            if (fields.Count < 2 || fields[1].Trim().Length == 0)
            {
                continue;
            }

            DateTime? ts = null;
            if (fields.Count >= 3 &&
                DateTime.TryParse(fields[2].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime parsed))
            {
                ts = parsed;
            }

            list.Add(new HistoryMessage(MapRole(fields[0].Trim()), fields[1].Trim(), ts));
        }

        return list;
    }

    /// <summary>收发方向别名映射；无法识别为 Unknown（蒸馏时按"未知"转写）。</summary>
    private static MessageRole MapRole(string role) => role.ToLowerInvariant() switch
    {
        "incoming" or "other" or "对方" or "接收" or "left" => MessageRole.Incoming,
        "outgoing" or "me" or "self" or "我" or "发送" or "right" => MessageRole.Outgoing,
        _ => MessageRole.Unknown
    };

    /// <summary>单行 CSV 字段切分：支持引号包裹与 "" 转义。</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return fields;
    }
}

using System.Text;

namespace WeChatCopilot.Core.Diagnostics;

/// <summary>
/// 轻量级本地诊断日志：按日写入 %AppData%/WeChatCopilot/logs/copilot-yyyyMMdd.log，
/// 任何 I/O 异常内部消化，绝不影响宿主业务流程。
/// </summary>
public static class AppLog
{
    private static readonly object s_sync = new();
    private static string s_logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WeChatCopilot",
        "logs");

    /// <summary>当前日志目录（支持测试重定向）。</summary>
    public static string LogDirectory
    {
        get => s_logDirectory;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                s_logDirectory = value;
            }
        }
    }

    public static void Info(string category, string message) =>
        Write("INFO", category, message, null);

    public static void Warn(string category, string message, Exception? ex = null) =>
        Write("WARN", category, message, ex);

    public static void Error(string category, string message, Exception? ex = null) =>
        Write("ERROR", category, message, ex);

    private static void Write(string level, string category, string message, Exception? ex)
    {
        try
        {
            DateTime now = DateTime.Now;
            string line = ex is null
                ? $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}{Environment.NewLine}"
                : $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}";

            lock (s_sync)
            {
                Directory.CreateDirectory(s_logDirectory);
                string filePath = Path.Combine(s_logDirectory, $"copilot-{now:yyyyMMdd}.log");
                File.AppendAllText(filePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志组件自身永不向外抛出异常
        }
    }
}

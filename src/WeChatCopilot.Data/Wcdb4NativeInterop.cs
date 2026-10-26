using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WeChatCopilot.Data;

/// <summary>
/// WCDB 4.0 原生动态库 (wcdb_api.dll / WCDB.dll / SDL2.dll) P/Invoke 进程级安全封装。
/// 遵循 TraceMemo wcdb4-client.ts 规范：
/// 1. 进程级单次执行 InitProtection + wcdb_init（避免重复调用返回 -1006）；
/// 2. 所有返回字符串指针的调用均在 finally 中执行 wcdb_free_string 释放非托管内存；
/// 3. 复用同一个已打开的账号句柄并通过互斥锁串行化保护底层 C++ 调用。
/// </summary>
public sealed class Wcdb4NativeInterop
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitProtectionDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string resourcePath);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbOpenAccountDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        out long handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbSetMyWxidDelegate(
        long handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string wxid);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WcdbFreeStringDelegate(IntPtr ptr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbGetSessionsDelegate(long handle, out IntPtr outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbGetDisplayNamesDelegate(
        long handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string usernamesJson,
        out IntPtr outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbGetMessagesDelegate(
        long handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string username,
        int limit,
        int offset,
        out IntPtr outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbExecQueryDelegate(
        long handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string kind,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        out IntPtr outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbOpenMessageCursorDelegate(
        long handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sessionId,
        int batchSize,
        int ascending,
        int beginTimestamp,
        int endTimestamp,
        out long outCursor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbFetchMessageBatchDelegate(
        long handle,
        long cursor,
        out IntPtr outJson,
        out int outHasMore);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WcdbCloseMessageCursorDelegate(long handle, long cursor);

    private static readonly object s_syncRoot = new();
    private static Wcdb4NativeInterop? s_instance;

    private IntPtr _libHandle;
    private InitProtectionDelegate? _initProtection;
    private WcdbInitDelegate? _wcdbInit;
    private WcdbShutdownDelegate? _wcdbShutdown;
    private WcdbOpenAccountDelegate? _wcdbOpenAccount;
    private WcdbSetMyWxidDelegate? _wcdbSetMyWxid;
    private WcdbFreeStringDelegate? _wcdbFreeString;
    private WcdbGetSessionsDelegate? _wcdbGetSessions;
    private WcdbGetDisplayNamesDelegate? _wcdbGetDisplayNames;
    private WcdbGetMessagesDelegate? _wcdbGetMessages;
    private WcdbExecQueryDelegate? _wcdbExecQuery;
    private WcdbOpenMessageCursorDelegate? _wcdbOpenMessageCursor;
    private WcdbFetchMessageBatchDelegate? _wcdbFetchMessageBatch;
    private WcdbCloseMessageCursorDelegate? _wcdbCloseMessageCursor;

    private long _activeHandle;
    private string _activeSessionDbPath = string.Empty;
    private string _activeKey = string.Empty;

    public string LoadedLibraryPath { get; }

    private Wcdb4NativeInterop(string libraryPath)
    {
        LoadedLibraryPath = libraryPath;
    }

    /// <summary>
    /// 获取或初始化进程级 WCDB4 原生桥接单例。
    /// </summary>
    public static bool TryGetOrCreate(string? customDllDir, out Wcdb4NativeInterop? interop, out string? error)
    {
        lock (s_syncRoot)
        {
            if (s_instance is not null)
            {
                interop = s_instance;
                error = null;
                return true;
            }

            if (!TryResolveWcdbLibraryPath(customDllDir, out string dllPath, out string? runtimeDir, out error))
            {
                interop = null;
                return false;
            }

            try
            {
                var inst = new Wcdb4NativeInterop(dllPath);
                inst.BootstrapNative(dllPath, runtimeDir);
                s_instance = inst;
                interop = inst;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                interop = null;
                error = "加载 WCDB 4.0 原生组件失败: " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// 自动探测 wcdb_api.dll 所在路径（支持配置路径、应用内置 resources、工作区目录及本机已安装的 TraceMemo 目录）。
    /// </summary>
    public static bool TryResolveWcdbLibraryPath(
        string? customDllDir,
        out string dllPath,
        out string? runtimeDir,
        out string? error)
    {
        dllPath = string.Empty;
        runtimeDir = null;

        var candidateDirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(customDllDir))
        {
            candidateDirs.Add(customDllDir.Trim());
        }

        string? envDllPath = Environment.GetEnvironmentVariable("WCDB_DLL_PATH");
        if (!string.IsNullOrWhiteSpace(envDllPath))
        {
            if (File.Exists(envDllPath))
            {
                dllPath = Path.GetFullPath(envDllPath);
                runtimeDir = FindRuntimeDirNear(Path.GetDirectoryName(dllPath)!);
                error = null;
                return true;
            }
            candidateDirs.Add(envDllPath.Trim());
        }

        // 1. 当前应用目录与上级开发目录
        string baseDir = AppContext.BaseDirectory;
        candidateDirs.Add(Path.Combine(baseDir, "resources", "wcdb", "win32", "x64"));
        candidateDirs.Add(Path.Combine(baseDir, "wcdb", "win32", "x64"));
        candidateDirs.Add(baseDir);

        string? dirCursor = baseDir;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(dirCursor); i++)
        {
            candidateDirs.Add(Path.Combine(dirCursor, "resources", "wcdb", "win32", "x64"));
            dirCursor = Path.GetDirectoryName(dirCursor.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        // 2. 本机已安装的 TraceMemo 资源目录（注册表 UninstallLocation 或常见安装路径）
        foreach (string tmInstallDir in DiscoverInstalledTraceMemoDirs())
        {
            candidateDirs.Add(Path.Combine(tmInstallDir, "resources", "resources", "wcdb", "win32", "x64"));
            candidateDirs.Add(Path.Combine(tmInstallDir, "resources", "wcdb", "win32", "x64"));
        }

        foreach (string dir in candidateDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            string candidateFile = File.Exists(dir) && dir.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? dir
                : Path.Combine(dir, "wcdb_api.dll");

            if (File.Exists(candidateFile))
            {
                dllPath = Path.GetFullPath(candidateFile);
                runtimeDir = FindRuntimeDirNear(Path.GetDirectoryName(dllPath)!);
                error = null;
                return true;
            }
        }

        error = "未找到 WCDB 4.0 原生库 (wcdb_api.dll)。";
        return false;
    }

    /// <summary>
    /// 确保指定账号的 session.db 已打开，返回句柄。
    /// </summary>
    public long EnsureAccountOpen(string sessionDbPath, string key, string wxid)
    {
        lock (s_syncRoot)
        {
            string normalizedDb = Path.GetFullPath(sessionDbPath);
            string normalizedKey = WeChatDbKeyAndRootProvider.NormalizeDatabaseKey(key);

            if (_activeHandle > 0 &&
                string.Equals(_activeSessionDbPath, normalizedDb, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_activeKey, normalizedKey, StringComparison.OrdinalIgnoreCase))
            {
                return _activeHandle;
            }

            if (_wcdbOpenAccount is null)
            {
                throw new InvalidOperationException("WCDB 4.0 wcdb_open_account 接口未就绪");
            }

            int rc = _wcdbOpenAccount(normalizedDb, normalizedKey, out long handle);
            if (rc != 0 || handle <= 0)
            {
                string hint = rc == -1005 ? "（密钥与当前微信账号数据库不匹配，请确认微信已登录目标账号）" : string.Empty;
                throw new InvalidOperationException($"wcdb_open_account 失败，错误码: {rc}{hint}");
            }

            _activeHandle = handle;
            _activeSessionDbPath = normalizedDb;
            _activeKey = normalizedKey;

            if (!string.IsNullOrWhiteSpace(wxid) && _wcdbSetMyWxid is not null)
            {
                try
                {
                    _wcdbSetMyWxid(handle, wxid);
                }
                catch
                {
                }
            }

            return handle;
        }
    }

    /// <summary>获取会话列表原始 JSON。</summary>
    public string GetSessionsJson(long handle)
    {
        lock (s_syncRoot)
        {
            if (_wcdbGetSessions is null)
            {
                return "[]";
            }

            IntPtr outJson = IntPtr.Zero;
            try
            {
                int rc = _wcdbGetSessions(handle, out outJson);
                if (rc != 0 || outJson == IntPtr.Zero)
                {
                    return "[]";
                }
                return Marshal.PtrToStringUTF8(outJson) ?? "[]";
            }
            finally
            {
                FreeNativeString(outJson);
            }
        }
    }

    /// <summary>获取指定用户名列表的显示名映射 JSON。</summary>
    public string? GetDisplayNamesJson(long handle, string usernamesJson)
    {
        lock (s_syncRoot)
        {
            if (_wcdbGetDisplayNames is null)
            {
                return null;
            }

            IntPtr outJson = IntPtr.Zero;
            try
            {
                int rc = _wcdbGetDisplayNames(handle, usernamesJson, out outJson);
                if (rc != 0 || outJson == IntPtr.Zero)
                {
                    return null;
                }
                return Marshal.PtrToStringUTF8(outJson);
            }
            finally
            {
                FreeNativeString(outJson);
            }
        }
    }

    /// <summary>执行自定义只读 SQL 查询（如查询 contact 表）。</summary>
    public string? ExecQueryJson(long handle, string kind, string dbPath, string sql)
    {
        lock (s_syncRoot)
        {
            if (_wcdbExecQuery is null)
            {
                return null;
            }

            IntPtr outJson = IntPtr.Zero;
            try
            {
                int rc = _wcdbExecQuery(handle, kind, dbPath, sql, out outJson);
                if (rc != 0 || outJson == IntPtr.Zero)
                {
                    return null;
                }
                return Marshal.PtrToStringUTF8(outJson);
            }
            finally
            {
                FreeNativeString(outJson);
            }
        }
    }

    /// <summary>
    /// 使用游标分批拉取指定联系人的消息 JSON 批次列表；若游标接口不可用则回退到 wcdb_get_messages。
    /// </summary>
    public List<string> FetchMessageBatchesJson(
        long handle,
        string username,
        int maxRows = 2000,
        int beginTimestamp = 0,
        int endTimestamp = 0)
    {
        lock (s_syncRoot)
        {
            var batches = new List<string>();

            if (_wcdbOpenMessageCursor is not null &&
                _wcdbFetchMessageBatch is not null &&
                _wcdbCloseMessageCursor is not null)
            {
                int batchSize = maxRows > 0 ? Math.Min(500, maxRows) : 1000;
                int ascending = maxRows > 0 ? 0 : 1;

                int openRc = _wcdbOpenMessageCursor(
                    handle,
                    username,
                    batchSize,
                    ascending,
                    beginTimestamp,
                    endTimestamp,
                    out long cursor);

                if (openRc == 0 && cursor > 0)
                {
                    int fetchedTotal = 0;
                    try
                    {
                        while (true)
                        {
                            IntPtr outJson = IntPtr.Zero;
                            int hasMore = 0;
                            try
                            {
                                int fetchRc = _wcdbFetchMessageBatch(handle, cursor, out outJson, out hasMore);
                                if (fetchRc != 0 || outJson == IntPtr.Zero)
                                {
                                    break;
                                }

                                string? json = Marshal.PtrToStringUTF8(outJson);
                                if (!string.IsNullOrWhiteSpace(json))
                                {
                                    batches.Add(json);
                                    fetchedTotal += batchSize;
                                }
                            }
                            finally
                            {
                                FreeNativeString(outJson);
                            }

                            if (hasMore == 0 || (maxRows > 0 && fetchedTotal >= maxRows))
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            _wcdbCloseMessageCursor(handle, cursor);
                        }
                        catch
                        {
                        }
                    }

                    if (batches.Count > 0)
                    {
                        return batches;
                    }
                }
            }

            // 回退到 wcdb_get_messages
            if (_wcdbGetMessages is not null)
            {
                int limit = maxRows > 0 ? Math.Min(1000, maxRows) : 1000;
                IntPtr outJson = IntPtr.Zero;
                try
                {
                    int rc = _wcdbGetMessages(handle, username, limit, 0, out outJson);
                    if (rc == 0 && outJson != IntPtr.Zero)
                    {
                        string? json = Marshal.PtrToStringUTF8(outJson);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            batches.Add(json);
                        }
                    }
                }
                finally
                {
                    FreeNativeString(outJson);
                }
            }

            return batches;
        }
    }

    private void BootstrapNative(string dllPath, string? runtimeDir)
    {
        string libDir = Path.GetDirectoryName(dllPath)!;

        // 1. 将 DLL 目录与 runtime 目录加入当前进程 PATH
        string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathParts = new List<string> { libDir };
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        {
            pathParts.Add(runtimeDir);
        }
        pathParts.Add(currentPath);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, pathParts));

        // 2. 预加载 VC++ 运行时与依赖库
        if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
        {
            foreach (string rtName in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll" })
            {
                string rtPath = Path.Combine(runtimeDir, rtName);
                if (File.Exists(rtPath))
                {
                    try { NativeLibrary.Load(rtPath); } catch { }
                }
            }
        }

        foreach (string depName in new[] { "SDL2.dll", "WCDB.dll" })
        {
            string depPath = Path.Combine(libDir, depName);
            if (File.Exists(depPath))
            {
                try { NativeLibrary.Load(depPath); } catch { }
            }
        }

        // 3. 加载 wcdb_api.dll
        _libHandle = NativeLibrary.Load(dllPath);

        _initProtection = GetRequiredExport<InitProtectionDelegate>("InitProtection");
        _wcdbInit = GetRequiredExport<WcdbInitDelegate>("wcdb_init");
        _wcdbOpenAccount = GetRequiredExport<WcdbOpenAccountDelegate>("wcdb_open_account");
        _wcdbFreeString = GetRequiredExport<WcdbFreeStringDelegate>("wcdb_free_string");
        _wcdbGetSessions = GetRequiredExport<WcdbGetSessionsDelegate>("wcdb_get_sessions");
        _wcdbGetMessages = GetRequiredExport<WcdbGetMessagesDelegate>("wcdb_get_messages");
        _wcdbGetDisplayNames = GetOptionalExport<WcdbGetDisplayNamesDelegate>("wcdb_get_display_names");
        _wcdbSetMyWxid = GetOptionalExport<WcdbSetMyWxidDelegate>("wcdb_set_my_wxid");
        _wcdbExecQuery = GetOptionalExport<WcdbExecQueryDelegate>("wcdb_exec_query");
        _wcdbOpenMessageCursor = GetOptionalExport<WcdbOpenMessageCursorDelegate>("wcdb_open_message_cursor");
        _wcdbFetchMessageBatch = GetOptionalExport<WcdbFetchMessageBatchDelegate>("wcdb_fetch_message_batch");
        _wcdbCloseMessageCursor = GetOptionalExport<WcdbCloseMessageCursorDelegate>("wcdb_close_message_cursor");
        _wcdbShutdown = GetOptionalExport<WcdbShutdownDelegate>("wcdb_shutdown");

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();

        // 4. 调用 InitProtection + wcdb_init（进程级单次）
        var resourceCandidates = new List<string>();

        // 优先选择包含 app.asar 的资源根目录（与 Electron process.resourcesPath 保持一致）
        foreach (string tmInstallDir in DiscoverInstalledTraceMemoDirs())
        {
            string tmRes = Path.Combine(tmInstallDir, "resources");
            if (Directory.Exists(tmRes))
            {
                resourceCandidates.Add(tmRes);
            }
        }

        string? cursorDir = libDir;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(cursorDir); i++)
        {
            if (File.Exists(Path.Combine(cursorDir, "app.asar")))
            {
                resourceCandidates.Insert(0, cursorDir);
            }
            else
            {
                resourceCandidates.Add(cursorDir);
            }
            cursorDir = Path.GetDirectoryName(cursorDir);
        }

        bool initProtectionOk = false;
        foreach (string resRoot in resourceCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (_initProtection(resRoot) == 0)
                {
                    initProtectionOk = true;
                    break;
                }
            }
            catch
            {
            }
        }

        if (initProtectionOk)
        {
            _wcdbInit();
        }
    }

    public void Shutdown()
    {
        lock (s_syncRoot)
        {
            if (_activeHandle > 0 && _wcdbShutdown is not null)
            {
                try
                {
                    _wcdbShutdown();
                }
                catch
                {
                }
                finally
                {
                    _activeHandle = 0;
                    _activeSessionDbPath = string.Empty;
                    _activeKey = string.Empty;
                }
            }
        }
    }

    private void FreeNativeString(IntPtr ptr)
    {
        if (ptr != IntPtr.Zero && _wcdbFreeString is not null)
        {
            try
            {
                _wcdbFreeString(ptr);
            }
            catch
            {
            }
        }
    }

    private T GetRequiredExport<T>(string name) where T : Delegate
    {
        IntPtr addr = NativeLibrary.GetExport(_libHandle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private T? GetOptionalExport<T>(string name) where T : Delegate
    {
        if (NativeLibrary.TryGetExport(_libHandle, name, out IntPtr addr) && addr != IntPtr.Zero)
        {
            return Marshal.GetDelegateForFunctionPointer<T>(addr);
        }
        return null;
    }

    private static string? FindRuntimeDirNear(string wcdbDllDir)
    {
        // wcdbDllDir 形如 .../resources/wcdb/win32/x64，向上找 .../resources/runtime/win32
        string? cursor = wcdbDllDir;
        for (int i = 0; i < 4 && !string.IsNullOrEmpty(cursor); i++)
        {
            string candidate = Path.Combine(cursor, "runtime", "win32");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            cursor = Path.GetDirectoryName(cursor);
        }
        return null;
    }

    private static IEnumerable<string> DiscoverInstalledTraceMemoDirs()
    {
        var dirs = new List<string>();

        // 1. 从注册表 Uninstall 项探测
        string[] regPaths =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var rootHive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string regPath in regPaths)
            {
                try
                {
                    using var key = rootHive.OpenSubKey(regPath);
                    if (key is null) continue;

                    foreach (string subName in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(subName);
                        string? displayName = sub?.GetValue("DisplayName") as string;
                        if (string.IsNullOrEmpty(displayName) ||
                            (!displayName.Contains("TraceMemo", StringComparison.OrdinalIgnoreCase) &&
                             !displayName.Contains("WechatExplorer", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        string? installLoc = sub?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc))
                        {
                            dirs.Add(installLoc);
                        }

                        string? uninstallStr = sub?.GetValue("UninstallString") as string;
                        if (!string.IsNullOrWhiteSpace(uninstallStr))
                        {
                            string cleaned = uninstallStr.Trim();
                            if (cleaned.StartsWith('"'))
                            {
                                int endQuote = cleaned.IndexOf('"', 1);
                                if (endQuote > 1)
                                {
                                    cleaned = cleaned[1..endQuote];
                                }
                            }
                            string? uninstDir = Path.GetDirectoryName(cleaned);
                            if (!string.IsNullOrEmpty(uninstDir) && Directory.Exists(uninstDir))
                            {
                                dirs.Add(uninstDir);
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        // 2. 常见安装路径兜底
        string localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TraceMemo");
        if (Directory.Exists(localPrograms))
        {
            dirs.Add(localPrograms);
        }

        for (char c = 'C'; c <= 'G'; c++)
        {
            string p1 = $"{c}:\\software\\TraceMemo";
            if (Directory.Exists(p1)) dirs.Add(p1);

            string p2 = $"{c}:\\Program Files\\TraceMemo";
            if (Directory.Exists(p2)) dirs.Add(p2);
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

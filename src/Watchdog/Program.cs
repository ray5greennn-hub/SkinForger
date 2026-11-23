using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const int PollMilliseconds = 1000;
    private const int StartupNoWindowGraceSeconds = 120;
    private const int NoWindowGraceSeconds = 30;
    private const int HungGraceSeconds = 30;
    private const int GracefulCloseWaitMilliseconds = 5000;
    private const int HeadlessExitWaitMilliseconds = 250;
    private const int ForceExitWaitMilliseconds = 3000;
    private const int IdentityUnavailableGraceSeconds = 10;
    private const int ResidualDiscoverySeconds = 5;
    private const int WerFaultGraceSeconds = 60;
    private const int OrphanNoWindowSeconds = 120;
    private const string Version = "2.0.0";
    private const uint WmClose = 0x0010;
    private const uint WmNull = 0x0000;
    private const uint SmtoBlock = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static string _logPath = string.Empty;
    // 2.0: only processes that belong to the game may be cleaned (see IsCleanupEligible).
    private static string _gameDirectory = string.Empty;
    private static int _gamePid;

    public static int Main(string[] args)
    {
        try
        {
            Dictionary<string, string> options = ParseArgs(args);
            _logPath = options.GetValueOrDefault("log", Path.Combine(AppContext.BaseDirectory, "musedash-watchdog.log"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);

            string mode = options.GetValueOrDefault("mode", string.Empty);
            if (mode.Equals("watch", StringComparison.OrdinalIgnoreCase))
                return Watch(options);
            if (mode.Equals("cleanup", StringComparison.OrdinalIgnoreCase))
                return Cleanup(options);
            if (mode.Equals("self-test", StringComparison.OrdinalIgnoreCase))
                return SelfTest();
            if (mode.Equals("predeploy", StringComparison.OrdinalIgnoreCase))
                return Predeploy(options);

            Log("watchdog_exit", "reason=invalid_mode");
            return 2;
        }
        catch (Exception ex)
        {
            SafeLog("watchdog_exit", "reason=unhandled_exception error=" + Quote(ex.GetType().Name + ": " + ex.Message));
            return 1;
        }
    }

    private static int Watch(Dictionary<string, string> options)
    {
        int pid = RequireInt(options, "pid");
        string executablePath = NormalizePath(Require(options, "exe"));
        string sessionId = Require(options, "session");
        long startTicks = options.TryGetValue("start-ticks", out string? ticksText) && long.TryParse(ticksText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTicks)
            ? parsedTicks
            : 0L;
        int windowsSessionId = RequireInt(options, "windows-session");
        if (startTicks <= 0L)
            throw new ArgumentException("Missing or invalid --start-ticks");

        var expected = new ProcessIdentity(pid, startTicks, executablePath, windowsSessionId, 0);
        var ownedChildren = new Dictionary<int, ProcessIdentity>();
        string sessionDirectory = Path.GetDirectoryName(Path.GetFullPath(_logPath))!;
        string ledgerDirectory = options.GetValueOrDefault("tool-ledger-directory", Path.Combine(sessionDirectory, "tool-sessions"));
        string registryPath = options.GetValueOrDefault("session-registry", Path.Combine(sessionDirectory, $"active-tool-session-{windowsSessionId}.json"));
        string mainPath = options.GetValueOrDefault("main", Path.Combine(Path.GetDirectoryName(executablePath)!, "Mods", "SkinForger", "SkinForger.Melon.dll"));
        Directory.CreateDirectory(ledgerDirectory);
        _gameDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        _gamePid = pid;
        // Holding a handle keeps the exit code readable after the game exits (crash classification).
        Process? tracked = null;
        try { tracked = Process.GetProcessById(pid); _ = tracked.Handle; } catch { tracked = null; }
        WriteSessionRegistry(registryPath, sessionId, "active", expected, ledgerDirectory, mainPath, "watchdog_started");
        Log("watchdog_started", $"version={Version} mode=watch pid={pid} session={Quote(sessionId)} windowsSession={windowsSessionId} exe={Quote(executablePath)} elevated={IsElevated()}");
        Log("watchdog_monitoring", $"pid={pid}");

        DateTime startedUtc = DateTime.UtcNow;
        DateTime? noWindowSince = null;
        DateTime? hungSince = null;
        bool everHadVisibleWindow = false;
        DateTime? identityUnavailableSince = null;

        while (true)
        {
            IdentityReadStatus status = TryGetIdentity(pid, out ProcessIdentity? identity, out string identityError);
            if (status == IdentityReadStatus.Exited)
            {
                ExitInfo exit = ReadExit(tracked);
                Log("process_exited", $"pid={pid} detail={Quote(identityError)} exitCode={exit.CodeText} kind={exit.Kind}");
                DiscoverOwnedChildren(expected, ownedChildren);
                DateTime discoveryDeadline = DateTime.UtcNow.AddSeconds(ResidualDiscoverySeconds);
                while (DateTime.UtcNow < discoveryDeadline)
                {
                    Thread.Sleep(PollMilliseconds);
                    DiscoverOwnedChildren(expected, ownedChildren);
                }
                CompleteSession(expected, sessionId, ownedChildren, ledgerDirectory, registryPath, mainPath, "tracked_process_exited", exit);
                Log("watchdog_exit", $"reason=tracked_process_exited ownedResidualCandidates={ownedChildren.Count}");
                return 0;
            }
            if (status == IdentityReadStatus.Inaccessible)
            {
                identityUnavailableSince ??= DateTime.UtcNow;
                LogOnce($"identity_unavailable:{pid}", "watchdog_monitoring", $"pid={pid} state=identity_unavailable detail={Quote(identityError)} action=bounded_retry");
                if (DateTime.UtcNow - identityUnavailableSince >= TimeSpan.FromSeconds(IdentityUnavailableGraceSeconds))
                {
                    WriteSessionRegistry(registryPath, sessionId, "monitoring_failed", expected, ledgerDirectory, mainPath, "identity_unavailable");
                    Log("watchdog_exit", $"reason=identity_unavailable pid={pid} action=no_cleanup");
                    return 3;
                }
                Thread.Sleep(PollMilliseconds);
                continue;
            }

            ProcessIdentity observed = identity!;
            identityUnavailableSince = null;
            if (!IdentityMatches(observed, expected, requireParent: false))
            {
                Log("watchdog_exit", $"reason=identity_changed pid={pid} observedStartTicks={observed.StartTimeUtcTicks} observedSession={observed.WindowsSessionId} observedExe={Quote(observed.ExecutablePath)} action=no_cleanup");
                return 0;
            }
            DiscoverOwnedChildren(expected, ownedChildren);

            WindowState windowState = GetWindowState(pid);
            if (windowState.VisibleWindows.Count == 0)
            {
                noWindowSince ??= DateTime.UtcNow;
                hungSince = null;
                TimeSpan grace = everHadVisibleWindow
                    ? TimeSpan.FromSeconds(NoWindowGraceSeconds)
                    : TimeSpan.FromSeconds(StartupNoWindowGraceSeconds);
                if (DateTime.UtcNow - noWindowSince >= grace)
                {
                    Log("abandoned_process_detected", $"reason=no_visible_window pid={pid} seconds={(DateTime.UtcNow - noWindowSince.Value).TotalSeconds:F1}");
                    if (!TerminateExact(expected, GetWindowState(pid), reason: "tracked_no_visible_window"))
                        return 4;
                    CompleteSession(expected, sessionId, ownedChildren, ledgerDirectory, registryPath, mainPath, "tracked_no_visible_window",
                        new ExitInfo("terminated", "watchdog", "无窗口超过时限，已由 Watchdog 结束"));
                    Log("watchdog_exit", "reason=abandoned_process_cleanup_complete");
                    return 0;
                }
            }
            else
            {
                everHadVisibleWindow = true;
                noWindowSince = null;
                bool responsive = windowState.VisibleWindows.Any(IsWindowResponsive);
                if (responsive)
                {
                    hungSince = null;
                }
                else
                {
                    hungSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - hungSince >= TimeSpan.FromSeconds(HungGraceSeconds))
                    {
                        Log("abandoned_process_detected", $"reason=hung_window pid={pid} windows={windowState.VisibleWindows.Count} seconds={(DateTime.UtcNow - hungSince.Value).TotalSeconds:F1}");
                        if (!TerminateExact(expected, windowState, reason: "tracked_hung_window"))
                            return 4;
                        CompleteSession(expected, sessionId, ownedChildren, ledgerDirectory, registryPath, mainPath, "tracked_hung_window",
                            new ExitInfo("terminated", "watchdog", "窗口无响应超过时限，已由 Watchdog 结束"));
                        Log("watchdog_exit", "reason=hung_process_cleanup_complete");
                        return 0;
                    }
                }
            }

            Thread.Sleep(PollMilliseconds);
        }
    }

    private static int Cleanup(Dictionary<string, string> options)
    {
        int pid = RequireInt(options, "pid");
        string executablePath = NormalizePath(Require(options, "exe"));
        long startTicks = long.Parse(Require(options, "start-ticks"), CultureInfo.InvariantCulture);
        int windowsSessionId = RequireInt(options, "windows-session");
        string reason = options.GetValueOrDefault("reason", "manual_cleanup");
        var expected = new ProcessIdentity(pid, startTicks, executablePath, windowsSessionId, 0);
        Log("watchdog_started", $"mode=cleanup pid={pid} startTicks={startTicks} windowsSession={windowsSessionId} exe={Quote(executablePath)} reason={Quote(reason)} elevated={IsElevated()}");
        IdentityReadStatus status = TryGetIdentity(pid, out ProcessIdentity? observed, out string error);
        if (!IsVerified(status, observed, expected, requireParent: false))
        {
            Log("watchdog_exit", $"reason=cleanup_identity_not_verified pid={pid} status={status} detail={Quote(error)} action=no_cleanup");
            return status == IdentityReadStatus.Exited ? 0 : 3;
        }
        TerminateExact(expected, GetWindowState(pid), reason);
        Log("watchdog_exit", "reason=cleanup_complete");
        return 0;
    }

    private static int SelfTest()
    {
        var expected = new ProcessIdentity(101, 123456, @"C:\test\MuseDash.exe", 7, 0);
        var exact = new ProcessIdentity(101, 123456, @"C:\test\MuseDash.exe", 7, 0);
        var reused = exact with { StartTimeUtcTicks = 123457 };
        var unrelated = new ProcessIdentity(102, 123500, @"C:\test\MuseDash.exe", 7, 999);
        var owned = unrelated with { ParentPid = expected.Pid };

        if (!IsVerified(IdentityReadStatus.Found, exact, expected, requireParent: false)) throw new InvalidOperationException("exact identity rejected");
        if (IsVerified(IdentityReadStatus.Inaccessible, exact, expected, requireParent: false)) throw new InvalidOperationException("inaccessible identity accepted");
        if (IsVerified(IdentityReadStatus.Exited, exact, expected, requireParent: false)) throw new InvalidOperationException("exited identity accepted");
        if (IsVerified(IdentityReadStatus.Found, reused, expected, requireParent: false)) throw new InvalidOperationException("PID reuse accepted");
        if (!IsOwnedDescendant(owned, expected)) throw new InvalidOperationException("owned descendant rejected");
        if (IsOwnedDescendant(unrelated with { StartTimeUtcTicks = expected.StartTimeUtcTicks - 1 }, expected))
            throw new InvalidOperationException("older unrelated process accepted");
        string game = @"C:\Games\Muse Dash";
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!IsCleanupEligible(@"C:\Games\Muse Dash\UnityCrashHandler64.exe", 5, game, 101)) throw new InvalidOperationException("crash handler rejected");
        if (!IsCleanupEligible(@"C:\Games\Muse Dash\MuseDash_Data\Plugins\ZFGameBrowser.exe", 5, game, 101)) throw new InvalidOperationException("game plugin rejected");
        if (!IsCleanupEligible(Path.Combine(sys, "WerFault.exe"), 101, game, 101)) throw new InvalidOperationException("game WerFault rejected");
        if (IsCleanupEligible(Path.Combine(sys, "WerFault.exe"), 999, game, 101)) throw new InvalidOperationException("foreign WerFault accepted");
        if (IsCleanupEligible(@"C:\Program Files (x86)\SogouInput\16.10.0.6009\SogouCloud.exe", 101, game, 101)) throw new InvalidOperationException("input method accepted");
        if (IsCleanupEligible(Path.Combine(sys, "conhost.exe"), 101, game, 101)) throw new InvalidOperationException("console host accepted");
        if (IsCleanupEligible(@"C:\Games\Muse Dash Other\x.exe", 101, game, 101)) throw new InvalidOperationException("sibling folder accepted");
        if (ReadExit(null).Kind != "unknown") throw new InvalidOperationException("exit classification");
        Console.WriteLine("PASS: watchdog identity, ownership and 2.0 cleanup-eligibility policy");
        return 0;
    }

    // ------------------------------------------------------------------ 2.0 additions

    private sealed record ExitInfo(string Kind, string CodeText, string Description);

    private sealed record LockHolder(int Pid, string Name, string ExecutablePath, int Threads, bool Cleaned, string Note);

    private static ExitInfo ReadExit(Process? tracked)
    {
        try
        {
            if (tracked == null || !tracked.HasExited) return new ExitInfo("unknown", "unknown", "无法读取退出代码");
            uint code = unchecked((uint)tracked.ExitCode);
            string text = "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
            return code switch
            {
                0 => new ExitInfo("normal", text, "正常退出"),
                0xC00000FD => new ExitInfo("crash", text, "崩溃：栈溢出（Stack Overflow）"),
                0xC0000005 => new ExitInfo("crash", text, "崩溃：内存访问冲突（Access Violation）"),
                0xC0000409 => new ExitInfo("crash", text, "崩溃：程序主动终止（Fail Fast / 栈缓冲区检查）"),
                0xC000001D => new ExitInfo("crash", text, "崩溃：非法指令"),
                0xE0434352 => new ExitInfo("crash", text, "崩溃：.NET 未处理异常"),
                0x80000003 => new ExitInfo("crash", text, "崩溃：断点异常"),
                _ when code >= 0xC0000000 => new ExitInfo("crash", text, "崩溃（Windows 异常代码 " + text + "）"),
                _ => new ExitInfo("abnormal", text, "非零退出代码 " + text + "（可能是被结束或游戏自身退出方式）")
            };
        }
        catch (Exception ex)
        {
            return new ExitInfo("unknown", "unknown", "无法读取退出代码：" + ex.GetType().Name);
        }
    }

    /// <summary>
    /// A descendant may be cleaned only if it belongs to the game: its executable lives in the game folder
    /// (UnityCrashHandler64, ZFGameBrowser, MuseDash itself), or it is the WerFault.exe the game started for its crash.
    /// Everything else (input methods, overlays, consoles) is left alone.
    /// </summary>
    private static bool IsCleanupEligible(string executablePath, int parentPid, string gameDirectory, int gamePid)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;
        string exe = NormalizePath(executablePath);
        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            string root = NormalizePath(gameDirectory) + Path.DirectorySeparatorChar;
            if (exe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        }
        string werFault = NormalizePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WerFault.exe"));
        return parentPid == gamePid && string.Equals(exe, werFault, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWerFault(ProcessIdentity identity) =>
        Path.GetFileName(identity.ExecutablePath).Equals("WerFault.exe", StringComparison.OrdinalIgnoreCase);

    private static void WaitForWerFault(IEnumerable<ProcessIdentity> candidates)
    {
        List<ProcessIdentity> reporters = candidates.Where(IsWerFault).ToList();
        if (reporters.Count == 0) return;
        Log("crash_report_wait", $"werFault={reporters.Count} graceSeconds={WerFaultGraceSeconds}");
        DateTime deadline = DateTime.UtcNow.AddSeconds(WerFaultGraceSeconds);
        while (DateTime.UtcNow < deadline &&
               reporters.Any(r => TryGetIdentity(r.Pid, out ProcessIdentity? o, out _) == IdentityReadStatus.Found && o != null && IdentityMatches(o, r, false)))
            Thread.Sleep(PollMilliseconds);
    }

    /// <summary>Files Muse Dash maps from the SkinForger folder (the running watchdog itself excluded).</summary>
    private static string[] ModFiles(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        string self = NormalizePath(Environment.ProcessPath ?? string.Empty);
        return Directory.EnumerateFiles(directory, "*.dll")
            .Concat(Directory.EnumerateFiles(directory, "*.exe"))
            .Where(path => !string.Equals(NormalizePath(path), self, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Asks Windows (Restart Manager) who holds the files, then cleans holders that are Muse Dash leftovers:
    /// same executable as the game, no visible window, and either already dead inside (0 threads, a crash snapshot)
    /// or windowless for a long time. A Muse Dash with a window (the game being played) is never touched.
    /// </summary>
    private static List<LockHolder> ReleaseLocks(IReadOnlyList<string> files, string gameExecutable, long sessionStart)
    {
        var result = new List<LockHolder>();
        if (!TryGetLockHolders(files, out List<(int Pid, long StartTicks, string AppName)> raw, out string rmError))
        {
            Log("lock_check_failed", $"detail={Quote(rmError)}");
            return result;
        }

        Dictionary<int, (int Threads, string ExeName)> table = SnapshotProcessTable();
        string gameExeName = Path.GetFileName(gameExecutable);
        foreach ((int pid, long startTicks, string appName) in raw)
        {
            if (pid == Environment.ProcessId) continue;
            table.TryGetValue(pid, out (int Threads, string ExeName) row);
            IdentityReadStatus status = TryGetIdentity(pid, out ProcessIdentity? identity, out string error);
            string exePath = identity?.ExecutablePath ?? string.Empty;
            bool sameGame = identity != null
                ? PathsEqual(exePath, gameExecutable)
                : string.Equals(row.ExeName, gameExeName, StringComparison.OrdinalIgnoreCase);
            bool hasWindow = GetWindowState(pid).VisibleWindows.Count > 0;
            bool zeroThreads = table.ContainsKey(pid) && row.Threads == 0;
            bool longWindowless = identity != null && !hasWindow &&
                DateTime.UtcNow.Ticks - identity.StartTimeUtcTicks > TimeSpan.FromSeconds(OrphanNoWindowSeconds).Ticks;

            string note;
            bool cleaned = false;
            if (!sameGame)
                note = "不是 Muse Dash，未处理";
            else if (hasWindow)
                note = "Muse Dash 正在运行（有窗口），未处理";
            else if (!zeroThreads && !longWindowless)
                note = "Muse Dash 刚启动或仍在退出，未处理";
            else
            {
                if (identity != null && status == IdentityReadStatus.Found)
                    cleaned = TerminateExact(identity, new WindowState(new List<IntPtr>()), zeroThreads ? "lock_holder_crash_snapshot" : "lock_holder_windowless");
                else
                {
                    // Crash snapshots often refuse identity queries. Restart Manager gave this exact PID and start time
                    // a moment ago and the snapshot table confirms the image name, so try the PID directly.
                    ForceTerminate(pid);
                    cleaned = WaitForExit(pid, ForceExitWaitMilliseconds);
                }
                note = cleaned
                    ? (zeroThreads ? "崩溃残留快照（0 线程），已清理" : "无窗口残留进程，已清理")
                    : (zeroThreads ? "崩溃残留快照（0 线程），无法结束：通常需要管理员权限或重启电脑" : "无窗口残留进程，无法结束（拒绝访问）");
            }

            var holder = new LockHolder(pid, string.IsNullOrEmpty(appName) ? row.ExeName : appName, exePath, table.ContainsKey(pid) ? row.Threads : -1, cleaned, note);
            result.Add(holder);
            Log("lock_holder", $"pid={pid} name={Quote(holder.Name)} exe={Quote(exePath)} threads={holder.Threads} window={hasWindow} rmStartTicks={startTicks} identity={status} cleaned={cleaned} note={Quote(note)}");
        }
        return result;
    }

    private static void WriteStatus(ExitInfo exit, int residuals, bool released, List<LockHolder> holders, string releaseDetail)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine("SkinForger Watchdog " + Version + " — 上一次 Muse Dash 会话");
            text.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            text.AppendLine("游戏退出：" + exit.Description + "（" + exit.CodeText + "）");
            text.AppendLine("检查过的游戏残留子进程：" + residuals.ToString(CultureInfo.InvariantCulture));
            text.AppendLine(released ? "Mods\\SkinForger 文件：已全部释放，可以正常部署。" : "Mods\\SkinForger 文件：仍被占用！");
            foreach (LockHolder holder in holders)
                text.AppendLine($"  占用者 PID {holder.Pid}  {holder.Name}  线程={holder.Threads}  → {holder.Note}");
            if (!released)
            {
                text.AppendLine("建议：");
                text.AppendLine("  1. 先什么都不做，等 1 分钟（崩溃报告可能还在写）。");
                text.AppendLine("  2. 仍被占用时，部署可以用“改名旧文件再复制新文件”的方式绕过。");
                text.AppendLine("  3. 0 线程的崩溃快照只有重启电脑才能彻底释放。");
            }
            text.AppendLine("详情：" + releaseDetail);
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_logPath))!, "watchdog-status.txt");
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
            Log("status_written", $"path={Quote(path)} exit={exit.Kind} released={released} holders={holders.Count}");
        }
        catch (Exception ex)
        {
            SafeLog("status_failed", "error=" + Quote(ex.GetType().Name + ": " + ex.Message));
        }
    }

    /// <summary>
    /// --mode predeploy --dir &lt;Mods\SkinForger&gt; [--game-exe &lt;MuseDash.exe&gt;]
    /// Run before replacing SkinForger files: reports who holds them and cleans Muse Dash leftovers.
    /// Exit 0 = everything released, 5 = still locked (details printed), 6 = the game is running.
    /// </summary>
    private static int Predeploy(Dictionary<string, string> options)
    {
        string directory = Path.GetFullPath(Require(options, "dir"));
        string gameExe = options.TryGetValue("game-exe", out string? g) ? NormalizePath(g)
            : NormalizePath(Path.Combine(directory, "..", "..", "MuseDash.exe"));
        _gameDirectory = Path.GetDirectoryName(gameExe) ?? string.Empty;
        string[] files = ModFiles(directory);
        Log("watchdog_started", $"version={Version} mode=predeploy dir={Quote(directory)} files={files.Length} gameExe={Quote(gameExe)} elevated={IsElevated()}");

        if (VerifyReleased(files, out string detail))
        {
            Console.WriteLine("RELEASED " + detail);
            Log("predeploy_result", "released=True " + detail);
            return 0;
        }

        List<LockHolder> holders = ReleaseLocks(files, gameExe, 0);
        foreach (LockHolder holder in holders)
            Console.WriteLine($"HOLDER pid={holder.Pid} name={holder.Name} threads={holder.Threads} cleaned={holder.Cleaned} note={holder.Note}");
        bool released = VerifyReleased(files, out detail);
        Console.WriteLine((released ? "RELEASED " : "LOCKED ") + detail);
        Log("predeploy_result", $"released={released} holders={holders.Count} {detail}");
        if (released) return 0;
        return holders.Any(h => h.Note.Contains("正在运行", StringComparison.Ordinal)) ? 6 : 5;
    }

    private static Dictionary<int, (int Threads, string ExeName)> SnapshotProcessTable()
    {
        var table = new Dictionary<int, (int, string)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return table;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return table;
            do
            {
                table[(int)entry.ProcessId] = ((int)entry.Threads, entry.ExecutableFile ?? string.Empty);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
            return table;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static bool TryGetLockHolders(IReadOnlyList<string> files, out List<(int Pid, long StartTicks, string AppName)> holders, out string error)
    {
        holders = new List<(int, long, string)>();
        error = string.Empty;
        if (files.Count == 0) return true;
        var key = new StringBuilder(64);
        int rc = RmStartSession(out uint session, 0, key);
        if (rc != 0) { error = "RmStartSession=" + rc; return false; }
        try
        {
            rc = RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, IntPtr.Zero, 0, null);
            if (rc != 0) { error = "RmRegisterResources=" + rc; return false; }
            uint needed = 0, count = 0, reasons = 0;
            rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == 0) return true;
            if (rc != 234) { error = "RmGetList=" + rc; return false; } // ERROR_MORE_DATA
            var info = new RmProcessInfo[needed];
            count = needed;
            rc = RmGetList(session, out needed, ref count, info, ref reasons);
            if (rc != 0) { error = "RmGetList(2)=" + rc; return false; }
            for (int i = 0; i < count; i++)
            {
                long ticks = DateTime.FromFileTimeUtc(((long)info[i].Process.ProcessStartTime.dwHighDateTime << 32) |
                                                      (uint)info[i].Process.ProcessStartTime.dwLowDateTime).Ticks;
                holders.Add((info[i].Process.dwProcessId, ticks, info[i].strAppName ?? string.Empty));
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint fileCount, string[] fileNames,
        uint applicationCount, IntPtr applications, uint serviceCount, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded, ref uint procInfo,
        [In, Out] RmProcessInfo[]? affectedApps, ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    // ------------------------------------------------------------------ v1

    private static bool IsVerified(IdentityReadStatus status, ProcessIdentity? observed, ProcessIdentity expected, bool requireParent) =>
        status == IdentityReadStatus.Found && observed != null && IdentityMatches(observed, expected, requireParent);

    private static void DiscoverOwnedChildren(ProcessIdentity parent, Dictionary<int, ProcessIdentity> ownedChildren)
    {
        Dictionary<int, int> parents = SnapshotParents();
        foreach (int pid in parents.Keys)
        {
            if (pid == parent.Pid || pid == Environment.ProcessId || !IsDescendantOf(pid, parent.Pid, parents))
                continue;
            try
            {
                if (TryGetIdentity(pid, out ProcessIdentity? identity, out string error) != IdentityReadStatus.Found || identity == null)
                {
                    LogOnce($"owned_child_probe_failed:{pid}", "watchdog_monitoring", $"pid={pid} state=owned_descendant_probe_failed error={Quote(error)}");
                    continue;
                }
                ProcessIdentity candidate = identity with { ParentPid = parents.GetValueOrDefault(pid, identity.ParentPid) };
                if (!IsOwnedDescendant(candidate, parent)) continue;
                // 2.0: the watchdog's own console and unrelated programs started under the game (input methods,
                // overlays) are never cleanup candidates. v1 terminated SogouCloud.exe / SGTool.exe on 2026-11-05.
                if (IsDescendantOf(pid, Environment.ProcessId, parents) ||
                    !IsCleanupEligible(candidate.ExecutablePath, candidate.ParentPid, _gameDirectory, parent.Pid))
                {
                    LogOnce($"ignored_child:{candidate.Pid}:{candidate.StartTimeUtcTicks}", "watchdog_monitoring",
                        $"pid={candidate.Pid} parentPid={candidate.ParentPid} state=unrelated_descendant_ignored exe={Quote(candidate.ExecutablePath)}");
                    continue;
                }
                ownedChildren[candidate.Pid] = candidate;
                LogOnce($"owned_child:{candidate.Pid}:{candidate.StartTimeUtcTicks}", "watchdog_monitoring", $"pid={candidate.Pid} parentPid={candidate.ParentPid} state=owned_descendant_observed startTicks={candidate.StartTimeUtcTicks} exe={Quote(candidate.ExecutablePath)}");
            }
            catch (Exception ex)
            {
                LogOnce($"owned_child_probe_failed:{pid}", "watchdog_monitoring", $"pid={pid} state=owned_descendant_probe_failed error={Quote(ex.GetType().Name + ": " + ex.Message)}");
            }
        }
    }

    private static bool IsDescendantOf(int pid, int rootPid, IReadOnlyDictionary<int, int> parents)
    {
        var visited = new HashSet<int>();
        int current = pid;
        for (int depth = 0; depth < 64 && parents.TryGetValue(current, out int parentPid); depth++)
        {
            if (parentPid == rootPid) return true;
            if (parentPid <= 0 || parentPid == current || !visited.Add(parentPid)) return false;
            current = parentPid;
        }
        return false;
    }

    private static void CleanupOwnedResiduals(ProcessIdentity parent, IEnumerable<ProcessIdentity> candidates)
    {
        foreach (ProcessIdentity candidate in candidates)
        {
            IdentityReadStatus status = TryGetIdentity(candidate.Pid, out ProcessIdentity? observed, out string error);
            if (status != IdentityReadStatus.Found || observed == null)
            {
                Log("process_exited", $"pid={candidate.Pid} relation=owned_child detail={Quote(error)}");
                continue;
            }
            observed = observed with { ParentPid = candidate.ParentPid };
            if (!IdentityMatches(observed, candidate, requireParent: true) || !IsOwnedDescendant(observed, parent))
            {
                Log("watchdog_monitoring", $"pid={candidate.Pid} state=owned_child_identity_changed action=skip");
                continue;
            }

            WindowState windows = GetWindowState(candidate.Pid);
            bool responsive = windows.VisibleWindows.Count > 0 && windows.VisibleWindows.Any(IsWindowResponsive);
            Log("abandoned_process_detected", $"pid={candidate.Pid} parentPid={parent.Pid} reason=tracked_process_exited visible={windows.VisibleWindows.Count > 0} responsive={responsive}");
            TerminateExact(candidate, windows, "owned_residual_after_parent_exit");
        }
    }

    private static void CompleteSession(
        ProcessIdentity owner,
        string sessionId,
        Dictionary<int, ProcessIdentity> ownedChildren,
        string ledgerDirectory,
        string registryPath,
        string mainPath,
        string reason,
        ExitInfo exit)
    {
        WriteSessionRegistry(registryPath, sessionId, "stopping", owner, ledgerDirectory, mainPath, reason);
        DateTime deadline = DateTime.UtcNow.AddSeconds(ResidualDiscoverySeconds);
        while (DateTime.UtcNow < deadline)
        {
            DiscoverOwnedChildren(owner, ownedChildren);
            Thread.Sleep(200);
        }

        // After a crash Windows Error Reporting (WerFault.exe -p <game pid>) may still be writing its report and
        // keeps a snapshot of the game, which holds every DLL. Give it time to finish before terminating it.
        WaitForWerFault(ownedChildren.Values);

        int toolRecords = CleanupSessionToolRecords(owner, sessionId, ledgerDirectory);
        CleanupOwnedResiduals(owner, ownedChildren.Values.OrderByDescending(candidate => candidate.StartTimeUtcTicks));
        string modDirectory = Path.GetDirectoryName(Path.GetFullPath(mainPath))!;
        string[] releaseTargets = ModFiles(modDirectory);
        bool mainReleased = VerifyReleased(releaseTargets, out string releaseDetail);
        List<LockHolder> holders = mainReleased ? new List<LockHolder>() : ReleaseLocks(releaseTargets, owner.ExecutablePath, sessionStart: owner.StartTimeUtcTicks);
        if (!mainReleased && holders.Count > 0 && holders.All(h => h.Cleaned))
            mainReleased = VerifyReleased(releaseTargets, out releaseDetail);
        WriteStatus(exit, ownedChildren.Count, mainReleased, holders, releaseDetail);
        WriteCleanupReport(registryPath, sessionId, reason, ownedChildren.Count, toolRecords, mainReleased, releaseDetail);
        WriteSessionRegistry(registryPath, sessionId, "completed", owner, ledgerDirectory, mainPath,
            reason + ";trackedDescendants=" + ownedChildren.Count.ToString(CultureInfo.InvariantCulture) +
            ";toolRecords=" + toolRecords.ToString(CultureInfo.InvariantCulture) + ";" + releaseDetail);
        Log("session_cleanup_complete", $"session={Quote(sessionId)} reason={Quote(reason)} trackedDescendants={ownedChildren.Count} toolRecords={toolRecords} mainReleased={mainReleased} detail={Quote(releaseDetail)}");
    }

    private static int CleanupSessionToolRecords(ProcessIdentity owner, string sessionId, string ledgerDirectory)
    {
        if (!Directory.Exists(ledgerDirectory)) return 0;
        int matched = 0;
        foreach (string path in Directory.EnumerateFiles(ledgerDirectory, "tool-*.json").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            ToolLedgerRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ToolLedgerRecord>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                Log("tool_cleanup_skipped", $"ledger={Quote(path)} reason=invalid_record error={Quote(ex.GetType().Name + ": " + ex.Message)}");
                continue;
            }
            if (record == null || record.SchemaVersion != 1 || !record.AllowExactResidualCleanup ||
                !string.Equals(record.OwnerSessionId, sessionId, StringComparison.Ordinal) || record.OwnerProcess == null)
                continue;
            if (!IdentityMatches(record.OwnerProcess, owner, requireParent: false))
            {
                Log("tool_cleanup_skipped", $"ledger={Quote(path)} reason=owner_identity_mismatch action=no_cleanup");
                continue;
            }
            if (!VerifySupervisorBinary(record, out string supervisorError))
            {
                Log("tool_cleanup_skipped", $"ledger={Quote(path)} reason=supervisor_identity_invalid detail={Quote(supervisorError)} action=no_cleanup");
                continue;
            }

            matched++;
            bool supervisorExited = CleanupLedgerIdentity(record.Supervisor, "session_tool_supervisor_exit");
            bool childExited = CleanupLedgerIdentity(record.Child, "session_tool_child_exit");
            bool targetsReleased = VerifyReleased(record.ReleaseTargets ?? Array.Empty<string>(), out string releaseDetail);
            record.State = supervisorExited && childExited && targetsReleased ? "watchdog_cleanup_completed" : "watchdog_cleanup_failed";
            record.CompletedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            record.Detail = $"owner_session_ended;supervisorExited={supervisorExited};childExited={childExited};{releaseDetail}";
            SaveToolRecord(path, record);
            Log("tool_cleanup_result", $"ledger={Quote(path)} module={Quote(record.OwnerModule)} supervisorExited={supervisorExited} childExited={childExited} targetsReleased={targetsReleased}");
        }
        return matched;
    }

    private static bool CleanupLedgerIdentity(ProcessIdentity expected, string reason)
    {
        IdentityReadStatus status = TryGetIdentity(expected.Pid, out ProcessIdentity? observed, out _);
        if (status == IdentityReadStatus.Exited) return true;
        if (status != IdentityReadStatus.Found || observed == null || !IdentityMatches(observed, expected, requireParent: false))
            return false;
        return TerminateExact(expected, GetWindowState(expected.Pid), reason);
    }

    private static bool VerifySupervisorBinary(ToolLedgerRecord record, out string error)
    {
        try
        {
            if (!Path.GetFileName(record.Supervisor.ExecutablePath).Equals("SkinForger_ToolSupervisor.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("supervisor executable name mismatch");
            if (!File.Exists(record.Supervisor.ExecutablePath)) throw new FileNotFoundException("supervisor executable missing");
            if (!string.Equals(HashFile(record.Supervisor.ExecutablePath), record.SupervisorSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("supervisor SHA256 mismatch");
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool VerifyReleased(string path, out string detail) => VerifyReleased(new[] { path }, out detail);

    private static bool VerifyReleased(IReadOnlyList<string> paths, out string detail)
    {
        foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!File.Exists(path))
            {
                detail = "release_target_missing=" + path;
                return false;
            }
            Exception? last = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    // 2.0: ReadWrite. A DLL mapped by a process (or a crash snapshot) still allows read-only opens,
                    // so v1's read check reported "released" for files that could not be replaced.
                    using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(100);
                }
            }
            if (last != null)
            {
                detail = "release_failed=" + path + ";error=" + last.GetType().Name + ":" + last.Message;
                return false;
            }
        }
        detail = "release_targets=" + paths.Count.ToString(CultureInfo.InvariantCulture) + ";exclusive=verified";
        return true;
    }

    private static void WriteSessionRegistry(string path, string sessionId, string state, ProcessIdentity owner,
        string ledgerDirectory, string mainPath, string detail)
    {
        var record = new ActiveToolSessionRecord
        {
            SchemaVersion = 1,
            SessionId = sessionId,
            State = state,
            Owner = owner,
            LedgerDirectory = Path.GetFullPath(ledgerDirectory),
            MainPath = Path.GetFullPath(mainPath),
            UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Detail = detail
        };
        SaveJsonAtomic(path, record);
    }

    private static void WriteCleanupReport(string registryPath, string sessionId, string reason, int trackedDescendants,
        int toolRecords, bool mainReleased, string releaseDetail)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(registryPath))!;
        string path = Path.Combine(directory, "process-cleanup-" + sessionId + ".json");
        SaveJsonAtomic(path, new
        {
            schemaVersion = 1,
            sessionId,
            completedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            reason,
            trackedDescendants,
            toolRecords,
            mainReleased,
            releaseDetail
        });
    }

    private static void SaveToolRecord(string path, ToolLedgerRecord record) => SaveJsonAtomic(path, record);

    private static void SaveJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool IsOwnedDescendant(ProcessIdentity candidate, ProcessIdentity parent) =>
        candidate.Pid != parent.Pid &&
        candidate.StartTimeUtcTicks >= parent.StartTimeUtcTicks &&
        candidate.WindowsSessionId == parent.WindowsSessionId;

    private static bool IdentityMatches(ProcessIdentity observed, ProcessIdentity expected, bool requireParent) =>
        observed.Pid == expected.Pid &&
        observed.StartTimeUtcTicks == expected.StartTimeUtcTicks &&
        observed.WindowsSessionId == expected.WindowsSessionId &&
        PathsEqual(observed.ExecutablePath, expected.ExecutablePath) &&
        (!requireParent || observed.ParentPid == expected.ParentPid);

    private static bool TerminateExact(ProcessIdentity expected, WindowState windows, string reason)
    {
        IdentityReadStatus status = TryGetIdentity(expected.Pid, out ProcessIdentity? observed, out string error);
        if (status != IdentityReadStatus.Found || observed == null || !IdentityMatches(observed, expected, requireParent: false))
        {
            Log("force_terminate_failed", $"pid={expected.Pid} stage=preflight status={status} detail={Quote(error)} action=no_cleanup");
            return false;
        }
        Log("abandoned_process_detected", $"pid={expected.Pid} reason={Quote(reason)} identity=verified");
        int gracefulWait = HeadlessExitWaitMilliseconds;
        if (windows.VisibleWindows.Count > 0)
        {
            TryGracefulClose(expected.Pid, windows.VisibleWindows);
            gracefulWait = GracefulCloseWaitMilliseconds;
        }
        if (WaitForExit(expected.Pid, gracefulWait))
        {
            Log("process_exited", $"pid={expected.Pid} after=graceful_wait");
            return true;
        }

        status = TryGetIdentity(expected.Pid, out observed, out error);
        if (status != IdentityReadStatus.Found || observed == null || !IdentityMatches(observed, expected, requireParent: false))
        {
            Log("force_terminate_failed", $"pid={expected.Pid} stage=final_identity status={status} detail={Quote(error)} action=no_cleanup");
            return false;
        }
        ForceTerminate(expected.Pid);
        if (WaitForExit(expected.Pid, ForceExitWaitMilliseconds))
        {
            Log("force_terminate_succeeded", $"pid={expected.Pid}");
            return true;
        }
        else
        {
            Log("force_terminate_failed", $"pid={expected.Pid} result=still_present");
            return false;
        }
    }

    private static void TryGracefulClose(int pid, List<IntPtr> windows)
    {
        Log("graceful_close_attempted", $"pid={pid} windows={windows.Count}");
        foreach (IntPtr window in windows)
            PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private static void ForceTerminate(int pid)
    {
        SetLastError(0);
        IntPtr handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, (uint)pid);
        int openError = Marshal.GetLastWin32Error();
        Log("force_terminate_attempted", $"pid={pid} openHandle=0x{handle.ToInt64():X} openError={openError}");
        if (handle == IntPtr.Zero)
        {
            Log("force_terminate_failed", $"pid={pid} stage=OpenProcess win32={openError} message={Quote(new Win32Exception(openError).Message)}");
            return;
        }

        try
        {
            SetLastError(0);
            bool terminated = TerminateProcess(handle, 1);
            int terminateError = Marshal.GetLastWin32Error();
            if (!terminated)
                Log("force_terminate_failed", $"pid={pid} stage=TerminateProcess win32={terminateError} message={Quote(new Win32Exception(terminateError).Message)}");
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void ProbeTerminateAccess(int pid)
    {
        SetLastError(0);
        IntPtr handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, (uint)pid);
        int openError = Marshal.GetLastWin32Error();
        if (handle == IntPtr.Zero)
            LogOnce($"terminate_probe_failed:{pid}", "force_terminate_failed", $"pid={pid} stage=OpenProcessProbe desired=terminate_query win32={openError} message={Quote(new Win32Exception(openError).Message)}");
        else
        {
            LogOnce($"terminate_probe_ok:{pid}", "force_terminate_attempted", $"pid={pid} stage=OpenProcessProbe desired=terminate_query openHandle=0x{handle.ToInt64():X}");
            CloseHandle(handle);
        }
    }

    private static bool WaitForExit(int pid, int milliseconds)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.WaitForExit(milliseconds);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static IdentityReadStatus TryGetIdentity(int pid, out ProcessIdentity? identity, out string error)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                identity = null;
                error = "process has exited";
                return IdentityReadStatus.Exited;
            }

            string moduleError = string.Empty;
            string? executablePath = TryQueryFullProcessImageName(pid, out string queryPath, out string queryError)
                ? queryPath
                : TryReadMainModulePath(process, out string modulePath, out moduleError)
                    ? modulePath
                    : null;
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new InvalidOperationException("Executable path is unavailable. MainModule=" + moduleError + "; QueryFullProcessImageName=" + queryError);

            long startTicks = TryReadStartTicks(process, out string startError);
            if (startTicks <= 0L)
                throw new InvalidOperationException("Start time is unavailable: " + startError);
            int windowsSessionId = process.SessionId;
            int parentPid = SnapshotParents().GetValueOrDefault(pid, 0);
            identity = new ProcessIdentity(pid, startTicks, NormalizePath(executablePath), windowsSessionId, parentPid);
            error = string.Empty;
            return IdentityReadStatus.Found;
        }
        catch (ArgumentException ex)
        {
            identity = null;
            error = ex.GetType().Name + ": " + ex.Message;
            return IdentityReadStatus.Exited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            identity = null;
            error = ex.GetType().Name + ": " + ex.Message;
            return IdentityReadStatus.Inaccessible;
        }
    }

    private static Dictionary<int, int> SnapshotParents()
    {
        var parents = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return parents;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return parents;
            do
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));
            return parents;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static bool TryReadMainModulePath(Process process, out string path, out string error)
    {
        try
        {
            path = process.MainModule?.FileName ?? string.Empty;
            error = string.Empty;
            return !string.IsNullOrWhiteSpace(path);
        }
        catch (Exception ex)
        {
            path = string.Empty;
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static long TryReadStartTicks(Process process, out string error)
    {
        try
        {
            error = string.Empty;
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return 0L;
        }
    }

    private static bool TryQueryFullProcessImageName(int pid, out string path, out string error)
    {
        path = string.Empty;
        SetLastError(0);
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        int openError = Marshal.GetLastWin32Error();
        if (handle == IntPtr.Zero)
        {
            error = "OpenProcess win32=" + openError + " message=" + new Win32Exception(openError).Message;
            return false;
        }

        try
        {
            var buffer = new char[32768];
            uint size = (uint)buffer.Length;
            SetLastError(0);
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                int queryError = Marshal.GetLastWin32Error();
                error = "QueryFullProcessImageName win32=" + queryError + " message=" + new Win32Exception(queryError).Message;
                return false;
            }

            path = new string(buffer, 0, (int)size);
            error = string.Empty;
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static WindowState GetWindowState(int pid)
    {
        var windows = new List<IntPtr>();
        EnumWindowsProc callback = (window, _) =>
        {
            GetWindowThreadProcessId(window, out uint windowPid);
            if (windowPid == (uint)pid && IsWindowVisible(window))
                windows.Add(window);
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return new WindowState(windows);
    }

    private static bool IsWindowResponsive(IntPtr window)
    {
        SetLastError(0);
        IntPtr result;
        return SendMessageTimeout(window, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung | SmtoBlock, 2000, out result) != IntPtr.Zero;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
            string key = arg[2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                parsed[key] = "true";
            else
                parsed[key] = args[++i];
        }
        return parsed;
    }

    private static string Require(Dictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Missing --" + key);
        return value;
    }

    private static int RequireInt(Dictionary<string, string> options, string key)
    {
        string value = Require(options, key);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
            throw new ArgumentException("Invalid --" + key + ": " + value);
        return result;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void LogOnce(string key, string eventName, string details)
    {
        if (Logged.Add(key)) Log(eventName, details);
    }

    private static void Log(string eventName, string details)
    {
        string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + eventName + " " + details;
        File.AppendAllText(_logPath, line + Environment.NewLine);
    }

    private static void SafeLog(string eventName, string details)
    {
        try
        {
            if (string.IsNullOrEmpty(_logPath))
                _logPath = Path.Combine(AppContext.BaseDirectory, "musedash-watchdog.log");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);
            Log(eventName, details);
        }
        catch
        {
            // Last ditch logging must not keep the watchdog alive.
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private sealed class ActiveToolSessionRecord
    {
        public int SchemaVersion { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public ProcessIdentity Owner { get; set; } = new(0, 0, string.Empty, 0, 0);
        public string LedgerDirectory { get; set; } = string.Empty;
        public string MainPath { get; set; } = string.Empty;
        public string UpdatedUtc { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;
    }

    private sealed class ToolLedgerRecord
    {
        public int SchemaVersion { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string? CompletedUtc { get; set; }
        public string Detail { get; set; } = string.Empty;
        public ProcessIdentity Supervisor { get; set; } = new(0, 0, string.Empty, 0, 0);
        public string SupervisorSha256 { get; set; } = string.Empty;
        public ProcessIdentity Child { get; set; } = new(0, 0, string.Empty, 0, 0);
        public string[]? ReleaseTargets { get; set; }
        public bool AllowExactResidualCleanup { get; set; }
        public string OwnerSessionId { get; set; } = string.Empty;
        public string OwnerModule { get; set; } = string.Empty;
        public ProcessIdentity? OwnerProcess { get; set; }
    }

    private enum IdentityReadStatus
    {
        Found,
        Exited,
        Inaccessible
    }

    private sealed record ProcessIdentity(int Pid, long StartTimeUtcTicks, string ExecutablePath, int WindowsSessionId, int ParentPid);

    private sealed record WindowState(List<IntPtr> VisibleWindows);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr processHandle, uint flags, char[] executableName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint errorCode);
}

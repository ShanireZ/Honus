using System.Diagnostics;

namespace Horus.Agent.Hardening;

/// M5 保活·看门狗(supervisor)。三层纵深的**层2**:反复确保采集 agent 子进程存活,异常退出即按退避重启。
/// 最强的**层1**由 Windows 服务承载(见 <see cref="WindowsService"/>);**层3**是服务器心跳告警(采集被关即看板暴露)。
/// 单例:命名 Mutex 防多看门狗并存。子进程被结束(非 0 退出)→ 重启;正常退出(0,如 Ctrl+C)→ 不重启同退。
public static class Watchdog
{
    /// 以 supervisor 身份运行。selfExe=本 exe;childArgs=传给采集模式的参数(通常是 config 路径);
    /// adoptPid≥0 时先监控**已存在**的采集进程(层2 互拉:看门狗被杀后由 agent 重新拉起并 adopt 现有 agent)。
    /// serviceSession=true(层1 服务模式):经 <see cref="SessionLauncher"/> 把采集拉进用户交互会话(session 0 截不到屏);
    /// false(层2 用户会话看门狗):普通 Process.Start。
    public static int RunSupervisor(string selfExe, string[] childArgs, int adoptPid, string instanceKey, bool serviceSession, CancellationToken ct)
    {
        using var single = new Mutex(initiallyOwned: false, "Global\\Horus_Watchdog_" + Sanitize(instanceKey), out bool _);
        bool held;
        try { held = single.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }   // 上个看门狗崩溃遗留 → 接管
        if (!held) { Console.WriteLine("[horus-watchdog] 已有看门狗在运行,本实例退出。"); return 0; }

        try
        {
            // adopt:先守着已存在的采集进程,等它退出再进入拉起循环。
            if (adoptPid > 0 && TryGetProcess(adoptPid) is Process existing)
            {
                Console.WriteLine($"[horus-watchdog] adopt 现有采集 pid={adoptPid}");
                WaitForExit(existing, ct);
                if (ct.IsCancellationRequested) return 0;
            }

            int backoffMs = 1000;
            while (!ct.IsCancellationRequested)
            {
                DateTime startedAt = DateTimeOffset.UtcNow.UtcDateTime;
                Process? child = LaunchChild(selfExe, childArgs, serviceSession);
                if (child is null)
                {
                    if (!Sleep(backoffMs, ct)) break;
                    backoffMs = Math.Min(backoffMs * 2, 30000);
                    continue;
                }
                Console.WriteLine($"[horus-watchdog] 采集子进程已启动 pid={child.Id}");
                WaitForExit(child, ct);

                if (ct.IsCancellationRequested) { TryKill(child); break; }
                int code = SafeExitCode(child);
                if (code == 0) { Console.WriteLine("[horus-watchdog] 采集正常退出,看门狗同退。"); break; }

                // 运行足够久(>60s)视为一次独立崩溃 → 重置退避,避免偶发重启把退避累积到 30s。
                if ((DateTimeOffset.UtcNow.UtcDateTime - startedAt).TotalSeconds > 60) backoffMs = 1000;
                Console.Error.WriteLine($"[horus-watchdog] 采集异常退出 code={code},{backoffMs}ms 后重启");
                if (!Sleep(backoffMs, ct)) break;
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }
        }
        finally { try { single.ReleaseMutex(); } catch { /* 未持有忽略 */ } }
        return 0;
    }

    /// 采集端(agent 模式)守护:监控看门狗 pid,若看门狗被杀 → 拉起新看门狗(adopt 本 agent),继续守新看门狗。层2 互拉。
    public static async Task GuardWatchdogAsync(int watchdogPid, string selfExe, string[] childArgs, string instanceKey, CancellationToken ct)
    {
        int current = watchdogPid;
        while (!ct.IsCancellationRequested)
        {
            Process? wd = TryGetProcess(current);
            if (wd is null) { current = RelaunchAdoptWatchdog(selfExe, childArgs, instanceKey); if (current <= 0) return; }
            else
            {
                try { await wd.WaitForExitAsync(ct); }
                catch (OperationCanceledException) { return; }
                if (ct.IsCancellationRequested) return;
                Console.Error.WriteLine("[horus-agent] 看门狗已退出,尝试重新拉起(adopt 本进程)。");
                current = RelaunchAdoptWatchdog(selfExe, childArgs, instanceKey);
                if (current <= 0) return;
            }
        }
    }

    private static int RelaunchAdoptWatchdog(string selfExe, string[] childArgs, string instanceKey)
    {
        try
        {
            var psi = new ProcessStartInfo(selfExe) { UseShellExecute = false };
            psi.ArgumentList.Add("--watchdog");
            psi.ArgumentList.Add("--adopt");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (string a in childArgs) psi.ArgumentList.Add(a);
            Process? p = Process.Start(psi);
            return p?.Id ?? -1;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-agent] 重拉看门狗失败: {ex.Message}"); return -1; }
    }

    private static Process? LaunchChild(string selfExe, string[] childArgs, bool serviceSession)
    {
        // 层1 服务模式:CreateProcessAsUser 拉进用户会话(截屏必需);失败回退普通启动(在用户会话跑的看门狗仍正确)。
        if (serviceSession && OperatingSystem.IsWindows())
        {
            int? pid = SessionLauncher.StartInActiveSession(selfExe, childArgs);
            if (pid is not null) { try { return Process.GetProcessById(pid.Value); } catch { return null; } }
            Console.Error.WriteLine("[horus-watchdog] 会话内启动失败,回退普通启动(可能截不到交互桌面)。");
        }
        try
        {
            var psi = new ProcessStartInfo(selfExe) { UseShellExecute = false };
            foreach (string a in childArgs) psi.ArgumentList.Add(a);
            psi.Environment["HORUS_WATCHDOG_PID"] = Environment.ProcessId.ToString();   // 子进程据此守护看门狗(层2 互拉)
            return Process.Start(psi);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-watchdog] 启动采集失败: {ex.Message}"); return null; }
    }

    private static void WaitForExit(Process p, CancellationToken ct)
    {
        try { p.WaitForExit((int)Math.Min(int.MaxValue, 1000)); while (!p.HasExited && !ct.IsCancellationRequested) p.WaitForExit(1000); }
        catch { /* 进程句柄异常按已退出处理 */ }
    }

    private static Process? TryGetProcess(int pid)
    {
        try { Process p = Process.GetProcessById(pid); return p.HasExited ? null : p; }
        catch { return null; }
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }
    private static void TryKill(Process p) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ignore */ } }

    /// 可取消的 sleep;返回 false 表示已取消(应退出循环)。
    private static bool Sleep(int ms, CancellationToken ct) => !ct.WaitHandle.WaitOne(ms);

    private static string Sanitize(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }
}

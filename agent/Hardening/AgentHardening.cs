using System.Security.Principal;

namespace Horus.Agent.Hardening;

/// M5 采集端硬化·Windows 专属助手(重启取证标记 + 管理员自检)。纯逻辑判定在 agentcore(RestartClassifier 等·已单测),
/// 此处只做文件 IO 与 Win32 身份查询(真机验收)。
public static class RestartMarker
{
    /// 读旧标记(无文件→null)后写 "running"。返回旧标记供 <see cref="RestartClassifier.IsUnexpectedRestart"/> 判定。
    /// 语义:上次正常退出会写 "clean";若启动读到仍是 "running" → 上次被结束/崩溃/断电(异常终止)。
    public static string? ReadThenMarkRunning(string path)
    {
        string? prev = null;
        try { if (File.Exists(path)) prev = File.ReadAllText(path); } catch { /* 读失败按无标记处理 */ }
        try { File.WriteAllText(path, RestartClassifier.Running); } catch { /* 写失败不致命 */ }
        return prev;
    }

    /// 正常退出时写 "clean"(下次启动据此判定非异常重启)。
    public static void MarkClean(string path)
    {
        try { File.WriteAllText(path, RestartClassifier.Clean); } catch { /* 写失败不致命 */ }
    }
}

/// 当前进程是否管理员(采集需管理员跑 ETW / UIAutomation / WMI;非管理员=能力降级)。
public static class WinPrivilege
{
    public static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

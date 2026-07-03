using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Horus.Agent.Hardening;

/// M5 保活层1:以 **Windows 服务(LocalSystem)** 承载看门狗 —— 学员标准账户杀不掉 LocalSystem 服务,
/// 服务被停 / 采集被关都靠**心跳断→看板告警**兜底(层3)。服务体 = <see cref="Watchdog.RunSupervisor"/>(serviceSession=true·
/// 经 SessionLauncher 把采集拉进用户交互会话)。安装/卸载走 sc.exe(免额外 SCM P/Invoke)。**真机验收**。
[SupportedOSPlatform("windows")]
public static class WindowsService
{
    public const string ServiceName = "HorusAgentWatchdog";

    /// 以服务身份运行(由 SCM 启动;也可控制台直接跑调试)。configArgs=传给采集模式的参数(config 路径)。
    public static void Run(string selfExe, string[] configArgs, string examSeatKey)
    {
        IHost host = Host.CreateDefaultBuilder()
            .UseWindowsService(o => o.ServiceName = ServiceName)
            .ConfigureServices(s => s.AddHostedService(_ => new WatchdogHostedService(selfExe, configArgs, examSeatKey)))
            .Build();
        host.Run();
    }

    /// 注册服务:binPath = "<exe> --service <configArgs>"。start=auto·LocalSystem。返回退出码。
    public static int Install(string selfExe, string[] configArgs)
    {
        string bin = "\"" + selfExe + "\" --service" + string.Concat(configArgs.Select(a => " \"" + a + "\""));
        int rc = Sc($"create {ServiceName} binPath= \"{bin.Replace("\"", "\\\"")}\" start= auto obj= LocalSystem DisplayName= \"Horus 监考采集看门狗\"");
        if (rc == 0) { Sc($"description {ServiceName} \"Horus 监考:看门狗保活采集端(纯检测·不阻断)\""); Sc($"start {ServiceName}"); }
        return rc;
    }

    /// 卸载服务:先停再删。
    public static int Uninstall()
    {
        Sc($"stop {ServiceName}");
        return Sc($"delete {ServiceName}");
    }

    private static int Sc(string args)
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false });
            if (p is null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[horus-service] sc {args} 失败: {ex.Message}"); return -1; }
    }
}

/// 服务托管的后台任务:跑看门狗 supervisor(serviceSession=true)。SCM 停止 → stoppingToken 取消 → 看门狗退出。
[SupportedOSPlatform("windows")]
internal sealed class WatchdogHostedService(string selfExe, string[] configArgs, string examSeatKey) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => Watchdog.RunSupervisor(selfExe, configArgs, adoptPid: -1, examSeatKey, serviceSession: true, stoppingToken), stoppingToken);
}

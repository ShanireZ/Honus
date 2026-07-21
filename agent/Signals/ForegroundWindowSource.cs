using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Horus.Agent.Model;
using Horus.Contracts;

namespace Horus.Agent.Signals;

/// 前台窗口标题 + 进程名。**事件驱动**(SetWinEventHook EVENT_SYSTEM_FOREGROUND),无轮询、零周期开销:
/// 仅在前台窗口切换时触发回调,标题/进程在回调里读取并上报。相比每秒 Timer 轮询,CPU 与延迟都更优(第二节待办闭环)。
public sealed class ForegroundWindowSource : ISignalSource
{
    public string Name => "foreground-window";
    public event Action<RawSignal>? Signal;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int WM_QUIT = 0x0012;

    private readonly WinEventDelegate _hookDelegate;   // 必须持有引用,否则被 GC 回收后回调失效
    private IntPtr _hook;
    private int _threadId;
    private Thread? _thread;

    public ForegroundWindowSource() => _hookDelegate = OnWinEvent;

    public void Start()
    {
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            Application.Run();   // 消息泵:WinEventProc 在本 STA 线程调用
            if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        })
        { IsBackground = true, Name = "horus-foreground" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        int tid = _threadId;
        if (tid != 0) PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);   // 退出消息泵(仅 STA 线程可安全退出)
    }

    public void Dispose()
    {
        Stop();
        try { _thread?.Join(TimeSpan.FromSeconds(1)); } catch { /* 超时忽略 */ }
    }

    private void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;   // 只关心真实前台窗口对象
        Signal?.Invoke(new RawSignal(SignalType.WindowFocus, new()
        {
            ["title"] = GetTitle(hwnd),
            ["process"] = GetProcessName(hwnd),
            ["hwnd"] = hwnd.ToInt64(),
        }));
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len == 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return string.Empty; }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventDelegate lpfn, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern int GetCurrentThreadId();
}

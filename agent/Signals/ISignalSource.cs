using Honus.Agent.Model;

namespace Honus.Agent.Signals;

/// 信号源统一接口。各源在后台采集,经 Signal 事件吐出 RawSignal。
public interface ISignalSource : IDisposable
{
    string Name { get; }
    event Action<RawSignal>? Signal;
    void Start();
    void Stop();
}

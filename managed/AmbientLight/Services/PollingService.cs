// PollingService.cs — 250ms 轮询 AC_GetStatus (API 文档: 无回调、UI 2-4Hz 轮询)
using System.Windows.Threading;
using AmbientLight.Interop;

namespace AmbientLight.Services;

public sealed class PollingService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private int _lastDeviceChanged = -1;
    private int _lastState = -1;
    private bool _disposed;

    /// <summary>每次轮询 (250ms) 触发, 携带最新状态快照</summary>
    public event Action<ACStatus>? Polled;

    /// <summary>deviceChanged 计数变化 (设备热插拔), UI 此时才重调 AC_GetDevices</summary>
    public event Action? DevicesChanged;

    /// <summary>state 变化 (old, new)</summary>
    public event Action<int, int>? StateChanged;

    public PollingService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        ACStatus st;
        try
        {
            st = AmbientCore.GetStatus();
        }
        catch
        {
            return;   // DLL 缺失等致命问题由 Start 路径报错, 轮询不抛
        }

        if (_lastDeviceChanged >= 0 && st.deviceChanged != (uint)_lastDeviceChanged)
            DevicesChanged?.Invoke();
        _lastDeviceChanged = (int)st.deviceChanged;

        if (_lastState >= 0 && st.state != _lastState)
            StateChanged?.Invoke(_lastState, st.state);
        _lastState = st.state;

        Polled?.Invoke(st);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}

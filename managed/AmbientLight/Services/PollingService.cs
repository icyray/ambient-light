// PollingService.cs — 250ms 轮询 AC_GetStatusEx (API 文档: 无回调、UI 2-4Hz 轮询)
// M3: 每个活动 slot 各轮询一次 (SlotPolled); "Polled" 事件携带当前选中设备的
// 状态 (无活动会话时兜底 slot 0 = STOPPED), 供主页/设置页沿用单设备语义。
using System.Windows.Threading;
using AmbientLight.Interop;

namespace AmbientLight.Services;

public sealed class PollingService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private int _lastDeviceChanged = -1;
    private int _lastState = -1;
    private bool _disposed;

    /// <summary>每次轮询 (250ms) 触发, 携带选中设备的最新状态快照</summary>
    public event Action<ACStatus>? Polled;

    /// <summary>每个活动 slot 的状态 (M3 多设备卡片)</summary>
    public event Action<int, ACStatus>? SlotPolled;

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
        ACStatus selected;
        try
        {
            var app = App.Instance;
            var slots = app.Slots.Keys.OrderBy(k => k).ToList();
            selected = default;
            bool haveSelected = false;
            if (slots.Count > 0)
            {
                foreach (var slot in slots)
                {
                    var st = AmbientCore.GetStatusEx(slot);
                    SlotPolled?.Invoke(slot, st);
                    if (app.KeyOf(slot) == ConfigStore.SelectedKey)
                    {
                        selected = st;
                        haveSelected = true;
                    }
                    if (_lastDeviceChanged >= 0 && st.deviceChanged != (uint)_lastDeviceChanged)
                        DevicesChanged?.Invoke();
                    _lastDeviceChanged = (int)st.deviceChanged;
                }
                if (!haveSelected) selected = AmbientCore.GetStatusEx(slots[0]);
            }
            else
            {
                // 全部停止: 未启动 slot 返回 STOPPED/未知 (热插拔计数此时也冻结)
                selected = AmbientCore.GetStatusEx(0);
            }
        }
        catch
        {
            return;   // DLL 缺失等致命问题由 Start 路径报错, 轮询不抛
        }

        if (_lastState >= 0 && selected.state != _lastState)
            StateChanged?.Invoke(_lastState, selected.state);
        _lastState = selected.state;

        Polled?.Invoke(selected);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }
}

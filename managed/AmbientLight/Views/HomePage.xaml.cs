// HomePage.xaml.cs — 仪表盘: 总开关 / 指标 / 灯带预览(LedRingView) / 亮度 / 模式 / 设备
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AmbientLight.Controls;
using AmbientLight.Interop;
using AmbientLight.Services;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class HomePage : Page
{
    private bool _loaded;                       // 初值阶段忽略一切配置下发
    private int _lastPolledState = -1;
    private int _lastRingSig = -1;              // (mode|leds|sides|dir) 变化才重配预览
    private readonly Dictionary<int, Wpf.Ui.Controls.Button> _modeBtns = new();

    public HomePage()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);      // 滚轮滚动由外层滚动器承担
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Instance.Poller.Polled += OnPolled;
        App.Instance.Poller.DevicesChanged += OnDevicesChanged;

        BuildModeGrid();
        var dev = ConfigStore.Device;
        BrightnessSlider.Value = dev.Brightness * 100.0;    // 先设初值 (事件未挂)
        BrightnessValueText.Text = $"{(int)(dev.Brightness * 100)}%";
        BrightnessSlider.ValueChanged += OnBrightnessChanged;
        _loaded = true;

        _ = RefreshDevicesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Instance.Poller.Polled -= OnPolled;
        App.Instance.Poller.DevicesChanged -= OnDevicesChanged;
        _lastPolledState = -1;
    }

    // ---- 模式宫格 --------------------------------------------------------
    private void BuildModeGrid()
    {
        ModeGrid.Children.Clear();
        _modeBtns.Clear();
        foreach (var (index, name, kind) in AmbientCore.Modes)
        {
            var btn = new Wpf.Ui.Controls.Button
            {
                Content = new StackPanel
                {
                    Margin = new Thickness(4, 6, 4, 6),
                    Children =
                    {
                        new System.Windows.Controls.TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new System.Windows.Controls.TextBlock { Text = kind, FontSize = 10, Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
                Margin = new Thickness(0, 0, 6, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = index != 1,             // 拾音律动 M3 实现
            };
            btn.Click += (_, _) => SetMode(index);
            ModeGrid.Children.Add(btn);
            _modeBtns[index] = btn;
        }
        RefreshModeButtons();
    }

    private void RefreshModeButtons()
    {
        int cur = ConfigStore.Device.ModeIndex;
        foreach (var (index, _, _) in AmbientCore.Modes)
        {
            if (!_modeBtns.TryGetValue(index, out var btn)) continue;
            btn.Appearance = index == cur ? ControlAppearance.Primary : ControlAppearance.Secondary;
        }
    }

    private void SetMode(int mode)
    {
        if (!_loaded || ConfigStore.Device.ModeIndex == mode) return;
        ConfigStore.Device.ModeIndex = mode;
        RefreshModeButtons();
        App.Instance.ApplyConfig();             // 模式切换立即生效
        UpdateRingHint(ConfigStore.Device.ModeIndex);
    }

    // ---- 状态轮询 ----------------------------------------------------
    private unsafe void OnPolled(ACStatus st)
    {
        if (st.state != _lastPolledState)
        {
            _lastPolledState = st.state;
            RefreshMonitors();   // 拓扑切换 (Win+P/热插拔) 后信息面板跟随
        }
        StateText.Text = st.state switch
        {
            AmbientCore.StateStopped => "已停止",
            AmbientCore.StateStarting => "启动中…",
            AmbientCore.StateRunning => ModeNameWithState(ConfigStore.Device.ModeIndex),
            AmbientCore.StateWaitDisp => "等待画面 (锁屏/切换)",
            AmbientCore.StateDevLost => "设备未连接",
            AmbientCore.StateError => "错误",
            _ => $"未知 ({st.state})",
        };
        bool on = st.state is AmbientCore.StateRunning or AmbientCore.StateWaitDisp
                              or AmbientCore.StateStarting;
        StateText.Foreground = st.state switch
        {
            AmbientCore.StateRunning => System.Windows.Media.Brushes.LightGreen,
            AmbientCore.StateError => System.Windows.Media.Brushes.IndianRed,
            AmbientCore.StateDevLost => System.Windows.Media.Brushes.Orange,
            _ => System.Windows.Media.Brushes.White,
        };
        ToggleBtn.Content = on ? "关闭" : "开启";

        PortText.Text = st.state == AmbientCore.StateStopped && !App.Instance.CoreRunning
            ? "未连接设备"
            : $"端口 {st.ComPort} · 设备类型 {DevTypeName(st.devType)} · 供电 {((st.ledStatusByte & 1) == 1 ? "外接" : "USB")}";

        FpsText.Text = st.fps.ToString("F1");
        FramesText.Text = st.frameCounter.ToString("N0");
        ProcText.Text = $"{st.procMs:F2} ms";
        WireText.Text = $"{st.wireMs:F2} ms";

        var warns = new List<string>();
        if (st.monitorFallback == 1)
            warns.Add($"配置的显示器不可用, 已回退主屏 (margin {{底,右,顶,左}} = {st.margins[0]},{st.margins[1]},{st.margins[2]},{st.margins[3]})");
        if (st.allBlackFrames > 30)
            warns.Add($"连续 {st.allBlackFrames} 帧全黑 (独占全屏或 MPO?), 画面可能不准");
        if (warns.Count > 0)
        {
            WarnText.Text = string.Join("\n", warns);
            WarnText.Visibility = Visibility.Visible;
        }
        else
        {
            WarnText.Visibility = Visibility.Collapsed;
        }

        string diag = AmbientCore.LastDiag();
        if (!string.IsNullOrEmpty(diag) && st.state != AmbientCore.StateRunning)
        {
            DiagText.Text = diag;
            DiagText.Visibility = Visibility.Visible;
        }
        else
        {
            DiagText.Visibility = Visibility.Collapsed;
        }

        UpdateRing(st);
    }

    private void UpdateRing(ACStatus st)
    {
        var dev = ConfigStore.Device;
        int sig = dev.ModeIndex | (dev.LedCount << 5) | (dev.Sides << 15) | (dev.Direction << 18) | ((int)st.state << 20);
        if (sig != _lastRingSig)
        {
            _lastRingSig = sig;
            Ring.Configure(dev.LedCount, dev.Sides, dev.Direction);
            UpdateRingHint(dev.ModeIndex);
            ModeTagText.Text = $"{AmbientCore.ModeName(dev.ModeIndex)} · {dev.LedCount} 灯";
        }
        if (st.state == AmbientCore.StateRunning && AmbientCore.IsHostFrameMode(dev.ModeIndex))
        {
            Ring.Update(st.CopyLedRgb(), dev.LedCount);
        }
        else if (st.state == AmbientCore.StateStopped)
        {
            Ring.Clear();
        }
        // WAIT_DISP/DEV_LOST: 保留最后画面 (串口策略帧/重连语义与之一致)
    }

    private void UpdateRingHint(int mode)
    {
        if (AmbientCore.IsMcuMode(mode))
            Ring.SetHint("MCU 特效由灯带固件渲染, 主机无帧数据可预览");
        else if (mode == 11)
            Ring.SetHint("纯色模式: 显示最近下发的纯色帧");
        else
            Ring.SetHint("实时显示每颗 LED 最近下发的颜色");
    }

    private static string ModeNameWithState(int mode) => mode switch
    {
        0 => "追光运行中",
        11 => "纯色运行中",
        _ => $"{AmbientCore.ModeName(mode)} 运行中",
    };

    private static string DevTypeName(int t) => t switch
    {
        AmbientCore.DevScreen => "屏幕灯带",
        AmbientCore.DevFan => "ARGB 风扇",
        AmbientCore.DevDesk => "桌面灯条",
        AmbientCore.DevSound => "立柱氛围灯",
        _ => "未知",
    };

    private void OnDevicesChanged() => _ = RefreshDevicesAsync();

    // ---- 总开关 --------------------------------------------------------
    private void OnToggle(object sender, RoutedEventArgs e) => App.Instance.ToggleCore();

    // ---- 亮度 (节流下发: 首次立即生效, 拖动中每 40ms 一次, 收尾补发最终值) ----
    // 轨道点击跳转/跟手拖动由 SliderJump 附加行为接管 (与设置页滑条一致)
    private const double BrightnessThrottleMs = 40;
    private DateTime _lastBrightnessApply = DateTime.MinValue;
    private DispatcherTimer? _brightnessFlushTimer;

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;                   // 构造/初值阶段不触发热更与落盘
        if (BrightnessValueText != null)
            BrightnessValueText.Text = $"{(int)e.NewValue}%";
        var due = _lastBrightnessApply.AddMilliseconds(BrightnessThrottleMs);
        if (DateTime.UtcNow >= due)
        {
            ApplyBrightnessNow();
        }
        else
        {
            if (_brightnessFlushTimer == null)
            {
                _brightnessFlushTimer = new DispatcherTimer();
                _brightnessFlushTimer.Tick += (_, _) => ApplyBrightnessNow();
            }
            _brightnessFlushTimer.Interval = due - DateTime.UtcNow;
            _brightnessFlushTimer.Stop();
            _brightnessFlushTimer.Start();
        }
    }

    private void ApplyBrightnessNow()
    {
        _brightnessFlushTimer?.Stop();
        _lastBrightnessApply = DateTime.UtcNow;
        ConfigStore.Device.Brightness = BrightnessSlider.Value / 100.0;
        App.Instance.ApplyConfig();
    }

    // ---- 设备 / 显示器 -------------------------------------------------
    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    private void OnRefreshMonitorsClick(object sender, RoutedEventArgs e) => RefreshMonitors();

    private void RefreshMonitors()
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            MonitorsText.Text = mons.Length == 0
                ? "未枚举到显示器 "
                : string.Join("\n", mons.Select((m, i) =>
                    $"[{i}] {m.Name}  {m.width}x{m.height}{(m.primary != 0 ? "  (主屏)" : "")}  —  {m.Adapter}"));
        }
        catch (Exception ex)
        {
            MonitorsText.Text = $"枚举失败: {ex.Message}";
        }
    }

    private async Task RefreshDevicesAsync()
    {
        if (DevicesText == null) return;
        DevicesText.Text = "探测中… (打开串口握手, ~0.2s/台)";
        try
        {
            var devices = await Task.Run(AmbientCore.GetDevices).ConfigureAwait(true);
            DevicesText.Text = devices.Length == 0
                ? "未发现灯带 (VID_2E3C & PID_5740); 检查 USB 连接, 并确认 Ansnake 未占用端口"
                : string.Join("\n", devices.Select(d =>
                    $"{d.ComPort}  {DevTypeName(d.devType)}  id={d.devId}" +
                    (d.inUse == 1 ? "  [端口被占用]" : "")));
        }
        catch (Exception ex)
        {
            DevicesText.Text = $"探测失败: {ex.Message}";
        }
    }
}

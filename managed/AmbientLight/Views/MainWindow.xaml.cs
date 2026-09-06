// MainWindow.xaml.cs — M1 骨架: 总开关 / 亮度热更新 / 测试图 / 状态轮询展示
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AmbientLight.Interop;
using AmbientLight.Services;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class MainWindow : FluentWindow
{
    private bool _allowClose;
    private int _activePattern;                 // 0=无 1=四象限 2=洋红
    private int _lastPolledState = -1;

    public bool AllowClose { get => _allowClose; set => _allowClose = value; }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        App.Instance.Poller.Polled += OnPolled;
        App.Instance.Poller.DevicesChanged += OnDevicesChanged;
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;                    // 关窗 = 收进托盘, 退出走托盘菜单
            Hide();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var dev = ConfigStore.Device;
        BrightnessSlider.Value = dev.Brightness * 100.0;    // 先设初值 (事件未挂)
        BrightnessValueText.Text = $"{(int)(dev.Brightness * 100)}%";
        BrightnessSlider.ValueChanged += OnBrightnessChanged;
        _loaded = true;

        ConfigDirText.Text = $"{ConfigStore.ConfigDir}  ({ConfigStore.ConfigDirSource})";
        RefreshMonitors();
        _ = RefreshDevicesAsync();
    }

    // ---- 状态轮询 ----------------------------------------------------
    private unsafe void OnPolled(ACStatus st)
    {
        if (st.state != _lastPolledState)
        {
            _lastPolledState = st.state;
            RefreshMonitors();   // 拓扑切换 (Win+P/热插拔) 后信息面板跟随; 显示器选择 UI 见 M2
        }
        StateText.Text = st.state switch
        {
            AmbientCore.StateStopped => "已停止",
            AmbientCore.StateStarting => "启动中…",
            AmbientCore.StateRunning => "追光运行中",
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
    }

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
    private const double BrightnessThrottleMs = 40;
    private DateTime _lastBrightnessApply = DateTime.MinValue;
    private DispatcherTimer? _brightnessFlushTimer;
    private bool _trackDragActive;              // 轨道点击拖动中 (鼠标已被 Slider 捕获)
    private bool _loaded;                       // OnLoaded 前忽略一切配置下发

    // "指哪打哪": 按下轨道 → 跳到点击点并捕获鼠标; 移动 → 持续跟随; 松开 → 释放。
    // 点在滑块上则不接管, 交给原生 Thumb 拖动。
    private void BrightnessSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            sender is not Slider s ||
            s.Template?.FindName("PART_Track", s) is not System.Windows.Controls.Primitives.Track t ||
            t.Thumb == null || t.Thumb.IsMouseOver)
            return;

        _trackDragActive = true;
        s.CaptureMouse();
        JumpTrackValue(t, e);
        e.Handled = true;                       // 吞掉事件, 轨道 RepeatButton 不再参与
    }

    private void BrightnessSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_trackDragActive) return;
        if (sender is Slider s &&
            s.Template?.FindName("PART_Track", s) is System.Windows.Controls.Primitives.Track t)
            JumpTrackValue(t, e);
    }

    private void BrightnessSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _trackDragActive = false;
        if (sender is Slider s && s.IsMouseCaptured)
            s.ReleaseMouseCapture();
    }

    private void BrightnessSlider_LostMouseCapture(object? sender, MouseEventArgs e)
        => _trackDragActive = false;            // 捕获意外丢失 (如 alt-tab) 时终止拖动

    private void JumpTrackValue(System.Windows.Controls.Primitives.Track t, MouseEventArgs e)
    {
        // 不用 Track.ValueFromPoint: 它是 Track.Value+delta 的增量式计算, 依赖最近一次
        // arrange 缓存的 ThumbCenterOffset/Density, 在 WPF-UI 模板下与实际几何不一致
        // (实测点击轨道正中会得到 0)。这里用纯几何自算: 滑块中心对准点击点。
        var pt = e.GetPosition(t);
        double w = t.Thumb?.ActualWidth ?? 0.0;
        double len = t.ActualWidth;
        if (len <= 0) return;
        double ratio = (pt.X - w / 2.0) / Math.Max(1.0, len - w);
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        BrightnessSlider.Value = BrightnessSlider.Minimum +
                                 ratio * (BrightnessSlider.Maximum - BrightnessSlider.Minimum);
    }

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

    // ---- 测试图 --------------------------------------------------------
    private void OnPatternQuad(object sender, RoutedEventArgs e) => SetPattern(1);

    private void OnPatternMagenta(object sender, RoutedEventArgs e) => SetPattern(2);

    private void SetPattern(int mode)
    {
        int next = _activePattern == mode ? 0 : mode;
        AmbientCore.SetTestPattern(next);
        _activePattern = next;
        PatternQuadBtn.Appearance = next == 1 ? ControlAppearance.Primary : ControlAppearance.Secondary;
        PatternMagentaBtn.Appearance = next == 2 ? ControlAppearance.Primary : ControlAppearance.Secondary;
    }

    // ---- 设备 / 显示器 -------------------------------------------------
    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    private void RefreshMonitors()
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            MonitorsText.Text = mons.Length == 0
                ? "未枚举到显示器"
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

    // ---- 配置目录 --------------------------------------------------------
    private void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigStore.ConfigDir}\""));
        }
        catch { /* 忽略 */ }
    }
}

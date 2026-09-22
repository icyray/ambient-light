// CalibrateWindow.xaml.cs — 全屏四色校准窗 (M4, 厂商 FormCalibrate 同构)
// 打开时临时下发 captureRange=2 / colorCut=0 / modeIndex=0 (不落盘, 关闭恢复),
// 取色带收窄到屏幕边缘使中央面板不参与取色; 灯带以追光模式采集本屏 → 每颗灯
// 显示其所在象限颜色。换色轮换 4 种配色 (厂商同款旋转序), 灯序偏移即时生效并保存。
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AmbientLight.Interop;
using AmbientLight.Services;

namespace AmbientLight.Views;

public partial class CalibrateWindow : Window
{
    // 配色 [左上,右上,左下,右下] = 色板下标; 厂商 btn_RotateColors 的 4 档轮换序
    private static readonly int[][] Schemes =
    [
        [0, 1, 2, 3],
        [3, 0, 1, 2],
        [2, 3, 0, 1],
        [1, 2, 3, 0],
    ];
    private static readonly Color[] Palette = [Colors.Red, Colors.Green, Colors.Blue, Colors.Yellow];
    private static readonly string[] ColorNames = ["红", "绿", "蓝", "黄"];

    private readonly int _slot;
    private readonly string _key;
    private int _scheme;
    private MonitorMap.Info? _target;
    private readonly DispatcherTimer _poll;

    private CalibrateWindow(int slot, string key)
    {
        InitializeComponent();
        _slot = slot;
        _key = key;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _poll.Tick += (_, _) => PollOnce();
        Loaded += (_, _) => { ApplyCalibConfig(); RefreshAll(); _poll.Start(); };
        Closed += (_, _) =>
        {
            _poll.Stop();
            App.Instance.Calibrating = false;
            RestoreConfig();
        };
        ContentRendered += (_, _) => FinalizePlacement();
    }

    /// <summary>入口: 选中设备会话必须已运行 (校准 = 追光采集本屏)。</summary>
    public static void TryOpen()
    {
        var app = App.Instance;
        if (app.Calibrating) return;
        string key = ConfigStore.SelectedKey;
        int slot = app.SlotOf(key);
        if (slot < 0)
        {
            // 轮 34 补: 原来用 MessageBox —— 系统样式弹窗跟 Fluent 界面两套观感
            // (而且不随主题变)。统一走主窗口的 ContentDialogHost。
            _ = Controls.AppDialogs.ShowAsync("屏幕校准",
                "当前设备的核心会话未运行。\n请先在主页打开灯带 (校准靠追光采集本屏画面, 会话必须运行中)。",
                closeText: "知道了");
            return;
        }
        var win = new CalibrateWindow(slot, key);
        win.PlaceOn(ConfigStore.Device.MonitorIndex);
        win.Show();
        win.Activate();
    }

    // ---- 临时配置下发 (不落盘; App.Calibrating 期间 ApplyConfig 挂起) --------
    private void ApplyCalibConfig()
    {
        var cfg = ConfigStore.Device.ToCoreConfig();
        cfg.captureRange = 2;       // 厂商同款: 校准时取色带贴边, 中央面板不参与取色
        cfg.colorCut = 0;
        cfg.brightness = 1.0;       // 校准要肉眼分辨颜色, 临时拉满 (关闭时随恢复还原)
        cfg.modeIndex = 0;
        cfg.ledRotate = ConfigStore.Device.LedRotate;
        AmbientCore.SetConfigEx(_slot, ref cfg);
    }

    private void RestoreConfig()
    {
        try
        {
            var cfg = ConfigStore.Device.ToCoreConfig();
            AmbientCore.SetConfigEx(_slot, ref cfg);
        }
        catch { /* 会话已停则无需恢复 */ }
    }

    // ---- 定位: 核心显示器序 → 设备名 → 物理矩形, DIP 换算 --------------------
    private void PlaceOn(int monitorIndex)
    {
        var mons = MonitorMap.All();
        string? want = null;
        try
        {
            var core = AmbientCore.GetMonitors();
            if (monitorIndex >= 0 && monitorIndex < core.Length) want = core[monitorIndex].Name;
        }
        catch { }
        // Info 是结构体: 按优先级逐级挑选 (设备名 → 主屏 → 首个)
        MonitorMap.Info pick = default;
        bool found = false;
        if (!string.IsNullOrEmpty(want))
            foreach (var m in mons)
                if (m.Device == want) { pick = m; found = true; break; }
        if (!found)
            foreach (var m in mons)
                if (m.Primary) { pick = m; found = true; break; }
        if (!found && mons.Count > 0) { pick = mons[0]; found = true; }
        _target = found ? pick : null;
        if (_target is { } t)
            SetRect((t.Px, 96.0 / t.DpiX, 96.0 / t.DpiY));   // 首摆按目标屏 DPI 估计
    }

    /// <summary>按目标显示器与 DPI 缩放系数 (物理 px × 缩放 = DIP→px 反解) 摆满整屏。</summary>
    private void SetRect((MonitorMap.Rect px, double sx, double sy) r)
    {
        Left = r.px.Left / r.sx;
        Top = r.px.Top / r.sy;
        Width = r.px.Width / r.sx;
        Height = r.px.Height / r.sy;
    }

    /// <summary>显示后按窗口实际 DPI 重摆一次 (首摆用的是目标屏 DPI 估计值)。</summary>
    private void FinalizePlacement()
    {
        if (_target is not { } t) return;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        SetRect((t.Px, dpi.PixelsPerDip, dpi.PixelsPerDip));
    }

    // ---- 状态刷新 ------------------------------------------------------------
    private bool _blink;

    private void PollOnce()
    {
        try
        {
            var st = AmbientCore.GetStatusEx(_slot);
            // 预览是固定参照 (ShowQuadrantMap), 不随物理灯带的偏移走 → 这里只刷状态。
            // 状态行带闪烁指示是有意的: 校准屏其余部分静止时, 桌面无更新 → DDA 不出帧,
            // 灯带就不刷新 (改偏移看不到效果)。字符轮换保证每 120ms 产生一次桌面更新。
            _blink = !_blink;
            string live = _blink ? " ●" : " ○";
            StatusText.Text = st.state switch
            {
                AmbientCore.StateRunning => "会话运行中" + live,
                AmbientCore.StateDevLost => "设备失联 (DEV_LOST): 灯带被拔出或端口被占用?" + live,
                AmbientCore.StateWaitDisp => "画面暂不可用 (锁屏/熄屏)" + live,
                _ => $"会话状态 {st.state}" + live,
            };
        }
        catch { }
    }

    private void RefreshAll()
    {
        var dev = ConfigStore.Device;
        // 配色方案
        int[] s = Schemes[_scheme];
        QuadTL.Background = new SolidColorBrush(Palette[s[0]]);
        QuadTR.Background = new SolidColorBrush(Palette[s[1]]);
        QuadBL.Background = new SolidColorBrush(Palette[s[2]]);
        QuadBR.Background = new SolidColorBrush(Palette[s[3]]);
        SchemeText.Text = $"配色方案 {_scheme + 1}/4 · 左上{ColorNames[s[0]]} 右上{ColorNames[s[1]]}" +
                          $" 左下{ColorNames[s[2]]} 右下{ColorNames[s[3]]}";
        // 分辨率 (核心枚举的目标显示器) — 同时供预览按真实长宽比布局
        int mw = 0, mh = 0;
        try
        {
            var core = AmbientCore.GetMonitors();
            int i = Math.Clamp(dev.MonitorIndex, 0, Math.Max(core.Length - 1, 0));
            if (core.Length > 0)
            {
                mw = core[i].width; mh = core[i].height;
                // 与主页/采集页统一 (轮 28): 用 1 起的序号 + 分辨率 + 适配器,
                // 不再显示 `\\.\DISPLAY5` 这种不友好的设备名
                ResText.Text = $"校准屏 {i + 1} · {mw}×{mh} · {core[i].Adapter}" +
                               (core[i].primary != 0 ? " (主屏)" : "");
            }
        }
        catch { }
        // 灯序偏移
        int maxRot = MaxRotate(dev.LedCount);
        RotateText.Text = $"灯数 {dev.LedCount} · 灯序偏移 {dev.LedRotate:+0;-0;0} (±{maxRot}) — 按钮直接移动物理灯带";
        RotLeftBtn.IsEnabled = maxRot > 0 && dev.LedRotate > -maxRot;
        RotRightBtn.IsEnabled = maxRot > 0 && dev.LedRotate < maxRot;
        RotResetBtn.IsEnabled = maxRot > 0 && dev.LedRotate != 0;
        // 预览 = 固定标准图 (不随偏移): 每格 = 该序号 LED 在 rotate=0 时的位置与象限色
        Ring.Configure(dev.LedCount, dev.Sides, dev.Direction, 0, mw, mh);
        Ring.ShowQuadrantMap([Palette[s[0]], Palette[s[1]], Palette[s[2]], Palette[s[3]]]);
        // 中心区: 0 号灯起点徽标 + 绕向箭头 (M5.1 §7 —— 这两个信息只在"对照参照图排错"时有用,
        // 所以放在校准向导里而不是主页)。direction 0 = 逆时针 (与核心段序一致)。
        Ring.SetOrientation(true, 0, clockwise: dev.Direction != 0);
        IndexText.Text = "LED 序号: " + Ring.DescribeIndices();
    }

    private void OnRotateColors(object sender, RoutedEventArgs e)
    {
        _scheme = (_scheme + 1) % Schemes.Length;
        RefreshAll();
    }

    private void OnRotLeft(object sender, RoutedEventArgs e) => ChangeRotate(-1);

    private void OnRotRight(object sender, RoutedEventArgs e) => ChangeRotate(+1);

    private void OnRotReset(object sender, RoutedEventArgs e) => ChangeRotate(0);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    /// <summary>delta=0 表示重置; 其余 ±1。立即下发 + 落盘 (灯序偏移是持久设置)。</summary>
    private void ChangeRotate(int delta)
    {
        var dev = ConfigStore.Device;
        int maxRot = MaxRotate(dev.LedCount);
        int next = delta == 0 ? 0 : Math.Clamp(dev.LedRotate + delta, -maxRot, maxRot);
        if (next == dev.LedRotate) return;
        dev.LedRotate = next;
        ConfigStore.SaveDevice();
        ApplyCalibConfig();
        RefreshAll();
    }

    /// <summary>与核心 ValidateConfig / 采集页同公式: ±灯数 12%, 灯数 <10 → 0。</summary>
    internal static int MaxRotate(int ledCount)
    {
        int n = Math.Min(ledCount, AmbientCore.MaxLeds);
        return n >= 10 ? n * 12 / 100 : 0;
    }
}

// SettingsPageCapture.xaml.cs — 采集设置: 每次 Loaded 从 ConfigStore 重读,
// 变更经 ApplyConfigDebounced 热更+落盘 (200ms 防抖)。范围对齐厂商 FormSettings。
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Interop;
using AmbientLight.Services;

namespace AmbientLight.Views;

public sealed record MonitorItem(int Index, string Label);

public partial class SettingsPageCapture : Page
{
    private static readonly int[] FpsCapChoices = [0, 240, 165, 144, 120, 90, 60, 30];   // 序与下拉一致 (M2 起显示序为降序, 原数组升序导致选 240 实发 30)
    private static readonly int[] SmoothingChoices = [0, 3, 4, 5];   // 厂商: 步长 = 2+配置值
    private bool _loading = true;   // InitializeComponent 解析期即有事件, 初值阶段一律忽略

    public SettingsPageCapture()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);   // 滚轮滚动由外层滚动器承担
        // 订阅放在 InitializeComponent 之后: 解析期 Value="60" 不会触发处理程序
        LedCountBox.ValueChanged += (_, _) => OnLedCountValueChanged();
        Loaded += (_, _) => { _loading = true; LoadFromConfig(); _loading = false; };
    }

    private void LoadFromConfig()
    {
        var dev = ConfigStore.Device;
        LedCountBox.Value = Math.Clamp(dev.LedCount, 1, 180);
        SidesCombo.SelectedIndex = Math.Clamp(dev.Sides, 2, 4) - 2;
        DirectionCombo.SelectedIndex = dev.Direction != 0 ? 1 : 0;
        RefreshRotateClamp(dev.LedCount);
        CaptureRangeSlider.Value = Math.Clamp(dev.CaptureRange, 5, 40);
        int sm = Array.IndexOf(SmoothingChoices, dev.Smoothing);
        SmoothingCombo.SelectedIndex = sm < 0 ? 0 : sm;
        int fps = Array.IndexOf(FpsCapChoices, dev.FpsCap);
        FpsCapCombo.SelectedIndex = fps < 0 ? 0 : fps;
        BlackBarsToggle.IsChecked = dev.BlackBars;
        BlackBarLevelSlider.Value = Math.Clamp(dev.BlackBarLevel, 0, 20);
        int chaseMode = Math.Clamp(dev.ChaseSmoothMode, 0, 2);
        ChaseSmoothToggle.IsChecked = chaseMode != 0;
        ChaseModeGuardRadio.IsChecked = chaseMode == 1;
        ChaseModeBothRadio.IsChecked = chaseMode == 2;
        ChaseModeGuardRadio.IsEnabled = ChaseModeBothRadio.IsEnabled = chaseMode != 0;
        RefreshMonitors(select: dev.MonitorIndex);
    }

    // v1.6 追光柔化平滑 (每设备): UI 是"总开关 + 二选一", 落到一个档位字段 (0/1/2)。
    // 核心在**写帧前**作用于字节 (输出层柔化, 不动颜色链)。
    private void OnChaseSmoothToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyChaseMode();
    }

    private void OnChaseModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyChaseMode();
    }

    private void ApplyChaseMode()
    {
        bool on = ChaseSmoothToggle.IsChecked == true;
        // 打开总开关时**必须有一个默认档位** —— 否则会出现"两个单选都没选中, 但配置已经落在 1"的
        // 状态: 行为生效了、界面上却看不出选了哪个 (用户实测反馈) → 默认 = 抑制闪光 ✓
        if (on && ChaseModeGuardRadio.IsChecked != true && ChaseModeBothRadio.IsChecked != true)
            ChaseModeGuardRadio.IsChecked = true;
        ChaseModeGuardRadio.IsEnabled = ChaseModeBothRadio.IsEnabled = on;   // 关着总开关时不可选
        ConfigStore.Device.ChaseSmoothMode =
            !on ? 0 : (ChaseModeBothRadio.IsChecked == true ? 2 : 1);
        App.Instance.ApplyConfigDebounced();
    }

    private void RefreshMonitors(int select)
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            // 文案 (轮 28 用户定稿, 轮 29 微调分隔符): `序号. 分辨率 - 适配器 - 主屏`
            // 不再出现 `[0]` 与 `\\.\DISPLAY5` 这种不友好的名字; 序号从 1 起, 与主页编号徽标一致。
            MonitorCombo.ItemsSource = mons
                .Select((m, i) => new MonitorItem(i,
                    $"{i + 1}. {m.width}×{m.height} - {m.Adapter}" +
                    (m.primary != 0 ? " - 主屏" : "")))
                .ToList();
            if (mons.Length > 0)
                MonitorCombo.SelectedIndex = Math.Clamp(select, 0, mons.Length - 1);
            else
                MonitorCombo.SelectedIndex = -1;
        }
        catch
        {
            MonitorCombo.ItemsSource = new List<MonitorItem>
                { new(0, "显示器枚举失败 (核心未加载?)" ) };
        }
    }

    private void OnRefreshMonitors(object sender, RoutedEventArgs e)
    {
        _loading = true;
        RefreshMonitors(select: ConfigStore.Device.MonitorIndex);
        _loading = false;
    }

    /// <summary>全屏校准向导 (轮 28 从「高级」页移来, 就在灯序偏移下面 —— 这是它的使用场景)。</summary>
    private void OnCalibrate(object sender, RoutedEventArgs e) => CalibrateWindow.TryOpen();

    private void OnMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MonitorCombo.SelectedItem is not MonitorItem mi) return;
        ConfigStore.Device.MonitorIndex = mi.Index;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnLedCountValueChanged()
    {
        if (_loading) return;
        if (LedCountBox.Value is double v)
        {
            ConfigStore.Device.LedCount = (int)Math.Round(v);
            RefreshRotateClamp(ConfigStore.Device.LedCount);   // 灯数变化 → 偏移范围联动
            App.Instance.ApplyConfigDebounced();
        }
    }

    // ---- 灯序偏移 (M4 校准): 范围 = ±灯数 12% (与核心 ValidateConfig 同公式) ----
    private void RefreshRotateClamp(int ledCount)
    {
        int n = Math.Min(ledCount, Interop.AmbientCore.MaxLeds);
        int maxRot = n >= 10 ? n * 12 / 100 : 0;
        RotateBox.Minimum = -maxRot;
        RotateBox.Maximum = maxRot;
        RotateBox.Value = Math.Clamp(ConfigStore.Device.LedRotate, -maxRot, maxRot);
        RotateBox.IsEnabled = maxRot > 0;
        RotateRow.Description = maxRot > 0
            ? $"校正灯带物理起点与协议起点的少量错位 (±{maxRot}); 全屏校准向导里可直接试出正确值"
            : "灯数少于 10 时无需灯序偏移";
    }

    private void OnRotateChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (RotateBox.Value is double v)
        {
            ConfigStore.Device.LedRotate = (int)Math.Round(v);
            App.Instance.ApplyConfigDebounced();
        }
    }

    private void OnSidesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SidesCombo.SelectedIndex < 0) return;
        ConfigStore.Device.Sides = SidesCombo.SelectedIndex + 2;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnDirectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DirectionCombo.SelectedIndex < 0) return;
        ConfigStore.Device.Direction = DirectionCombo.SelectedIndex;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnCaptureRangeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CaptureRangeValue != null) CaptureRangeValue.Text = $"{(int)e.NewValue}";
        if (_loading) return;
        ConfigStore.Device.CaptureRange = (int)e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnSmoothingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SmoothingCombo.SelectedIndex < 0) return;
        ConfigStore.Device.Smoothing = SmoothingChoices[SmoothingCombo.SelectedIndex];
        App.Instance.ApplyConfigDebounced();
    }

    private void OnFpsCapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FpsCapCombo.SelectedIndex < 0) return;
        ConfigStore.Device.FpsCap = FpsCapChoices[FpsCapCombo.SelectedIndex];
        App.Instance.ApplyConfigDebounced();
    }

    private void OnBlackBarsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.Device.BlackBars = BlackBarsToggle.IsChecked == true;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnBlackBarLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlackBarLevelValue != null) BlackBarLevelValue.Text = $"{(int)e.NewValue}";
        if (_loading) return;
        ConfigStore.Device.BlackBarLevel = (int)e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }
}

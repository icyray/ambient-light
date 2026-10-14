// SettingsPageCapture.xaml.cs — 采集设置: 每次 Loaded 从 ConfigStore 重读,
// 变更经 ApplyConfigDebounced 热更+落盘 (200ms 防抖)。范围对齐厂商 FormSettings。
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Interop;
using AmbientLight.Localization;
using AmbientLight.Services;

namespace AmbientLight.Views;

public sealed record MonitorItem(int Index, string Label);

public partial class SettingsPageCapture : Page
{
    // ⚠️ 上限 **144 = 本灯带实测硬上限** (2026-10-14 LedRateProbe 扫描: 145Hz 起 USB CDC 写阻塞,
    //    write 1.08 → 6.24ms、发送被钳; ≤144 写耗时恒定 ~1.1ms)。0(不限)/240/165 已退役 ——
    //    "不限"在 240Hz 屏上会直接放飞 (是"微光抖动"那类问题的放大器), 旧配置迁移见 LoadFromConfig。
    // 序与下拉一致 (M2 起显示序为降序, 原数组升序导致选 240 实发 30)。
    // **三处耦合**: ① 本表 ② 本页 XAML 的 FpsCapCombo 项 (顺序=此表顺序) ③ native ValidateConfig
    //    的 fpsCap 归一 (AmbientCore.cpp) —— 改一处必须同步改另两处 (探针有"项数==表长 / 值⇄项
    //    往返 / 项文本数字==值 / 表内最大值==144"四条断言兜底)。要放开更高档位: 先用
    //    tools/LedRateProbe 实测目标设备的阻塞点, 再同步改三处。
    public static readonly int[] FpsCapChoices = [144, 120, 90, 60, 30];
    public static readonly int[] SmoothingChoices = [0, 3, 4, 5];   // 厂商: 步长 = 2+配置值
    private bool _loading = true;   // InitializeComponent 解析期即有事件, 初值阶段一律忽略

    public SettingsPageCapture()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);   // 滚轮滚动由外层滚动器承担
        // 订阅放在 InitializeComponent 之后: 解析期 Value="60" 不会触发处理程序
        LedCountBox.ValueChanged += (_, _) => OnLedCountValueChanged();
        // M5.3 语言: 显示器下拉的「N. 宽×高 - 适配器」是 C# 拼的 → 换语言时重建
        LanguageService.LanguageChanged += OnLanguageChanged;
        Loaded += (_, _) => { _loading = true; LoadFromConfig(); _loading = false; };
        Unloaded += (_, _) => LanguageService.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>M5.3: 换语言后重建显示器下拉 (其余文案随 {DynamicResource} 自动刷)。</summary>
    private void OnLanguageChanged()
    {
        if (!IsLoaded) return;               // 没在页面上就不用管 (切过去时会重新构造)
        _loading = true;
        LoadFromConfig();
        _loading = false;
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
        if (fps < 0)
        {
            // 旧配置残留 (0=不限 / 240 / 165 …): 表已收口到 144 上限。归一并**当场写实**配置
            // (对齐"显示器回退提示"那次的教训: 只改显示不写配置 = 核心拿着旧值跑, 与 UI 脱节)。
            // 语义: >144 的"要快" → 144 (合法最高); 0(不限)/非法 → 60 (安全默认)。
            int norm = dev.FpsCap > 144 ? 144 : 60;
            ConfigStore.Device.FpsCap = norm;
            App.Instance?.ApplyConfigDebounced();
            fps = Array.IndexOf(FpsCapChoices, norm);
        }
        FpsCapCombo.SelectedIndex = fps < 0 ? 0 : fps;
        BlackBarsToggle.IsChecked = dev.BlackBars;
        BlackBarLevelSlider.Value = Math.Clamp(dev.BlackBarLevel, 0, 20);
        // 档位字段: **符号 = 开关, 绝对值 = 档位** → 关着也记得上次选的档 (用户口径)
        int chaseMag = Math.Clamp(Math.Abs(dev.ChaseSmoothMode), 0, 2);
        bool chaseOn = dev.ChaseSmoothMode > 0;
        ChaseSmoothToggle.IsChecked = chaseOn;
        ChaseModeGuardRadio.IsChecked = chaseMag <= 1;      // 0 (从未启用) 也默认停在"抑制闪光"
        ChaseModeBothRadio.IsChecked = chaseMag == 2;
        ChaseModeGuardRadio.IsEnabled = ChaseModeBothRadio.IsEnabled = chaseOn;
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
        // 首次打开 (两个单选都未选) 时默认落到"抑制闪光" —— 否则会出现"界面看不出选了哪个、
        // 但配置已经生效"的状态 ✓
        if (ChaseModeGuardRadio.IsChecked != true && ChaseModeBothRadio.IsChecked != true)
            ChaseModeGuardRadio.IsChecked = true;
        ChaseModeGuardRadio.IsEnabled = ChaseModeBothRadio.IsEnabled = on;   // 关着总开关时不可选
        int mag = ChaseModeBothRadio.IsChecked == true ? 2 : 1;              // 档位 (绝对值)
        // **符号 = 开关, 绝对值 = 档位**: 关掉时写负值 → "关掉去玩游戏、回来再看电影"时,
        // 打开还是他上次选的档, 不会被重置成默认 (用户 2026-10-09 提议) ✓
        ConfigStore.Device.ChaseSmoothMode = on ? mag : -mag;
        App.Instance.ApplyConfigDebounced();
    }

    private void RefreshMonitors(int select)
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            // 文案: `序号. 分辨率 - 适配器 - 主屏`
            // 不再出现 `[0]` 与 `\\.\DISPLAY5` 这种不友好的名字; 序号从 1 起, 与主页编号徽标一致。
            MonitorCombo.ItemsSource = mons
                .Select((m, i) => new MonitorItem(i,
                    Loc.F("Cap.Monitor.Item", i + 1, m.width, m.height, m.Adapter) +
                    (m.primary != 0 ? " - " + Loc.T("Cap.Monitor.PrimaryMark") : "")))
                .ToList();
            if (mons.Length > 0)
            {
                // 配置的显示器不在位 (拔线/扩展转单屏后的残留) → 选中项落到"主屏"
                // (与核心 PickOutput 的回退目标同一语义), 并把配置**当场写实**。否则:
                //   ① 下拉看起来已选中主屏, 配置却还是那块关闭的屏 → 核心永远 fallback,
                //      主页"已回退主屏"提示常驻、重启不消失;
                //   ② 用户"点唯一的主屏"也修不回来 —— SelectedIndex 早就是它, 选择事件不触发。
                // (2026-10-14 用户报)
                int use = select >= 0 && select < mons.Length
                    ? select
                    : MonitorMap.PrimaryIndexOf(mons);
                MonitorCombo.SelectedIndex = use;
                if (use != select)
                {
                    ConfigStore.Device.MonitorIndex = use;
                    App.Instance?.ApplyConfigDebounced();   // 探针/解析期无 App 实例时静默 (防御)
                }
            }
            else
                MonitorCombo.SelectedIndex = -1;
        }
        catch
        {
            MonitorCombo.ItemsSource = new List<MonitorItem>
                { new(0, Loc.T("Cap.Monitor.EnumFailed")) };
        }
    }

    private void OnRefreshMonitors(object sender, RoutedEventArgs e)
    {
        _loading = true;
        RefreshMonitors(select: ConfigStore.Device.MonitorIndex);
        _loading = false;
    }

    /// <summary>全屏校准向导 (从「高级」页移来, 就在灯序偏移下面 —— 这是它的使用场景)。</summary>
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
            ? Loc.F("Cap.Rotate.Desc", maxRot)
            : Loc.T("Cap.Rotate.NoNeed");
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

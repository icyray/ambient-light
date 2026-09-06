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
    private static readonly int[] FpsCapChoices = [0, 30, 60, 90, 120, 144, 165, 240];
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
        CaptureRangeSlider.Value = Math.Clamp(dev.CaptureRange, 5, 40);
        int sm = Array.IndexOf(SmoothingChoices, dev.Smoothing);
        SmoothingCombo.SelectedIndex = sm < 0 ? 0 : sm;
        int fps = Array.IndexOf(FpsCapChoices, dev.FpsCap);
        FpsCapCombo.SelectedIndex = fps < 0 ? 0 : fps;
        BlackBarsToggle.IsChecked = dev.BlackBars;
        BlackBarLevelSlider.Value = Math.Clamp(dev.BlackBarLevel, 0, 20);
        RefreshMonitors(select: dev.MonitorIndex);
    }

    private void RefreshMonitors(int select)
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            MonitorCombo.ItemsSource = mons
                .Select((m, i) => new MonitorItem(i,
                    $"[{i}] {m.width}x{m.height}{(m.primary != 0 ? "  主屏" : "")}  —  {m.Name}"))
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

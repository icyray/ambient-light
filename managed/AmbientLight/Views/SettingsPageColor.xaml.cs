// SettingsPageColor.xaml.cs — 颜色链参数。变更经 ApplyConfigDebounced 热更+落盘。
// 纯色拾色器已迁至 HomePage (M5.1), 本页只剩颜色链。
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Services;

namespace AmbientLight.Views;

public partial class SettingsPageColor : Page
{
    /// <summary>灯珠色序下拉的项数 (与 XAML 里的 ComboBoxItem 个数**必须一致**)。
    /// 两处独立存在 → 探针断言 items.Count == 它 (加一项漏改这个常数, 多出来的那项永远选不中)。</summary>
    public const int OutputOrderCount = 6;

    private bool _loading = true;   // InitializeComponent 解析期即有事件, 初值阶段一律忽略

    public SettingsPageColor()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);   // 滚轮滚动由外层滚动器承担
        Loaded += (_, _) => { _loading = true; LoadFromConfig(); _loading = false; };
    }

    private void LoadFromConfig()
    {
        var dev = ConfigStore.Device;
        VibranceSlider.Value = Math.Clamp(dev.Vibrance, 0, 20);
        HueSlider.Value = Math.Clamp(dev.Hue, 0, 2);
        GammaSlider.Value = Math.Clamp(dev.Gamma, 0.3, 1.0);
        ColorCutSlider.Value = Math.Clamp(dev.ColorCut, 0, 25);
        RGainSlider.Value = Math.Clamp(dev.RGain, 0, 2);
        GGainSlider.Value = Math.Clamp(dev.GGain, 0, 2);
        BGainSlider.Value = Math.Clamp(dev.BGain, 0, 2);
        RefreshGainSummary();
        OutputCombo.SelectedIndex = Math.Clamp(dev.OutputOrder, 0, OutputOrderCount - 1);
    }

    // ---- 颜色链滑条 ------------------------------------------------------
    // 解析期 ValueChanged 即会触发 (M2 老坑): 此时 GainSummaryText/GGainSlider/BGainSlider
    // 等尚未建好, 全部判空
    private void RefreshGainSummary()
    {
        if (GainSummaryText == null || RGainSlider == null || GGainSlider == null || BGainSlider == null)
            return;
        GainSummaryText.Text = $"×{RGainSlider.Value:0.00} ×{GGainSlider.Value:0.00} ×{BGainSlider.Value:0.00}";
    }

    private void OnVibranceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VibranceValue != null) VibranceValue.Text = e.NewValue.ToString("F1", CultureInfo.InvariantCulture);
        if (_loading) return;
        ConfigStore.Device.Vibrance = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HueValue != null) HueValue.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
        if (_loading) return;
        ConfigStore.Device.Hue = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnGammaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GammaValue != null) GammaValue.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
        if (_loading) return;
        ConfigStore.Device.Gamma = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnColorCutChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ColorCutValue != null) ColorCutValue.Text = $"{(int)e.NewValue}";
        if (_loading) return;
        ConfigStore.Device.ColorCut = (int)e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnRGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RGainValue != null) RGainValue.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
        RefreshGainSummary();
        if (_loading) return;
        ConfigStore.Device.RGain = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnGGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GGainValue != null) GGainValue.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
        RefreshGainSummary();
        if (_loading) return;
        ConfigStore.Device.GGain = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnBGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BGainValue != null) BGainValue.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
        RefreshGainSummary();
        if (_loading) return;
        ConfigStore.Device.BGain = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputCombo.SelectedIndex < 0) return;
        ConfigStore.Device.OutputOrder = OutputCombo.SelectedIndex;
        App.Instance.ApplyConfigDebounced();
    }
}

// SettingsPageColor.xaml.cs — 颜色链参数 + 纯色拾色器。变更经 ApplyConfigDebounced
// 热更+落盘; 纯色 RGB 直接写 SolidColor (hex) 字段。
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AmbientLight.Services;

namespace AmbientLight.Views;

public partial class SettingsPageColor : Page
{
    // 常用纯色预设 (第一项 = 默认暖白)
    private static readonly (string Hex, string Name)[] Presets =
    {
        ("#FFE1D6", "暖白"), ("#FFF6E0", "米白"), ("#FFFFFF", "纯白"), ("#C9E4FF", "冷白"),
        ("#FF3B30", "红"),   ("#FF9500", "橙"),   ("#FFCC00", "黄"),   ("#34C759", "绿"),
        ("#5AC8FA", "青"),   ("#007AFF", "蓝"),   ("#AF52DE", "紫"),   ("#FF2D55", "粉"),
    };

    private bool _loading = true;   // InitializeComponent 解析期即有事件, 初值阶段一律忽略
    private bool _hexEditing;                   // HexBox 输入过程中避免反写

    public SettingsPageColor()
    {
        InitializeComponent();
        Loaded += (_, _) => { _loading = true; LoadFromConfig(); _loading = false; };
        BuildPresets();
    }

    private void BuildPresets()
    {
        foreach (var (hex, name) in Presets)
        {
            var btn = new System.Windows.Controls.Button
            {
                Width = 34,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)),
                ToolTip = name,
                Tag = hex,
            };
            btn.Click += OnPresetClick;
            PresetStrip.Items.Add(btn);
        }
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
        OutputCombo.SelectedIndex = Math.Clamp(dev.OutputOrder, 0, 5);

        var c = ParseHex(dev.SolidColor, (255, 225, 214));
        SRSlider.Value = c.r; SGSlider.Value = c.g; SBSlider.Value = c.b;
        RefreshSolidPreview(c.r, c.g, c.b);
        _hexEditing = true;
        HexBox.Text = $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        _hexEditing = false;
    }

    // ---- 颜色链滑条 ------------------------------------------------------
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
        if (_loading) return;
        ConfigStore.Device.RGain = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnGGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GGainValue != null) GGainValue.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
        if (_loading) return;
        ConfigStore.Device.GGain = e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnBGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BGainValue != null) BGainValue.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
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

    // ---- 纯色拾色器 ------------------------------------------------------
    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string hex }) return;
        var (r, g, b) = ParseHex(hex, (255, 225, 214));
        _loading = true;
        SRSlider.Value = r; SGSlider.Value = g; SBSlider.Value = b;
        _loading = false;
        ApplySolid(r, g, b);
    }

    private void OnSolidRgbChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SRValue != null) SRValue.Text = $"{(int)SRSlider.Value}";
        if (SGValue != null) SGValue.Text = $"{(int)SGSlider.Value}";
        if (SBValue != null) SBValue.Text = $"{(int)SBSlider.Value}";
        if (_loading) return;
        ApplySolid((int)SRSlider.Value, (int)SGSlider.Value, (int)SBSlider.Value);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _hexEditing) return;
        var (r, g, b) = ParseHex(HexBox.Text, (-1, -1, -1));
        if (r < 0) return;                       // 输入未完成时忽略
        _loading = true;
        SRSlider.Value = r; SGSlider.Value = g; SBSlider.Value = b;
        _loading = false;
        ApplySolid(r, g, b);
    }

    private void ApplySolid(int r, int g, int b)
    {
        RefreshSolidPreview(r, g, b);
        ConfigStore.Device.SolidColor = $"#{r:X2}{g:X2}{b:X2}";
        App.Instance.ApplyConfigDebounced();
        _hexEditing = true;
        HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
        _hexEditing = false;
    }

    private void RefreshSolidPreview(int r, int g, int b)
        => SolidPreview.Background = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));

    private static (int r, int g, int b) ParseHex(string? hex, (int r, int g, int b) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);   // #AARRGGBB → RGB
        if (s.Length != 6) return fallback;
        try
        {
            return (int.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(s[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        catch { return fallback; }
    }
}

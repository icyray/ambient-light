// HomePage.Picker.cs — 纯色拾色器 (H+S 两根滑块 + 预设色板 + 自定义色槽)。
// (HomePage partial 之一: 核心状态在 HomePage.xaml.cs, 本文件只放这一块职责)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AmbientLight.Controls;
using AmbientLight.Interop;
using AmbientLight.Localization;
using AmbientLight.Services;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class HomePage : Page
{
    // ---- §3 纯色灯光拾色器: H + S 即完整 HSV (V = 主页「亮度」行; ConfigStore.Brightness 是
    //      颜色链之后真正的线性调光乘数)。拖动时以 (H,S) 为真值源, 只在初值/HEX/预设时反推 ——
    //      否则 8bit 往返会让色相旋钮自己抖 (HEX 每通道只有 8bit, 反推 H 会跳)。 ----
    private const int CustomSlotCount = 5;            // 自定义色槽数量
    private const double CustomColorDelayMs = 1000;    // 颜色停手多久才算"定了"并记入自定义槽
    private readonly List<System.Windows.Controls.Button> _presetBtns = [];
    private readonly List<System.Windows.Controls.Button> _customBtns = [];
    private DispatcherTimer? _customColorTimer;

    /// <summary>收集 XAML 静态声明的预设色块与自定义槽 (只做一次)。
    /// 分拣判据: 收集时**预设的 Tag 已是颜色** (XAML 里声明), 自定义槽没有 Tag ——
    /// 之后 RefreshCustomSlots 会往空槽写 Tag, 所以必须在这个时点分拣 (见 HomePage.xaml 的注释)。</summary>
    private void CollectPresetButtons()
    {
        if (_presetBtns.Count > 0) return;
        foreach (var b in PresetStrip.Children.OfType<System.Windows.Controls.Button>())
        {
            if (b.Tag is string) _presetBtns.Add(b);
            else _customBtns.Add(b);
        }
        RefreshCustomSlots();
    }

    /// <summary>把记忆里的自定义色刷到 5 个槽上 (空槽画成一个空心圆: 模板里 BorderBrush 有、Fill 没有)。
    /// 槽与颜色列表按顺序对应, 不做左右补位 —— 清除中间一个会整体前移, 简单可预期。</summary>
    private void RefreshCustomSlots()
    {
        var colors = ConfigStore.App.CustomColors;
        for (int i = 0; i < _customBtns.Count; i++)
        {
            var btn = _customBtns[i];
            bool has = i < colors.Count && !string.IsNullOrWhiteSpace(colors[i]);
            string? hex = has ? colors[i] : null;
            btn.Tag = hex;
            btn.Background = has
                ? new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex!))
                : Brushes.Transparent;
            btn.BorderBrush = ThemeBrush(has ? "ControlStrokeColorDefaultBrush" : "TextFillColorSecondaryBrush");
            btn.ToolTip = has ? Loc.F("Home.Picker.ClearHint", hex) : Loc.T("Home.Picker.EmptySlot");
        }
        UpdatePresetSelection();
    }

    /// <summary>记住一个自定义色 (非预设、去重、最新在前、最多 5 个)。
    /// 由"颜色停手 600ms"的防抖计时器触发 —— 拖 H/S 滑条时会连续产生几十个近似色,
    /// 不防抖的话 5 个槽一秒就被刷满了。</summary>
    private void RememberCustomColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        // 预设色不重复记入自定义槽 (预设本身就在列表里, 再记一遍没意义)
        if (_presetBtns.Any(b => string.Equals(b.Tag as string, hex, StringComparison.OrdinalIgnoreCase))) return;
        var list = ConfigStore.App.CustomColors;
        list.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        while (list.Count > CustomSlotCount) list.RemoveAt(list.Count - 1);
        ConfigStore.SaveApp();
        RefreshCustomSlots();
    }

    private void OnCustomSlotClear(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string hex }) return;
        ConfigStore.App.CustomColors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        ConfigStore.SaveApp();
        RefreshCustomSlots();
        e.Handled = true;
    }

    private void LoadSolidFromConfig()
    {
        var (r, g, b) = ParseHex(ConfigStore.Device.SolidColor, (255, 225, 214));
        SetHsvFromRgb(r, g, b);
        _hexEditing = true;
        HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
        _hexEditing = false;
        RefreshSolidSwatches();
    }

    /// <summary>把 RGB 反推成 H/S 写进滑块 (程序化写入 → 加 _hexEditing 抑制回环)。
    /// 颜色链侧看到的 V 恒为 1, 实际亮度由「亮度」行决定。</summary>
    private void SetHsvFromRgb(int r, int g, int b)
    {
        var (h, s, _) = RgbToHsv(r, g, b);
        _hexEditing = true;
        HueSlider.Value = h;
        SatSlider.Value = s * 100.0;
        _hexEditing = false;
        HValueText.Text = $"{(int)Math.Round(h)}°";
        SValueText.Text = $"{(int)Math.Round(s * 100)}%";
        RefreshHsvTracks();
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string hex }) return;
        var (r, g, b) = ParseHex(hex, (255, 225, 214));
        SetHsvFromRgb(r, g, b);
        ApplySolid(r, g, b);
    }

    private void OnHsvChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HValueText == null || SValueText == null || SatTrackBar == null) return;  // 解析期防护
        HValueText.Text = $"{(int)Math.Round(HueSlider.Value)}°";
        SValueText.Text = $"{(int)Math.Round(SatSlider.Value)}%";
        RefreshHsvTracks();
        if (_hexEditing || !_loaded) return;
        var (r, g, b) = HsvToRgb(HueSlider.Value, SatSlider.Value / 100.0, 1.0);
        ApplySolid(r, g, b);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_hexEditing || !_loaded) return;
        var (r, g, b) = ParseHex(HexBox.Text, (-1, -1, -1));
        if (r < 0) return;                       // 输入未完成时忽略
        SetHsvFromRgb(r, g, b);
        ApplySolid(r, g, b);
    }

    /// <summary>S 轨道 = 白 → 当前 H 的全饱和色 (HSV 里 S=0 且 V=1 就是白); H 变化时重画。
    /// 色相轨道是静态彩虹 (XAML 资源 HueRainbowBrush)。</summary>
    private void RefreshHsvTracks()
    {
        double h = HueSlider?.Value ?? 0;
        var (fr, fg, fb) = HsvToRgb(h, 1.0, 1.0);
        SatTrackBar.Background = new LinearGradientBrush(
            Colors.White, Color.FromRgb((byte)fr, (byte)fg, (byte)fb), 0);
        SatSwatch.Background = Frozen(Color.FromRgb((byte)fr, (byte)fg, (byte)fb));
    }

    private void ApplySolid(int r, int g, int b)
    {
        ConfigStore.Device.SolidColor = $"#{r:X2}{g:X2}{b:X2}";
        _hexEditing = true;
        HexBox.Text = ConfigStore.Device.SolidColor;
        _hexEditing = false;
        RefreshSolidSwatches();
        App.Instance.ApplyConfigDebounced();
        ScheduleCustomColorRemember();
    }

    /// <summary>颜色停手 600ms 后记入自定义槽 (拖动中不记 —— 否则一秒钟刷满 5 个槽)。</summary>
    private void ScheduleCustomColorRemember()
    {
        if (_customColorTimer == null)
        {
            _customColorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(CustomColorDelayMs),
            };
            _customColorTimer.Tick += (_, _) =>
            {
                _customColorTimer!.Stop();
                RememberCustomColor(ConfigStore.Device.SolidColor);
            };
        }
        _customColorTimer.Stop();
        _customColorTimer.Start();
    }

    /// <summary>Header 的色块 (生效色的对比行已按轮 29 用户要求去掉)。</summary>
    private void RefreshSolidSwatches()
    {
        var (r, g, b) = ParseHex(ConfigStore.Device.SolidColor, (255, 225, 214));
        SolidPreview.Background = Frozen(Color.FromRgb((byte)r, (byte)g, (byte)b));
        UpdatePresetSelection();
    }

    private void UpdatePresetSelection()
    {
        string cur = ConfigStore.Device.SolidColor;
        foreach (var b in PresetStrip.Children.OfType<System.Windows.Controls.Button>())
        {
            bool sel = string.Equals((string?)b.Tag, cur, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = sel ? ThemeBrush("AccentFillColorDefaultBrush")
                                : ThemeBrush("ControlStrokeColorDefaultBrush");
        }
    }

    private static (int r, int g, int b) ParseHex(string? hex, (int r, int g, int b) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);   // #AARRGGBB → RGB
        if (s.Length != 6) return fallback;
        try
        {
            return (int.Parse(s[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(s[4..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { return fallback; }
    }

    /// <summary>RGB(0-255) → HSV (h 0-360, s/v 0-1)</summary>
    public static (double h, double s, double v) RgbToHsv(int r, int g, int b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double mx = Math.Max(rd, Math.Max(gd, bd)), mn = Math.Min(rd, Math.Min(gd, bd));
        double d = mx - mn, h = 0;
        if (d > 0)
        {
            if (mx == rd) h = 60 * (((gd - bd) / d) % 6);
            else if (mx == gd) h = 60 * ((bd - rd) / d + 2);
            else h = 60 * ((rd - gd) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, mx <= 0 ? 0 : d / mx, mx);
    }

    /// <summary>HSV → RGB(0-255)</summary>
    public static (int r, int g, int b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255),
                (int)Math.Round((b + m) * 255));
    }
}

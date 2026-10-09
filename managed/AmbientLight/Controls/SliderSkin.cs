// SliderSkin.cs — 滑块皮肤的**页面侧声明** (配套 Styles/SliderStyle.xaml, 见 docs/SLIDER_TEMPLATE.md)
//
// 为什么需要这两个附加属性: 模板要按"这个滑块该画哪种填充"分流, 而 WPF-UI 的滑块没有这个开关。
// 三种模式与各自的理由:
//   Track  (默认) —— 从**最小值**到拇指的实体填充, **贴左边缘**(填 Decrease 重复按钮实现)。
//                    为什么不用官方 PART_SelectionRange: WPF 内部固定按 `Canvas.Left = 拇指宽/2 + …`
//                    算, 画的是"拇指从最小值走到当前位置"的行程 → 左端必然内缩半个拇指(本项目 10px),
//                    进度条缺一截在直觉上就是错的 (用户验收标准: 进度显示靠直觉, 没人会去量长度)。
//   Anchor        —— 从**中性值**向两侧长 (往右=增益, 往左=衰减)。这一档才用官方机制(它天然两边都能画),
//                    中性值由 `Anchor` 给 (不设则退回 Minimum)。⚠️ 官方机制要求 Start ≤ End,
//                    所以"往左"必须把两端**交换** → 由 SliderAnchorConverter 折算。
//   None          —— 不画填充。用于**轨道本身就是色带**的滑块 (纯色拾色器的色调/饱和度:
//                    轨道是一条彩虹/饱和度渐变, 叠一层填充会把渐变盖掉 ✗)。
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace AmbientLight.Controls;

public enum SliderFill
{
    /// <summary>从最小值到拇指的实体填充 (贴左边缘)。</summary>
    Track,
    /// <summary>从中性值向两侧长的填充 (增益/倍率类)。</summary>
    Anchor,
    /// <summary>不画填充 (轨道本身是色带)。</summary>
    None,
}

public static class SliderSkin
{
    /// <summary>该滑块画哪种填充 (见 <see cref="SliderFill"/>), 默认 <see cref="SliderFill.Track"/>。</summary>
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.RegisterAttached("Fill", typeof(SliderFill), typeof(SliderSkin),
            new PropertyMetadata(SliderFill.Track, OnFillChanged));

    /// <summary>Fill 变化 → 到模板里把那条 Track 填充收起/放开 (见 <see cref="ApplyFill"/>)。</summary>
    /// <summary>给探针用的入口 (无头宿主里 Loaded 事件不一定触发) —— 行为同 <see cref="ApplyFill"/>。</summary>
    public static void ApplyFillForProbe(Slider sl) => ApplyFill(sl);

    private static void OnFillChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider sl) return;
        if (sl.IsLoaded) ApplyFill(sl);
        sl.Loaded -= OnSliderLoaded;            // 避免重复订阅
        sl.Loaded += OnSliderLoaded;
    }

    private static void OnSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Slider sl) ApplyFill(sl);
    }

    /// <summary>把 Track 填充条按当前模式收起/放开。
    /// **为什么用代码而不是绑定**: 那条填充在 Decrease 重复按钮**自己的 ControlTemplate** 里 →
    /// 跨模板的信号全都试过且**都不可靠**: `AncestorType=Slider` 的 DataTrigger / 绑定不生效 ✗、
    /// `TemplatedParent` 在那层指的是 RepeatButton 自己 ✗、`ElementName` 也解析不出来 ✗、
    /// 经 Track 的属性路径绑定同样拿不到 (宽度静默变 0 ✗)。
    /// 而模板遍历是确定的: Slider 模板 → PART_Track → DecreaseRepeatButton → 它模板里的 TrackFillBar ✓</summary>
    private static void ApplyFill(Slider sl)
    {
        if (sl.Template?.FindName("PART_Track", sl) is not System.Windows.Controls.Primitives.Track track) return;
        var btn = track.DecreaseRepeatButton;
        if (btn?.Template?.FindName("TrackFillBar", btn) is UIElement bar)
            bar.Visibility = GetFill(sl) == SliderFill.Track ? Visibility.Visible : Visibility.Collapsed;
    }

    public static SliderFill GetFill(DependencyObject d) => (SliderFill)d.GetValue(FillProperty);
    public static void SetFill(DependencyObject d, SliderFill v) => d.SetValue(FillProperty, v);

    /// <summary><see cref="SliderFill.Anchor"/> 模式的中性值; 不设 (NaN) 时退回滑块的 Minimum。</summary>
    public static readonly DependencyProperty AnchorProperty =
        DependencyProperty.RegisterAttached("Anchor", typeof(double), typeof(SliderSkin),
            new PropertyMetadata(double.NaN));

    public static double GetAnchor(DependencyObject d) => (double)d.GetValue(AnchorProperty);
    public static void SetAnchor(DependencyObject d, double v) => d.SetValue(AnchorProperty, v);
}

/// <summary>把 (当前值, 中性值, 最小值) 折算成官方填充的 `SelectionStart` / `SelectionEnd`。
/// 为什么必须折算: `PART_SelectionRange` 的宽度 = `(End - Start) × 每单位像素` ——
/// **End &lt; Start 时宽度为负 → 什么都不画** (用户实测: 增益滑块"往左"那一半没画出来 ✗)。
/// 所以这里始终返回 `min(anchor, value)` 当 Start、`max(...)` 当 End, 两端一交换, 两个方向都能画 ✓</summary>
public sealed class SliderAnchorConverter : IMultiValueConverter
{
    /// <summary>values = [Value, Anchor, Minimum]; parameter = "start" | "end"</summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3) return 0.0;
        double value = ToDouble(values[0]);
        double anchor = ToDouble(values[1]);
        double min = ToDouble(values[2]);
        if (double.IsNaN(anchor)) anchor = min;                 // 没给中性值 → 从最小值起
        bool wantStart = (parameter as string) != "end";
        return wantStart ? Math.Min(anchor, value) : Math.Max(anchor, value);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static double ToDouble(object o) => o switch
    {
        double d => d,
        float f => f,
        int i => i,
        _ => 0.0,
    };
}

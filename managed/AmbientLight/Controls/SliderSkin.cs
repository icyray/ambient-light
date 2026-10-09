// SliderSkin.cs — 滑块皮肤的**页面侧声明** (配套 Styles/SliderStyle.xaml, 见 docs/SLIDER_TEMPLATE.md)
//
// 为什么需要这两个附加属性: 模板要按"这个滑块该画哪种填充"分流, 而 WPF-UI 的滑块没有这个开关。
// 三种模式与各自的理由 (四种画法只有一种: WPF 官方的 `PART_SelectionRange`):
//   Track  (默认) —— 从**最小值**到拇指, **贴左边缘**。官方机制左端天生内缩半个拇指宽, 由模板里
//                    那层 Canvas 整体左移半个拇指抵消掉 (见 SliderStyle.xaml 的 ①)。
//   Anchor        —— 从**中性值**向两侧长 (往右=增益, 往左=衰减), 中性值由 `Anchor` 给
//                    (不设则退回 Minimum)。⚠️ 官方机制要求 Start ≤ End, 所以"往左"必须把两端
//                    **交换** → 由 SliderAnchorConverter 折算 (否则宽度为负 = 什么都不画)。
//   None          —— **不画**填充。用于**轨道本身就是色带**的滑块 (纯色拾色器的色调/饱和度:
//                    轨道是一条彩虹/饱和度渐变, 叠一层填充会把渐变盖掉 ✗)。
//
// ⚠️ 第四轮的重要修正: `Fill` 现在**纯声明式** —— 只在 SliderStyle.xaml 的 DataTrigger 里被读,
//    没有任何"C# 去模板里改元素"的代码了。前三轮用 `ApplyFill()` 代码钩子 (Loaded / LayoutUpdated /
//    IsEnabledChanged) 去藏那条填充, 实测在真实页面里根本不生效 (折叠容器里模板都没应用; 主题切换
//    重建重复按钮还会把状态丢掉) → 用户看到的"色带轨道仍被盖"。改法见 docs/SLIDER_TEMPLATE.md §7。
using System;
using System.Globalization;
using System.Windows;
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
    /// <summary>该滑块画哪种填充 (见 <see cref="SliderFill"/>), 默认 <see cref="SliderFill.Track"/>。
    /// 纯声明式: 由 Styles/SliderStyle.xaml 的两个 DataTrigger 消费 (模板里那层 Canvas 的显隐 +
    /// 官方挂点的开关), 这里不需要任何回调。</summary>
    public static readonly DependencyProperty FillProperty =
        DependencyProperty.RegisterAttached("Fill", typeof(SliderFill), typeof(SliderSkin),
            new PropertyMetadata(SliderFill.Track));

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

    internal static double ToDouble(object o) => o switch
    {
        double d => d,
        float f => f,
        int i => i,
        _ => 0.0,
    };
}

/// <summary>拇指宽度 → 左边的负外边距 (`Thickness(-w/2, 0, 0, 0)`)。
/// 用途: 把装 `PART_SelectionRange` 的那层 Canvas 整体左移**半个拇指宽**, 抵消 WPF 内部
/// `Canvas.Left = 拇指宽/2 + 行程` 带来的内缩 —— 这样进度填充才从轨道左沿开始 (贴边) ✓。
/// 用绑定的方式拿宽度 (而不是写死 10px): 上游若改拇指尺寸, 这里自动跟 ✓
/// value = NaN (还没布局) 时返回 0, 不产生负边距。</summary>
public sealed class HalfNegativeThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double w = SliderAnchorConverter.ToDouble(value);
        return double.IsNaN(w) ? default(Thickness) : new Thickness(-w / 2.0, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

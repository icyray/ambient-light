// Controls/AnimatedVisibility.cs — 高度+透明度展开/收起动画附加行为 (M5.1):
// 让设置行/整卡像 CardExpander 一样有弹出/收回过渡。用法:
//   <controls:SettingRow controls:AnimatedVisibility.Expanded="False" ... />
//   AnimatedVisibility.SetExpanded(row, true/false);
//
// 四个 WPF 陷阱 (全部为实测踩坑):
// 1. 依赖属性"同值不触发回调": 默认值必须是 true, XAML 显式写 Expanded="False" 才有
//    true→false 变更、解析期回调才会执行 (默认写 false 的后果: 显式 False 与默认相同 →
//    回调不触发 → 初始化该隐藏的控件全部可见);
// 2. 动画结束后属性回落到本地基准值: Completed 里必须显式写终态 (收起 → Height=0,
//    展开 → Height=NaN), 否则收起的行会跳回基准高度 (→ "重新展示后展不开")。
// 3. **基准 Margin 不能早于 Loaded 捕获**: BAML 属性应用顺序与源码不一致 (M2 老坑),
//    解析期读到/写到的 Margin 都还不是 XAML 声明的值 → 若此时把它当"基准值"记住,
//    展开后下边距永久少 4px (现象: "纯色/固件特效两张卡间距更窄")。
//    故基准 Margin 统一等元素 Loaded (XAML 属性必然就位) 再捕获, 期间只设显隐、不碰 Margin。
// 4. **只读 DP 的默认值不能用 `is Thickness t` 判"未捕获"**: 只读 DP 的默认值就是
//    default(Thickness) = (0,0,0,0), 模式匹配恒为 true → 永远区分不出"还没捕获"。
//    必须用 NaN 哨兵显式标记"未捕获"。
// 另注: Page.Loaded 早于其子元素 Loaded (Loaded 是自顶向下的广播), 所以"页面 Loaded 里
//   调 SetExpanded"这一步一定发生在子元素基准捕获之前 —— 上面第 3/4 条必须同时成立
//   才能覆盖"启动时就是纯色模式"这条路径。
// 自然高不缓存: 每次 Apply 现场独立 Measure (与布局状态无关), 内容变化不会用过期值。
using System.Windows;
using System.Windows.Media.Animation;

namespace AmbientLight.Controls;

public static class AnimatedVisibility
{
    // NaN 哨兵 = "基准 Margin 尚未捕获"
    private static readonly DependencyPropertyKey NaturalMarginKey = DependencyProperty.RegisterAttachedReadOnly(
        "NaturalMargin", typeof(Thickness), typeof(AnimatedVisibility),
        new PropertyMetadata(new Thickness(double.NaN)));
    private static readonly DependencyProperty NaturalMarginProperty = NaturalMarginKey.DependencyProperty;

    private static readonly DependencyPropertyKey HookedKey = DependencyProperty.RegisterAttachedReadOnly(
        "Hooked", typeof(bool), typeof(AnimatedVisibility), new PropertyMetadata(false));
    private static readonly DependencyProperty HookedProperty = HookedKey.DependencyProperty;

    public static readonly DependencyProperty ExpandedProperty = DependencyProperty.RegisterAttached(
        "Expanded", typeof(bool), typeof(AnimatedVisibility),
        new FrameworkPropertyMetadata(true, OnExpandedChanged));

    public static bool GetExpanded(DependencyObject obj) => (bool)obj.GetValue(ExpandedProperty);

    public static void SetExpanded(DependencyObject obj, bool value) => obj.SetValue(ExpandedProperty, value);

    private static void OnExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        bool expand = (bool)e.NewValue;

        if (!HasNaturalMargin(el))
        {
            // 解析期 / Loaded 之前: XAML 属性未必就位 → 只设显隐, **绝不读写 Margin**。
            // 这里一旦动了 Margin, 就会把"默认 0"错认成基准值 (见文件头第 3/4 条)。
            el.Height = expand ? double.NaN : 0;
            el.Opacity = expand ? 1 : 0;
            el.IsHitTestVisible = expand;
            if (!(bool)el.GetValue(HookedProperty))
            {
                el.SetValue(HookedKey, true);
                el.Loaded += OnFirstLoaded;
            }
            return;
        }

        Apply(el, expand, animate: true);
    }

    /// <summary>首次进入可视树: XAML 属性已全部应用 → 此时取基准 Margin 才可靠, 并按当前态重放一次</summary>
    private static void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        el.Loaded -= OnFirstLoaded;
        if (HasNaturalMargin(el)) return;
        el.SetValue(NaturalMarginKey, el.Margin);   // 此刻 el.Margin 一定是 XAML 声明的值 (本行为从未改写过它)
        Apply(el, GetExpanded(el), animate: false);
    }

    private static bool HasNaturalMargin(FrameworkElement el)
        => el.GetValue(NaturalMarginProperty) is Thickness t && !double.IsNaN(t.Top);

    private static void Apply(FrameworkElement el, bool expand, bool animate)
    {
        if (!HasNaturalMargin(el)) return;          // 未捕获前不动作 (防御: 正常路径不会走到)
        var naturalMargin = (Thickness)el.GetValue(NaturalMarginProperty);
        double h = GetNaturalHeight(el);

        if (!animate)
        {
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.BeginAnimation(FrameworkElement.MarginProperty, null);
            el.Height = expand ? double.NaN : 0;
            el.Opacity = expand ? 1 : 0;
            el.IsHitTestVisible = expand;
            el.Margin = expand ? naturalMargin : default(Thickness);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(200);
        double from = expand ? 0 : h;
        double to = expand ? h : 0;
        el.BeginAnimation(FrameworkElement.HeightProperty, null);   // 撤旧动画 (HoldEnd 会钉住终值)
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(FrameworkElement.MarginProperty, null);
        // 基准值一律先写成**终点**: 动画结束后撤销动画即可自然回落到终态
        // (与 banner 的 SetColor 同款; Completed 里再兜一次底)。
        el.Height = to;
        el.Margin = expand ? naturalMargin : default(Thickness);
        if (expand) el.IsHitTestVisible = true;

        var heightAnim = new DoubleAnimation(from, to, dur) { EasingFunction = ease };
        heightAnim.Completed += (_, _) =>
        {
            // FillBehavior 默认 HoldEnd: 动画结束后仍持有终值 → 显式撤除并写本地基准值,
            // 否则收起后 Height 被钉在 0, 下次展开的 Measure 又被钳出 1px (第四坑)
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.BeginAnimation(FrameworkElement.MarginProperty, null);
            if (GetExpanded(el))
            {
                el.Height = double.NaN;          // 还原自适应高度
                el.Opacity = 1;
                el.Margin = naturalMargin;
            }
            else
            {
                el.Height = 0;
                el.Opacity = 0;
                el.Margin = default(Thickness);
                el.IsHitTestVisible = false;
            }
        };
        el.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(expand ? 0 : 1, expand ? 1 : 0, dur) { EasingFunction = ease });
        el.BeginAnimation(FrameworkElement.MarginProperty,
            new ThicknessAnimation(expand ? default(Thickness) : naturalMargin,
                                   expand ? naturalMargin : default(Thickness), dur)
            { EasingFunction = ease });
    }

    /// <summary>
    /// 测自然高。收起态 (Height=0) 下直接 Measure 会得到 DesiredSize=0
    /// (显式 Height 钳制期望尺寸) → 展开动画目标只剩 1px、"展不开" (修复轮踩坑):
    /// 须临时解除固定高再测; 优先用真实布局高度 ActualHeight (展开态下最准)。
    /// </summary>
    private static double GetNaturalHeight(FrameworkElement el)
    {
        if (double.IsNaN(el.Height) && el.ActualHeight > 1)
            return el.ActualHeight;
        double saved = el.Height;
        el.Height = double.NaN;                         // 解除固定高
        double w = el.ActualWidth > 10 ? el.ActualWidth : double.PositiveInfinity;
        el.Measure(new Size(w, double.PositiveInfinity));
        double h = Math.Max(el.DesiredSize.Height, 1);
        el.Height = saved;                              // 还原 (不触发布局, 动画随后覆盖)
        return h;
    }
}

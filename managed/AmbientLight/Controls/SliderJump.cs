// SliderJump.cs — 滑条"指哪打哪"附加行为: 点击轨道任意位置直接跳到点击点,
// 按住跟手拖动, 点在 Thumb 上仍走原生拖动。原理同 M1 主页亮度滑条
// (Track.ValueFromPoint 在 WPF-UI 模板下与实际几何不一致, 用纯几何自算)。
// 用法: <Slider controls:SliderJump.Enabled="True" ... />
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AmbientLight.Controls;

public static class SliderJump
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SliderJump), new PropertyMetadata(false, OnEnabledChanged));

    // 拖动中标志挂在 Slider 自身的附加属性上, 随元素释放, 无静态泄漏
    private static readonly DependencyProperty DragActiveProperty = DependencyProperty.RegisterAttached(
        "DragActive", typeof(bool), typeof(SliderJump), new PropertyMetadata(false));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider s) return;
        if ((bool)e.NewValue)
        {
            s.PreviewMouseLeftButtonDown += OnDown;
            s.PreviewMouseMove += OnMove;
            s.PreviewMouseLeftButtonUp += OnUp;
            s.LostMouseCapture += OnLost;
        }
        else
        {
            s.PreviewMouseLeftButtonDown -= OnDown;
            s.PreviewMouseMove -= OnMove;
            s.PreviewMouseLeftButtonUp -= OnUp;
            s.LostMouseCapture -= OnLost;
        }
    }

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not Slider s) return;
        if (s.Template?.FindName("PART_Track", s) is not Track t ||
            t.Thumb == null || t.Thumb.IsMouseOver)
            return;                                     // 点在滑块上 → 原生 Thumb 拖动
        s.SetValue(DragActiveProperty, true);
        s.CaptureMouse();
        Jump(s, t, e);
        e.Handled = true;                               // 轨道 RepeatButton 不再参与
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (sender is not Slider s || !(bool)s.GetValue(DragActiveProperty)) return;
        if (s.Template?.FindName("PART_Track", s) is Track t)
            Jump(s, t, e);
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not Slider s) return;
        s.SetValue(DragActiveProperty, false);
        if (s.IsMouseCaptured) s.ReleaseMouseCapture();
    }

    private static void OnLost(object sender, MouseEventArgs e)
    {
        if (sender is Slider s)
            s.SetValue(DragActiveProperty, false);      // 捕获意外丢失 (如 alt-tab)
    }

    private static void Jump(Slider s, Track t, MouseEventArgs e)
    {
        var pt = e.GetPosition(t);
        double w = t.Thumb?.ActualWidth ?? 0.0;
        double len = t.ActualWidth;
        if (len <= 0) return;
        double ratio = (pt.X - w / 2.0) / Math.Max(1.0, len - w);
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        s.Value = s.Minimum + ratio * (s.Maximum - s.Minimum);
    }
}

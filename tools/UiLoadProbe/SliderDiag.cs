// SliderDiag.cs — 滑块皮肤**真实页面结构**排查工具 (出图 + 量状态)
//
// 为什么单独一个文件: 滑块的问题只在**真实页面结构**里出现 —— 拾色器的两根滑块在 `ui:CardExpander`
// (默认折叠) 里, 亮度行在 `SettingRow`(CardControl) 里且会被整行禁用。
//
// 关键事实 (WPF-UI 4.3.0 的 `Expander.xaml`, 已核对源码):
//   `<Border x:Name="ContentPresenterBorder" ... Visibility="Collapsed">` —— 折叠时内容是 **Collapsed**,
//   根本不参与测量 ⇒ 里面的 Slider 连**模板都不会被应用**。所以任何"等 Loaded / 等 LayoutUpdated
//   再去模板里改元素"的代码, 在真实页面里都可能一次都不生效 (第四轮就是靠这里量出来的 ✗)。
//   第四轮起填充改成**纯声明式** (模板里的 DataTrigger 收/放那层 Canvas), 与时序无关。
//
// 本工具做两件事:
//   ① 打印真实页面里 HueSlider 在"折叠 → 模拟 Loaded → 展开布局"三个阶段的状态 (模板/填充层);
//   ② 把真实页面里的**亮度行**(可用/禁用)、**亮度滑块本体**(6x)与**拾色器**出图, 并沿线扫像素。
//      —— 观感问题必须看图, 数值断言全绿照样能错 (前三轮的教训)。
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UiLoadProbe;

internal static class SliderDiag
{
    public static void Dump(string pathPrefix)
    {
        var app = new AmbientLight.App();
        app.InitializeComponent();
        typeof(AmbientLight.App)
            .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, app);

        var home = new AmbientLight.Views.HomePage();
        var hue = (Slider)home.FindName("HueSlider")!;
        var sat = (Slider)home.FindName("SatSlider")!;
        var solid = (FrameworkElement)home.FindName("SolidCard")!;
        var brightRow = (FrameworkElement)home.FindName("BrightnessRow")!;
        var brightSlider = (Slider)home.FindName("BrightnessSlider")!;

        var t = solid.GetType();
        Console.WriteLine($"[0] SolidCard 类型链: {t.FullName} -> {t.BaseType?.FullName} -> {t.BaseType?.BaseType?.FullName}");

        // ⚠️ Page 只能挂在 Window/Frame 下 → 把页面的**内容根**摘出来再托管 (名字引用已在上方取好)
        var root = (FrameworkElement)home.Content;
        home.Content = null;
        var host = new ContentControl { Content = root, Width = 880 };
        host.Measure(new Size(880, 1600));
        host.Arrange(new Rect(0, 0, 880, 1600));
        host.UpdateLayout();

        Console.WriteLine($"[1] 折叠态      : Template={(hue.Template is null ? "null" : "有")}" +
                          $" IsLoaded={hue.IsLoaded} IsVisible={hue.IsVisible} 填充层={Describe(RangeHostOf(hue))}");
        RaiseLoaded(root);   // 探针没有 PresentationSource → Loaded 不会自己来
        Console.WriteLine($"    模拟 Loaded 后: 填充层={Describe(RangeHostOf(hue))}");

        // 展开 SolidCard: 无头宿主里动画不会走 → 手动把内容层置为 Visible (等价于动画走完的稳态)
        t.GetProperty("IsExpanded")?.SetValue(solid, true);
        if (solid is Control sc && sc.Template?.FindName("ContentPresenterBorder", solid) is UIElement cpb)
            cpb.Visibility = Visibility.Visible;
        else
            Console.WriteLine("    ⚠️ 没找到 ContentPresenterBorder —— 模板结构可能变了");
        // 页面代码在纯色模式下还会把整卡 AnimatedVisibility 打开 (Height=NaN/Opacity=1)
        AmbientLight.Controls.AnimatedVisibility.SetExpanded(solid, true);
        host.UpdateLayout();
        Console.WriteLine($"[2] 展开+布局后: Template={(hue.Template is null ? "null" : "有")} · " +
                          $"Hue 填充层={Describe(RangeHostOf(hue))} (期望 Collapsed) · " +
                          $"Sat 填充层={Describe(RangeHostOf(sat))} (期望 Collapsed)");
        Scan(hue, "拾色器-色相");
        Scan(sat, "拾色器-饱和度");

        // 亮度行: 可用 / 禁用 (用户报「禁用态缺一截」)
        host.UpdateLayout();
        Console.WriteLine($"[3] 亮度行可用: Slider.IsEnabled={brightSlider.IsEnabled} 填充层={Describe(RangeHostOf(brightSlider))}");
        brightRow.IsEnabled = false;
        host.UpdateLayout();
        Console.WriteLine($"[3] 亮度行禁用: Slider.IsEnabled={brightSlider.IsEnabled} 填充层={Describe(RangeHostOf(brightSlider))}" +
                          $" (期望 Collapsed)");

        // ---- 出图 + 沿线扫像素 ----
        ShotInPlace(brightRow, $"{pathPrefix}_bright_disabled.png", "亮度行 (禁用)");
        ShotInPlace(brightSlider, $"{pathPrefix}_slider_disabled.png", "亮度滑块本体 (禁用, 6x)", 6);
        Scan(brightSlider, "禁用");
        brightRow.IsEnabled = true;
        host.UpdateLayout();
        ShotInPlace(brightRow, $"{pathPrefix}_bright_enabled.png", "亮度行 (可用)");
        ShotInPlace(brightSlider, $"{pathPrefix}_slider_enabled.png", "亮度滑块本体 (可用, 6x)", 6);
        Scan(brightSlider, "可用");
        // 拾色器出图: 把折叠容器里的内容摘出来单独布局后再画 —— 卡片本体的展开动画在无头宿主里
        // 不会推进 (Opacity 停在 0), 直接画整卡会是一片空白 ✗; 内容自身的布局与它无关 ✓
        ShotContent(solid, $"{pathPrefix}_picker.png", "拾色器内容 (H/S 色带轨道, 应完全露出来)");
    }

    private static FrameworkElement? RangeHostOf(Slider sl)
        => sl.Template?.FindName("SelectionRangeHost", sl) as FrameworkElement;
    private static FrameworkElement? PartOf(Slider sl)
        => sl.Template?.FindName("PART_SelectionRange", sl) as FrameworkElement;

    private static string Describe(UIElement? e)
        => e is null ? "<模板还没应用, 找不到那层 Canvas>"
                     : $"{e.Visibility} ({(e as FrameworkElement)?.ActualWidth:F0}x{(e as FrameworkElement)?.ActualHeight:F0})";

    private static void RaiseLoaded(DependencyObject d)
    {
        if (d is FrameworkElement fe) fe.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) RaiseLoaded(VisualTreeHelper.GetChild(d, i));
    }

    /// <summary>沿元素中线扫一遍像素并打印颜色分段。⚠️ 必须走 VisualBrush 重绘:
    /// 直接 `rtb.Render(元素)` 会带上它在父容器里的偏移 (第一版就是这么被坑的 —— 量到的
    /// "左边一截透明"其实是渲染偏移, 不是真的少画了)。</summary>
    private static void Scan(FrameworkElement el, string label)
    {
        try
        {
            int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
            if (w < 2 || h < 2) { Console.WriteLine($"    ({label}) 未布局, 跳过像素扫描"); return; }
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None,
                                                       AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                                 null, new Rect(0, 0, w, h));
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            int stride = w * 4;
            var px = new byte[h * stride];
            rtb.CopyPixels(px, stride, 0);
            // 逐行找"最不透明"的那一行 (轨道只有 4px, 中线未必落在轨道上)
            int bestY = 0, bestN = -1;
            for (int y = 0; y < h; y++)
            {
                int n = 0;
                for (int x = 0; x < w; x++) if (px[y * stride + x * 4 + 3] > 8) n++;
                if (n > bestN) { bestN = n; bestY = y; }
            }
            var sb = new StringBuilder();
            uint last = 0; int runStart = 0;
            for (int x = 0; x < w; x++)
            {
                int o = bestY * stride + x * 4;
                uint c = (uint)(px[o] | (px[o + 1] << 8) | (px[o + 2] << 16) | (px[o + 3] << 24));
                if (x == 0) { last = c; runStart = 0; continue; }
                if (c != last) { sb.Append($"[{runStart}..{x - 1}]{Hex(last)} "); last = c; runStart = x; }
            }
            sb.Append($"[{runStart}..{w - 1}]{Hex(last)}");
            Console.WriteLine($"    ({label}) 中线 y={bestY} 像素分段: {sb}");
        }
        catch (Exception ex) { Console.WriteLine($"    ({label}) 像素扫描失败: {ex.Message}"); }
    }

    private static string Hex(uint c) => $"#{c >> 24:X2}{(c >> 16) & 0xFF:X2}{(c >> 8) & 0xFF:X2}{c & 0xFF:X2}";

    /// <summary>把"已经在树里、已经布局好"的元素原样画到深色底上 (不改动它的父容器)。</summary>
    private static void ShotInPlace(FrameworkElement el, string file, string caption, double zoom = 2)
    {
        double w = el.ActualWidth, h = el.ActualHeight;
        if (w < 2 || h < 2) { Console.WriteLine($"    跳过 {caption}: 元素尺寸 {w:F0}x{h:F0}"); return; }
        const double pad = 10;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)), null, new Rect(0, 0, w + pad * 2, h + pad * 2));
            dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                             null, new Rect(pad, pad, w, h));
        }
        Save(dv, w + pad * 2, h + pad * 2, zoom, file, caption);
    }

    /// <summary>把折叠容器里的内容摘出来单独布局后再画 (折叠时内容不参与测量, 量不出尺寸)。</summary>
    private static void ShotContent(FrameworkElement owner, string file, string caption)
    {
        try
        {
            var content = (FrameworkElement)owner.GetType().GetProperty("Content")!.GetValue(owner)!;
            owner.SetValue(ContentControl.ContentProperty, null);
            var host = new ContentControl { Content = content, Width = 700 };
            host.Measure(new Size(700, 600));
            host.Arrange(new Rect(0, 0, 700, 600));
            host.UpdateLayout();
            double w = content.ActualWidth > 2 ? content.ActualWidth : 700;
            double h = content.ActualHeight > 2 ? content.ActualHeight : 200;
            const double zoom = 2, pad = 10;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)), null,
                                 new Rect(0, 0, w + pad * 2, h + pad * 2));
                dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                                 null, new Rect(pad, pad, w, h));
            }
            Save(dv, w + pad * 2, h + pad * 2, zoom, file, caption);
        }
        catch (Exception ex) { Console.WriteLine($"    跳过 {caption}: {ex.Message}"); }
    }

    private static void Save(Visual v, double w, double h, double zoom, string file, string caption)
    {
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * zoom), (int)Math.Ceiling(h * zoom),
                                         96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        rtb.Render(v);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
        Console.WriteLine($"    dumped -> {file} ({rtb.PixelWidth}x{rtb.PixelHeight}) · {caption}");
    }
}

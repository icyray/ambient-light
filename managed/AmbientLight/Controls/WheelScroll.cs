// WheelScroll.cs — NavigationView 页面滚轮兜底。
// WPF-UI 的 NavigationView 模板用自带 DynamicScrollViewer 包住 Frame 内容, 且 Frame
// 以无限高度测量页面 → 页内自放 ScrollViewer 会被撑成内容高度 (ScrollableHeight=0)
// 永远滚不动, 外层的 DynamicScrollViewer 又不响应滚轮。
// 方案: 页面根不再放 ScrollViewer, 由本行为在 Page.PreviewMouseWheel (隧道事件,
// 先于一切子元素) 里直接驱动最近的外层 ScrollViewer。
// 用法: 页面构造函数里 Controls.WheelScroll.Attach(this);
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AmbientLight.Controls;

public static class WheelScroll
{
    /// <summary>挂接到页面: 滚轮事件驱动外层 ScrollViewer (WPF-UI NavigationView 自带)</summary>
    public static void Attach(Page page)
    {
        page.PreviewMouseWheel += OnPreviewWheel;
    }

    private static void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Page page) return;
        var sv = FindOuterScrollViewer(page);
        if (sv == null) return;                       // 找不到外层滚动器 (独立窗口宿主等)
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);   // Delta: 上滚 +120
        e.Handled = true;
    }

    internal static ScrollViewer? FindOuterScrollViewer(DependencyObject start)
    {
        DependencyObject d = start;
        while ((d = VisualTreeHelper.GetParent(d)) != null)
        {
            if (d is ScrollViewer sv) return sv;      // WPF-UI DynamicScrollViewer 派生自 ScrollViewer
        }
        return null;
    }
}

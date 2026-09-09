// LedRingView.xaml.cs — 灯带环绕示意图 (PoC 终端 --diagram 的 GUI 版)
// 布局与核心 capture.cpp BuildRects 完全同构: 相同的边数分配公式与绕向段序 (真实长宽比),
// 保证每颗椭圆的位置 = 该 LED 的物理位置。中央暗色矩形 = 屏幕本体, LED 均匀环绕其四周;
// 画布尺寸 = 屏幕矩形 + 环绕带 (固定值), 在控件内居中。颜色来自 ACStatus.ledRgb。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AmbientLight.Controls;

public partial class LedRingView : UserControl
{
    private List<Ellipse> _top = [], _bottom = [], _left = [], _right = [];
    private long _signature = -1;
    private double _aspect = 16.0 / 9.0;

    // 布局预算 (固定): 屏幕矩形最大外接尺寸 + LED 与矩形边缘的间隙
    private const double MaxScreenW = 460, MaxScreenH = 220, LedGap = 14;

    public LedRingView() => InitializeComponent();

    /// <summary>中心区提示副标题 (如 "MCU 特效由固件渲染")</summary>
    public void SetHint(string text) => HintText.Text = text;

    /// <summary>重建布局 (签名相同则跳过)。screenW/H = 目标显示器实际物理分辨率
    /// (0,0 = 按 16:9 缺省): 屏幕矩形按真实长宽比绘制, 每边 LED 数按同一比例分配
    /// (与核心一致), LED 尺寸取各边跨度均分的最小值 (均匀环绕), 画布尺寸固定。</summary>
    public void Configure(int ledCount, int sides, int direction, int rotate = 0,
                          int screenW = 0, int screenH = 0)
    {
        double aspect = screenW > 0 && screenH > 0 ? (double)screenW / screenH : 16.0 / 9.0;
        long sig = ledCount | ((long)sides << 12) | ((long)direction << 16)
                 | (((long)Math.Clamp(rotate, -255, 255) + 256) << 18)
                 | ((long)Math.Round(aspect * 100) << 30);
        if (sig == _signature) return;
        _signature = sig;
        _aspect = aspect;
        Rebuild(ledCount, sides, direction, rotate);
    }

    private void Rebuild(int leds, int sides, int direction, int rotate)
    {
        // ---- 边数分配 (capture.cpp ComputeCounts, 真实长宽比) ----
        int horiz, vert;
        if (sides == 4)
        {
            double wShare = _aspect / (1.0 + _aspect);
            horiz = (int)Math.Round(wShare * leds / 2.0);
            vert = (int)Math.Round((1.0 - wShare) * leds / 2.0);
        }
        else if (sides == 3)
        {
            horiz = (int)Math.Round(leds * _aspect / (_aspect + 2.0));
            vert = (leds - horiz) / 2;
        }
        else
        {
            vert = leds / 2; horiz = 0;
        }

        // ---- 屏幕矩形 (真实比例, 预算内最大) + 均匀环绕的 LED 尺寸 ----
        double sw, sh;
        if (_aspect >= MaxScreenW / MaxScreenH) { sw = MaxScreenW; sh = MaxScreenW / _aspect; }
        else { sh = MaxScreenH; sw = MaxScreenH * _aspect; }
        double spanH = horiz > 0 ? sw / horiz : double.MaxValue;
        double spanV = vert > 0 ? sh / vert : double.MaxValue;
        double cell = Math.Clamp(Math.Min(spanH, spanV), 3.0, 12.0);
        double pad = LedGap + cell;                  // 矩形边到画布边 (间隙 + 一颗灯)
        double cw = sw + 2 * pad, ch = sh + 2 * pad;

        _top = []; _bottom = []; _left = []; _right = [];
        RingCanvas.Children.Clear();
        RingCanvas.Width = cw;
        RingCanvas.Height = ch;
        RingBox.MaxWidth = cw;                       // 原大上限; 窗口更窄时 Viewbox 等比缩小
        RingBox.MaxHeight = ch;

        var screen = new Rectangle
        {
            Width = sw,
            Height = sh,
            RadiusX = 6,
            RadiusY = 6,
            Fill = Frozen(16, 16, 22),
            Stroke = Frozen(46, 46, 56),
            StrokeThickness = 1,
        };
        Canvas.SetLeft(screen, pad);
        Canvas.SetTop(screen, pad);
        RingCanvas.Children.Add(screen);

        // ---- LED 物理位置 (归一化 u,v ∈ [0,1]; 段序/绕向/rotate 与核心同构) ----
        var pts = new List<(int idx, double u, double v)>(leds);
        void HSeg(int count, bool bottom, bool l2r)
        {
            for (int i = 0; i < count; i++)
            {
                int k = l2r ? i : (count - 1 - i);
                pts.Add((pts.Count, (k + 0.5) / count, bottom ? 1.0 : 0.0));
            }
        }
        void VSeg(int count, bool left, bool b2t)
        {
            for (int i = 0; i < count; i++)
            {
                int k = b2t ? (count - 1 - i) : i;
                pts.Add((pts.Count, left ? 0.0 : 1.0, (k + 0.5) / count));
            }
        }
        if (direction == 0)
        {
            VSeg(vert, false, true); HSeg(horiz, false, false);
            VSeg(vert, true, false); HSeg(horiz, true, true);
        }
        else
        {
            VSeg(vert, true, true); HSeg(horiz, false, true);
            VSeg(vert, false, false); HSeg(horiz, true, false);
        }
        if (pts.Count > leds) pts.RemoveRange(leds, pts.Count - leds);
        if (rotate != 0 && leds > 0)
        {
            var rotated = new List<(int idx, double u, double v)>(leds);
            for (int i = 0; i < leds; i++)
            {
                int j = (i + rotate) % leds;
                if (j < 0) j += leds;
                rotated.Add((i, pts[j].u, pts[j].v));
            }
            pts = rotated;
        }

        foreach (var (idx, u, v) in pts)
        {
            // 灯珠中心在矩形边缘外侧: 间隙 LedGap + 半径 cell/2
            double off = LedGap + cell / 2;
            double cx = pad + u * sw + (u == 0 ? -off : u == 1 ? off : 0);
            double cy = pad + v * sh + (v == 0 ? -off : v == 1 ? off : 0);
            var e = new Ellipse { Width = cell, Height = cell, Fill = Frozen(30, 30, 34), Tag = idx };
            Canvas.SetLeft(e, cx - cell / 2);
            Canvas.SetTop(e, cy - cell / 2);
            RingCanvas.Children.Add(e);
            if (v == 0) _top.Add(e);
            else if (v == 1) _bottom.Add(e);
            else if (u == 0) _left.Add(e);
            else _right.Add(e);
        }
        // 物理序排列 (ShowQuadrantMap 按序号位置的象限判定依赖此序)
        _top.Sort((a, b) => Canvas.GetLeft(a).CompareTo(Canvas.GetLeft(b)));
        _bottom.Sort((a, b) => Canvas.GetLeft(a).CompareTo(Canvas.GetLeft(b)));
        _left.Sort((a, b) => Canvas.GetTop(a).CompareTo(Canvas.GetTop(b)));
        _right.Sort((a, b) => Canvas.GetTop(a).CompareTo(Canvas.GetTop(b)));
    }

    /// <summary>当前布局下各物理边的 LED 序号范围 (灯数核对用), 如 "顶 0-19 · 右 20-29 …"。
    /// 空边跳过; 仅在 Configure 之后有意义。</summary>
    public string DescribeIndices()
    {
        static string Range(List<Ellipse> strip, string name)
        {
            if (strip.Count == 0) return null;
            var idx = strip.Select(e => (int)e.Tag).ToList();
            return $"{name} {idx.Min()}-{idx.Max()}";
        }
        var parts = new[] {
            (Range(_top, "顶"), 1), (Range(_right, "右"), 2),
            (Range(_bottom, "底"), 3), (Range(_left, "左"), 4),
        }.Where(p => p.Item1 != null).OrderBy(p => p.Item2).Select(p => p.Item1);
        return string.Join(" · ", parts);
    }

    /// <summary>按物理位置刷新颜色; rgb 为 543B 线帧 payload (RGB 序) 的前段</summary>
    public void Update(ReadOnlySpan<byte> rgb, int ledCount)
    {
        Fill(_top, rgb, ledCount);
        Fill(_bottom, rgb, ledCount);
        Fill(_left, rgb, ledCount);
        Fill(_right, rgb, ledCount);
    }

    /// <summary>全部置灰 (无数据/MCU 模式)</summary>
    public void Clear()
    {
        var off = Frozen(30, 30, 34);
        foreach (var e in _top.Concat(_bottom).Concat(_left).Concat(_right))
            e.Fill = off;
    }

    /// <summary>以当前布局画"标准四象限图" (不随灯序偏移): 每格按其物理位置所在象限着色,
    /// quad = [左上,右上,左下,右下]。校准窗的固定参照 —— 物理灯带偏移时此图不动,
    /// 用户将灯带颜色分界对齐屏幕象限分界即可。</summary>
    public void ShowQuadrantMap(Color[] quad)
    {
        if (quad == null || quad.Length < 4) return;
        var brushes = quad.Take(4).Select(c =>
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }).ToArray();
        Paint(_top, k => (k + 0.5) / Math.Max(_top.Count, 1) < 0.5 ? 0 : 1);
        Paint(_bottom, k => (k + 0.5) / Math.Max(_bottom.Count, 1) < 0.5 ? 2 : 3);
        Paint(_left, k => (k + 0.5) / Math.Max(_left.Count, 1) < 0.5 ? 0 : 2);
        Paint(_right, k => (k + 0.5) / Math.Max(_right.Count, 1) < 0.5 ? 1 : 3);

        void Paint(List<Ellipse> strip, Func<int, int> quadOf)
        {
            for (int k = 0; k < strip.Count; k++) strip[k].Fill = brushes[quadOf(k)];
        }
    }

    private static void Fill(List<Ellipse> strip, ReadOnlySpan<byte> rgb, int ledCount)
    {
        foreach (var e in strip)
        {
            int i = (int)e.Tag;
            if (i < ledCount && (i * 3 + 2) < rgb.Length)
            {
                byte r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                e.Fill = Frozen(r, g, b);
            }
            else
            {
                e.Fill = Frozen(30, 30, 34);
            }
        }
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

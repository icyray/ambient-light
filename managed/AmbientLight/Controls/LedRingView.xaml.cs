// LedRingView.xaml.cs — 灯带环形示意图 (PoC 终端 --diagram 的 GUI 版)
// 布局与核心 capture.cpp BuildRects 完全同构: 相同的边数分配公式与绕向段序,
// 保证图中每颗椭圆的位置 = 该 LED 的物理位置。颜色来自 ACStatus.ledRgb。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AmbientLight.Controls;

public partial class LedRingView : UserControl
{
    private List<Ellipse> _top = [], _bottom = [], _left = [], _right = [];
    private int _signature = -1;
    private int _cfgLeds, _cfgSides, _cfgDir;   // ShowQuadrantMap 复算几何用

    public LedRingView() => InitializeComponent();

    /// <summary>中心区提示副标题 (如 "MCU 特效由固件渲染")</summary>
    public void SetHint(string text) => HintText.Text = text;

    /// <summary>布局变化 (灯数/边数/方向/灯序偏移) 时重建椭圆; 签名相同则跳过。
    /// 椭圆尺寸同时受横向 (540px 预算) 与纵向 (340px 预算) 约束, 组件高度按
    /// 纵向实际需要自适应 —— 固定高度会让左右列被截断。</summary>
    public void Configure(int ledCount, int sides, int direction, int rotate = 0)
    {
        int sig = ledCount | (sides << 10) | (direction << 14) | ((Math.Clamp(rotate, -255, 255) + 255) << 16);
        if (sig == _signature) return;
        _signature = sig;
        _cfgLeds = ledCount; _cfgSides = sides; _cfgDir = direction;

        var (top, bottom, left, right, maxPerSide) = BuildLayout(ledCount, sides, direction, rotate);
        int maxVert = Math.Max(Math.Max(left.Count, right.Count), 1);
        double cell = Math.Clamp(
            Math.Min(540.0 / Math.Max(maxPerSide, 1), 340.0 / (maxVert + 2)) - 3.0, 3.0, 14.0);
        _top = MakeStrip(top, cell - 3.0);
        _bottom = MakeStrip(bottom, cell - 3.0);
        _left = MakeStrip(left, cell - 3.0);
        _right = MakeStrip(right, cell - 3.0);
        TopStrip.ItemsSource = _top;
        BottomStrip.ItemsSource = _bottom;
        LeftStrip.ItemsSource = _left;
        RightStrip.ItemsSource = _right;
        // 高度 = 左右列 (maxVert 颗) + 顶/底行各 1 颗的格位 + 余量
        Height = Math.Clamp((maxVert + 2) * cell + 10, 150, 400);
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

    /// <summary>当前布局下各物理边的 LED 序号范围 (灯数核对用), 如 "右 0-19 · 顶 20-39 …"。
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
            (Range(_top, "顶"), 1), (Range(_bottom, "底"), 2),
            (Range(_left, "左"), 3), (Range(_right, "右"), 4),
        }.Where(p => p.Item1 != null).OrderBy(p => p.Item2).Select(p => p.Item1);
        return string.Join(" · ", parts);
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
        const double W = 16, H = 9;
        Paint(_top, k => (k + 0.5) * W / Math.Max(_top.Count, 1) < W / 2 ? 0 : 1);
        Paint(_bottom, k => (k + 0.5) * W / Math.Max(_bottom.Count, 1) < W / 2 ? 2 : 3);
        Paint(_left, k => (k + 0.5) * H / Math.Max(_left.Count, 1) < H / 2 ? 0 : 2);
        Paint(_right, k => (k + 0.5) * H / Math.Max(_right.Count, 1) < H / 2 ? 1 : 3);

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

    private static List<Ellipse> MakeStrip(List<int> indices, double size)
    {
        var list = new List<Ellipse>(indices.Count);
        foreach (var i in indices)
        {
            double s = Math.Max(2.5, size);
            list.Add(new Ellipse
            {
                Width = s,
                Height = s,
                Margin = new Thickness(1.5),
                Fill = Frozen(30, 30, 34),
                Tag = i,
            });
        }
        return list;
    }

    // ---- 布局复刻 (capture.cpp BuildRects, 虚拟 16:9 屏) -------------------
    // dir=0: 右(B→T) → 顶(R→L) → 左(T→B) → 底(L→R), #0 = 右下角
    // dir=1: 左(B→T) → 顶(L→R) → 右(T→B) → 底(R→L), #0 = 左下角
    // rotate: 灯序偏移 (capture.cpp 同构): LED i 的位置 = 段 (i+rotate) mod N
    // sides=3 时核心通过 rects.resize(leds) 截掉尾段 (无底边)
    private static (List<int> top, List<int> bottom, List<int> left, List<int> right, int maxPerSide)
        BuildLayout(int leds, int sides, int direction, int rotate)
    {
        const double W = 16, H = 9;
        int horiz, vert;
        if (sides == 4)
        {
            double wShare = W / (W + H);
            horiz = (int)Math.Round(wShare * leds / 2.0);
            vert = (int)Math.Round((1.0 - wShare) * leds / 2.0);
        }
        else if (sides == 3)
        {
            horiz = (int)Math.Round(leds * W / (W + 2.0 * H));
            vert = (leds - horiz) / 2;
        }
        else
        {
            vert = leds / 2; horiz = 0;
        }

        // (index, cx, cy): index = 段内生成序 = LED 序号
        var pts = new List<(int idx, double cx, double cy)>(leds);
        void HSeg(int count, bool bottom, bool l2r)
        {
            for (int i = 0; i < count; i++)
            {
                int k = l2r ? i : (count - 1 - i);
                pts.Add((pts.Count, (k + 0.5) * W / count, bottom ? H : 0));
            }
        }
        void VSeg(int count, bool left, bool b2t)
        {
            for (int i = 0; i < count; i++)
            {
                int k = b2t ? (count - 1 - i) : i;
                pts.Add((pts.Count, left ? 0 : W, (k + 0.5) * H / count));
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

        // 灯序偏移: LED i 显示段 (i+rotate) mod N → 位置表按此重排 (与核心一致)
        if (rotate != 0 && leds > 0)
        {
            var rotated = new List<(int idx, double cx, double cy)>(leds);
            for (int i = 0; i < leds; i++)
            {
                int j = (i + rotate) % leds;
                if (j < 0) j += leds;
                rotated.Add((i, pts[j].cx, pts[j].cy));
            }
            pts = rotated;
        }

        // 显示序 = 物理序: 顶/底左→右, 左/右顶→底 (与绕向无关, 按 cx/cy 排)
        var top = pts.Where(p => p.cy == 0).OrderBy(p => p.cx).Select(p => p.idx).ToList();
        var bottom = pts.Where(p => p.cy == H).OrderBy(p => p.cx).Select(p => p.idx).ToList();
        var left = pts.Where(p => p.cx == 0 && p.cy > 0 && p.cy < H).OrderBy(p => p.cy).Select(p => p.idx).ToList();
        var right = pts.Where(p => p.cx == W && p.cy > 0 && p.cy < H).OrderBy(p => p.cy).Select(p => p.idx).ToList();
        int maxPerSide = Math.Max(Math.Max(top.Count, bottom.Count), Math.Max(left.Count, right.Count));
        return (top, bottom, left, right, maxPerSide);
    }
}

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
    private Rectangle? _screen;                 // 屏幕矩形 (随 Surface 换填充/边框)
    private long _signature = -1;
    private double _aspect = 16.0 / 9.0;

    /// <summary>这个控件被渲染在**什么面上** —— 决定整支外观（屏幕矩形 + 中心文字 + 灯珠描边）。
    ///   · `Theme`（默认，主页「布局预览」）: 跟着主题 —— 浅色主题下矩形也是浅的；
    ///   · `AlwaysDark`（校准窗）: 那块面板恒深（校色时不能被主题影响），整套外观也恒深。
    /// 为什么要这个开关: 控件同时用在两个底色完全不同的地方，而"跟底色拉开对比"这件事
    /// **必须知道底色**（25% 黑在深底上看不见、25% 白在浅底上看不见 —— 与 banner 辉光同理）。
    /// 一个布尔驱动**一份调色板**（见 `SurfaceColors`），调用点不必逐个元素分主题写 if。</summary>
    public enum RingSurface { Theme, AlwaysDark }

    private RingSurface _surface = RingSurface.Theme;
    public RingSurface Surface
    {
        get => _surface;
        set { _surface = value; ApplySurface(); }
    }

    /// <summary>整套外观的调色板 —— **纯函数**（探针用它算对比度）。
    /// 主题色作为入参传进来（纯函数读不到资源字典）。
    ///   · 深面: 屏幕 `#101016`（比卡片更暗 → 读作"熄灭的屏")+ 深灰描边 `#2E2E38`；
    ///   · 浅面: 屏幕 `#F5F5F5` + 浅灰描边 `#D5DDE3` —— 浅色主题下卡片本身接近白,
    ///     光靠填充色差（ΔL 0.0006）看不出"这是一块屏幕", 所以边框必须撑住识别度
    ///     （探针有一条断言守着"矩形边框在矩形底上可见"）。
    /// 灯珠描边: 深面 30% 白 / 浅面 20% 黑 —— 目的是"两颗颜色接近的圆之间留一道缝",
    /// 深面给得更足, 因为最糊的场景是校准窗（未点亮灯珠 #1E1E22 与面板 #141418 只差 0.007）。</summary>
    public static (Color RectFill, Color RectStroke, Color Title, Color Hint, Color Outline)
        SurfaceColors(bool dark, Color themePrimary, Color themeSecondary)
        => dark
            ? (Color.FromRgb(0x10, 0x10, 0x16), Color.FromRgb(0x2E, 0x2E, 0x38),
               Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF),
               Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF))
            : (Color.FromRgb(0xF5, 0xF5, 0xF5), Color.FromRgb(0xD5, 0xDD, 0xE3),
               themePrimary, themeSecondary,
               Color.FromArgb(0x33, 0x00, 0x00, 0x00));

    /// <summary>当前是否按"深面"渲染（AlwaysDark 或 主题本来就是深色）。</summary>
    private bool IsDarkSurface => _surface == RingSurface.AlwaysDark || Services.ThemeService.IsDark;

    /// <summary>按当前面重画整个控件的外观: 屏幕矩形 (填充/边框) + 中心文字 + 灯珠描边。
    /// Surface 变化、换主题、以及每次 Rebuild 之后都要调一次。</summary>
    private void ApplySurface()
    {
        bool dark = IsDarkSurface;
        var c = SurfaceColors(dark, ThemeBrushColor("TextFillColorPrimaryBrush", Colors.Black),
                                   ThemeBrushColor("TextFillColorSecondaryBrush", Colors.Gray));
        if (_screen is not null)
        {
            _screen.Fill = Frozen(c.RectFill);
            _screen.Stroke = Frozen(c.RectStroke);
        }
        HintTitle.Foreground = Frozen(c.Title);
        HintText.Foreground = Frozen(c.Hint);
        var outline = Frozen(c.Outline);
        foreach (var strip in new[] { _top, _bottom, _left, _right })
            foreach (var e in strip) e.Stroke = outline;
    }

    /// <summary>取主题色资源 (取不到就用兜底色) —— 供 SurfaceColors 的浅面用。</summary>
    private static Color ThemeBrushColor(string key, Color fallback)
        => Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    /// <summary>几何字符串里的坐标格式化 (定点 1 位, 用 InvariantCulture 免得被本地小数点坑)。</summary>
    private static string F(double v) => v.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // 布局预算 (固定): 屏幕矩形最大外接尺寸 + LED 与矩形边缘的间隙
    private const double MaxScreenW = 460, MaxScreenH = 220, LedGap = 8;

    public LedRingView()
    {
        InitializeComponent();
        ApplySurface();      // 先给文字上色: 首次 Configure 之前(还没有灯珠/矩形)也不能是"没设 Foreground 的黑字"
        // Surface=Theme 时要跟着主题变 (校准窗是 AlwaysDark, 与主题无关, 不必跟)
        Loaded += (_, _) => Services.ThemeService.AppearanceChanged += OnAppearanceChanged;
        Unloaded += (_, _) => Services.ThemeService.AppearanceChanged -= OnAppearanceChanged;
    }

    private void OnAppearanceChanged()
    {
        if (_surface == RingSurface.Theme) ApplySurface();
    }

    /// <summary>中心区提示副标题 (如 "MCU 特效由固件渲染" / 主页的静态几何参数)。
    /// **空串要把整行收起** —— 否则底衬里会多留一行高度 (校准窗只显示标题时就看得出来)。
    /// 初始状态在 XAML 里也是 Collapsed (没人调过 SetHint 的宿主同样不该留空行)。</summary>
    public void SetHint(string text)
    {
        string s = text ?? "";
        HintText.Text = s;
        HintText.Visibility = string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>校准向导中心区 (M5.1 §7): 0 号灯起点徽标 + 绕向箭头。
    /// 主页不显示 —— 那里的中心只放静态几何参数 (展开"布局预览"的人要的是几何事实)。
    /// 绕向与核心 capture.cpp / 本控件的段序一致: direction 0 = 逆时针。</summary>
    // ================= 校准向导的"安装示意"(起点角 + 绕向) =================
    // 用户 2026-10-08 设计稿: 旧版那一行小字("起点 0 号" + "↺ 逆时针")两个信息都有问题 ——
    //   ① "0 号"没有信息量: 起点恒为 0 号灯; ② 文字说不清"灯带怎么绕"。
    // 改成: 在**屏幕矩形内**画一圈环线 + 一个箭头表示绕向, 并把"起点"徽标贴在 0 号灯那个**角**上。

    /// <summary>起点 (0 号灯) 所在的角 —— **纯函数**, 与核心的段序同构 (探针打真值表)。
    /// `direction 0`(逆时针) 的段序是「右边缘自下而上 → 顶边右→左 → 左边缘自上而下 → 底边左→右」,
    /// 所以 0 号灯落在**右下角**; `direction 1`(顺时针) 是它关于竖直中轴的镜像 → **左下角**。
    /// (rotate 恒为 0: 两处 Configure 都传 0 —— 校准窗刻意显示"标准图", 灯序偏移只在下方的文字里体现。
    ///  若将来把偏移接进预览, 起点的角要跟着 shift 一起算。)</summary>
    public enum RingStartCorner { BottomLeft, BottomRight }

    public static RingStartCorner StartCornerOf(int direction)
        => direction == 0 ? RingStartCorner.BottomRight : RingStartCorner.BottomLeft;

    /// <summary>顺时针 = 逆时针关于竖直中轴的镜像 (纯函数) —— 用来选"镜像那一套"的几何
    /// (断口在左下 / 箭头朝左)。注意这里只是**选择变体**, 不靠 RenderTransform 去翻 ——
    /// RenderTransform 按元素自身原点解释, 拿它做"关于画布中心镜像"会把元素平移跑偏。</summary>
    public static bool MirrorGuide(int direction) => direction != 0;

    /// <summary>安装示意的配色 —— **纯函数**: 环线与箭头用中灰, 它在深面 `#101016` 与浅面 `#F5F5F5`
    /// 上都看得见, 所以不必分主题; 只有徽标的底/字要分面 (深面浅底白字 / 浅面浅灰底深字)。</summary>
    public static (Color Line, Color Head, Color BadgeBg, Color BadgeFg) GuideColors(bool dark)
        => (Color.FromRgb(0x7A, 0x7A, 0x85), Color.FromRgb(0x9A, 0x9A, 0xA6),
            dark ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00),
            dark ? Colors.White : Color.FromRgb(0x33, 0x33, 0x33));

    private bool _showGuide;
    private int _direction;
    private readonly List<UIElement> _guide = [];

    /// <summary>校准向导的安装示意: `visible` 控制显隐, `direction` = 绕向 (0 逆时针 / 1 顺时针)。
    /// 主页不显示 (那儿的中心只放静态几何参数) —— 由校准窗 `SetOrientation(true, dev.Direction)` 打开。</summary>
    public void SetOrientation(bool visible, int direction)
    {
        _showGuide = visible;
        _direction = direction;
        RebuildGuide();
    }

    /// <summary>画/擦安装示意。几何全部从 `_screen` 推出来 (它就是那块屏幕圆角矩形),
    /// 所以"重建布局 / 换主题 / 改绕向"都只需再调一次。画在**灯环画布**里 → 与灯珠同坐标、随 Viewbox 缩放。</summary>
    private void RebuildGuide()
    {
        foreach (var el in _guide) RingCanvas.Children.Remove(el);
        _guide.Clear();
        if (!_showGuide || _screen is null) return;      // 没 Configure 过 / 主页不显示

        void Add(UIElement el) { _guide.Add(el); RingCanvas.Children.Add(el); }

        var gc = GuideColors(IsDarkSurface);
        double x0 = Canvas.GetLeft(_screen), y0 = Canvas.GetTop(_screen);
        double sw = _screen.Width, sh = _screen.Height;
        const double inset = 24, r = 10;                 // 环线内缩: 与矩形边留出呼吸
        double x = x0 + inset, y = y0 + inset, w = sw - 2 * inset, h = sh - 2 * inset;

        // 起点在右下(逆时针)还是左下(顺时针) —— 下面三个元素都按它取坐标, **不用 RenderTransform**。
        // ⚠️ 教训: 曾经想用"整层关于画布中心镜像"来省事, 但 RenderTransform 是按**元素自身原点**
        // (局部坐标)解释的 → ScaleTransform(-1,1,画布中心,0) 会把元素整体平移到画布右侧 (顺时针那侧箭头跑飞)。
        // 所以这里老老实实按方向算坐标/出两条路径。
        bool right = StartCornerOf(_direction) == RingStartCorner.BottomRight;

        // ① 起点徽标: 骑在起点那个角上, 坐进环线的断口
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Background = Frozen(gc.BadgeBg),
            Child = new TextBlock { Text = "起点", FontSize = 12, Foreground = Frozen(gc.BadgeFg) },
        };
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = badge.DesiredSize;
        Canvas.SetLeft(badge, right ? x + w - size.Width + 12 : x - 12);
        Canvas.SetTop(badge, y + h - size.Height / 2);
        Add(badge);

        // ② 环线: 一圈**在起点处断开**的圆角矩形 —— 断口正好让"起点"徽标坐进去
        // (像灯带在这里起头/收尾)。断口横竖各切 g, 两端留圆头。两条路径分别是"断口在右下"与
        // "断口在左下"(= 关于竖直中轴的镜像), 都是 M → 三个圆角 → 停在断口另一侧。
        double g = size.Width + 6;
        // 两条路径 = 互为镜像的两个变体, 由 MirrorGuide 选 (顺时针 = 逆时针的镜像)
        string d = !MirrorGuide(_direction)
            ? $"M {F(x + w - g)},{F(y + h)} L {F(x + r)},{F(y + h)} " +
              $"A {r},{r} 0 0 1 {F(x)},{F(y + h - r)} L {F(x)},{F(y + r)} " +
              $"A {r},{r} 0 0 1 {F(x + r)},{F(y)} L {F(x + w - r)},{F(y)} " +
              $"A {r},{r} 0 0 1 {F(x + w)},{F(y + r)} L {F(x + w)},{F(y + h - g)}"
            // ⚠️ 顺时针这条是反向绕行, 三个圆角的 **sweep 必须是 0**(逆时针) —— 写 1 会让圆角
            // 朝外鼓成"外凸肩", 而包围盒只大一点点 (第一版就是这么错的: 位置/包围盒都像对的, 形状是错的)。
            : $"M {F(x + g)},{F(y + h)} L {F(x + w - r)},{F(y + h)} " +
              $"A {r},{r} 0 0 0 {F(x + w)},{F(y + h - r)} L {F(x + w)},{F(y + r)} " +
              $"A {r},{r} 0 0 0 {F(x + w - r)},{F(y)} L {F(x + r)},{F(y)} " +
              $"A {r},{r} 0 0 0 {F(x)},{F(y + r)} L {F(x)},{F(y + h - g)}";
        // PathGeometry (而非 Geometry.Parse 的 StreamGeometry): 几何可内省, 探针要读 Figure.IsClosed
        var loop = new Path
        {
            Data = PathGeometry.CreateFromGeometry(Geometry.Parse(d)),
            Stroke = Frozen(gc.Line),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Add(loop);

        // ③ 箭头: 断口外侧、指向断口 ("绕回起点")。逆时针朝右、顺时针朝左 —— 两个方向的
        //    三角朝向不同, 所以是两条路径 (不是把同一个镜像一下, 见上面的教训)。
        var head = new Path
        {
            Data = Geometry.Parse(right ? "M0,0 L0,10 L13,5 Z" : "M13,0 L13,10 L0,5 Z"),
            Fill = Frozen(gc.Head),
        };
        Canvas.SetLeft(head, right ? x + w - g - 6 : x + g + 6 - 13);
        Canvas.SetTop(head, y + h - 5);
        Add(head);

    }

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
        _guide.Clear();                        // Children.Clear 会把它们一起摘掉, 引用要同步作废
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
            StrokeThickness = 1,
        };
        _screen = screen;                      // ApplySurface 要按当前"面"给它上色
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
            // 描边: 灯与背景同色时靠它留出一道缝 (颜色随"控件所在底色", 见 Backdrop/LedOutline)
            var e = new Ellipse
            {
                Width = cell, Height = cell, Fill = Frozen(30, 30, 34), Tag = idx,
                StrokeThickness = 1,
            };
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
        ApplySurface();                       // 面/主题可能刚变过, 统一刷一遍 (矩形+文字+描边一次到位)
        RebuildGuide();                       // 安装示意 (校准窗才有) 依赖 _screen 与面的深浅, 放最后
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

// UiLoadProbe — 无界面加载探针 (UI 排查用)
//
// ⚠️ **本文件的判定原则** (改之前先读这一段):
//   只针对**程序自己算出来的 / 有规则的**东西做断言, 例如:
//     · 纯函数真值表 (MetricKeysForMode / DecidePaneForWidth)、
//     · 从对象自身读出来的不变量 (banner 外圆角 − 边框厚 = 光层内圆角)、
//     · 结构/机制是否就位 (模板能否渲染出内容、AnimatedVisibility 是否被接管、
//       某个资源能否解析 —— 资源解析失败是**静默**的, 只能靠断言),
//     · 数据驱动的计数 (磁贴数 == MCU 模式数, 而不是写死 11)。
//   **不要**断言我某一轮挑的具体数值 (圆心坐标、Margin、停靠坐标、分辨率/显卡型号这类文本),
//   那些是设计值, 用户随手在 VS 里调一下就会让探针"狼来了"。
//   反面教材 (已删): "圆心必须是 (15,15)"、"相邻圈内外沿必须相接"、"颜色值 = #FFE1D6"。
//
// 除此之外还做两件事:
//   1) STA + App 全局资源环境下实例化全部页面, 捕获 XamlParseException 的精确位置 (XAML 里写错
//      图标名/属性名只会在运行时炸, 这是唯一能提前发现的地方);
//   2) 导出 SettingRow 可视树, 方便肉眼核对模板渲染。
// 注意: 只 new App() 不 Run() —— OnStartup (单例守卫/托盘/主窗/核心) 不会触发。
// WPF 控件必须在 STA 线程创建 → 全部工作放 STA 线程执行。
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmbientLight.Interop;

Console.OutputEncoding = Encoding.UTF8;
// `--dump-ring <输出.png>`: 只把 LedRingView 渲染成图片 (设计评审用), 不跑断言。
// 无头宿主里 RenderTargetBitmap 可用 (只要先 Measure/Arrange) —— 这样改视觉之前能先看一眼。
if (args.Length >= 2 && args[0] == "--dump-slider")
{
    var ts = new Thread(() => DumpSlider(args[1]));
    ts.SetApartmentState(ApartmentState.STA);
    ts.Start();
    ts.Join();
    return;
}
// `--dump-slider-live <前缀>`: 在**真实页面结构**里量滑块状态并出图 (第四轮排查工具, 见 SliderDiag.cs)。
// 输出 <前缀>_bright_enabled.png / _bright_disabled.png / _picker.png。
if (args.Length >= 2 && args[0] == "--dump-slider-live")
{
    var tl = new Thread(() => UiLoadProbe.SliderDiag.Dump(args[1]));
    tl.SetApartmentState(ApartmentState.STA);
    tl.Start();
    tl.Join();
    return;
}
if (args.Length >= 2 && args[0] == "--dump-ring")
{
    var td = new Thread(() => DumpRing(args[1], args.Length >= 3 && int.TryParse(args[2], out var od) ? od : -1));
    td.SetApartmentState(ApartmentState.STA);
    td.Start();
    td.Join();
    return;
}
// `--dump-page <前缀>`: 整页出图（重构前后像素对照）—— home/capture/audio/color/advanced/devices
if (args.Length >= 2 && args[0] == "--dump-page")
{
    var tp = new Thread(() => UiLoadProbe.PageDiag.Dump(args[1]));
    tp.SetApartmentState(ApartmentState.STA);
    tp.Start();
    tp.Join();
    return;
}
var t = new Thread(Run);
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
return;

/// <summary>把**滑块的各种状态**渲染成一张 PNG (设计/排查用): 普通/锚点左/锚点右/色带轨道/禁用。
/// 用途: 滑块皮肤是"看起来对不对"的问题, 数值断言抓不住观感 → 出图肉眼判断 ✓</summary>
static void DumpSlider(string path)
{
    var app = new AmbientLight.App();
    app.InitializeComponent();
    typeof(AmbientLight.App)
        .GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?.SetValue(null, app);

    var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)) };
    void Row(string cap, Slider sl)
    {
        panel.Children.Add(new TextBlock
        {
            Text = cap, Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5)),
            FontSize = 12, Margin = new Thickness(16, 12, 16, 2),
        });
        sl.Width = 260;
        sl.VerticalAlignment = VerticalAlignment.Center;
        sl.Margin = new Thickness(16, 0, 16, 4);
        panel.Children.Add(sl);
    }

    // 普通 (Track): 50% / 100% / 5% 三种值 —— 看左端是否贴边、右端是否被拇指接住
    foreach (var v in new[] { 50.0, 100.0, 5.0 })
        Row($"Track 模式 · 值 {v:F0}%", new Slider { Minimum = 0, Maximum = 100, Value = v });

    // 锚点 (Anchor, 中性 1): 往右 / 往左 / 正好在中性
    foreach (var v in new[] { 1.5, 0.5, 1.0 })
    {
        var a2 = new Slider { Minimum = 0, Maximum = 2, Value = v };
        AmbientLight.Controls.SliderSkin.SetFill(a2, AmbientLight.Controls.SliderFill.Anchor);
        AmbientLight.Controls.SliderSkin.SetAnchor(a2, 1.0);
        Row($"Anchor 模式 (中性 1) · 值 {v:F1}", a2);
    }

    // None: 模拟色带轨道 (底层放一条彩虹 Border, 滑块轨道刷子覆盖为透明 —— 与主页拾色器同款)
    {
        var grid = new Grid { Width = 260, Height = 32 };
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (off, col) in new (double, Color)[]
                 { (0.00, Color.FromRgb(0xFF,0x40,0x40)), (0.25, Color.FromRgb(0xFF,0xE0,0x40)),
                   (0.50, Color.FromRgb(0x40,0xE0,0x80)), (0.75, Color.FromRgb(0x40,0xB0,0xFF)),
                   (1.00, Color.FromRgb(0xC0,0x60,0xFF)) })
            rainbow.GradientStops.Add(new GradientStop(col, off));
        grid.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2),
                                       VerticalAlignment = VerticalAlignment.Center,
                                       Background = rainbow, IsHitTestVisible = false });
        var ns = new Slider { Minimum = 0, Maximum = 360, Value = 24, Width = 260 };
        AmbientLight.Controls.SliderSkin.SetFill(ns, AmbientLight.Controls.SliderFill.None);
        ns.Resources.Add("SliderTrackFill", new SolidColorBrush(Colors.Transparent));
        ns.Resources.Add("SliderTrackFillPointerOver", new SolidColorBrush(Colors.Transparent));
        ns.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(ns);
        panel.Children.Add(new TextBlock
        {
            Text = "None 模式 (色带轨道, 应与主页拾色器同款) · 值 24/360",
            Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5)),
            FontSize = 12, Margin = new Thickness(16, 12, 16, 2),
        });
        panel.Children.Add(grid);
    }

    // 禁用态: Track 模式 + 禁用 (应与可用态明显不同、且填充同样贴边)
    {
        var d = new Slider { Minimum = 0, Maximum = 100, Value = 50, IsEnabled = false };
        Row("Track 模式 · **禁用** (值 50%)", d);
    }

    const double zoom = 2;
    panel.Measure(new Size(300, double.PositiveInfinity));
    panel.Arrange(new Rect(0, 0, 300, panel.DesiredSize.Height));
    panel.UpdateLayout();
    // ⚠️ 这里**不再**手动调 ApplyFill (第四轮起没有任何"代码去模板里改元素"的钩子了) ——
    //    图上看到的就是真实后果: 若是 None/禁用那两行还盖着一条填充, 说明声明式的收起没生效 ✗
    var bmp = new RenderTargetBitmap((int)(300 * zoom), (int)Math.Ceiling(panel.DesiredSize.Height * zoom),
                                     96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
    bmp.Render(panel);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var fs = File.Create(path);
    enc.Save(fs);
    Console.WriteLine($"dumped -> {path} ({bmp.PixelWidth}x{bmp.PixelHeight})");
}

/// <summary>把"校准窗的安装示意"渲染成一张 PNG: 逆时针 / 顺时针 并排, 便于肉眼评审。
/// 纯设计工具 —— 不参与断言 (断言在 Run 里)。</summary>
static void DumpRing(string path, int onlyDir)
{
    var app = new AmbientLight.App();
    app.InitializeComponent();
    typeof(AmbientLight.App)
        .GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?.SetValue(null, app);
    // 与校准窗一致: 恒深面 + 标准图 (象限色)
    var panel = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)) };
    // 只给一个方向时就放大 3 倍 (便于看局部: 断口/箭头/徽标的位置关系)
    var wanted = onlyDir >= 0
        ? new[] { (onlyDir, $"direction {onlyDir} · {(onlyDir == 0 ? "逆时针 (起点在右下)" : "顺时针 (起点在左下)")}") }
        : new[] { (0, "direction 0 · 逆时针 (起点在右下)"), (1, "direction 1 · 顺时针 (起点在左下)") };
    foreach (var (dir, cap) in wanted)
    {
        panel.Children.Add(new TextBlock
        {
            Text = cap, Foreground = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5)),
            FontSize = 12, Margin = new Thickness(16, 14, 16, 2),
        });
        var ring = new AmbientLight.Controls.LedRingView
        {
            Surface = AmbientLight.Controls.LedRingView.RingSurface.AlwaysDark,
            Width = 460, Height = 300,
        };
        ring.Configure(60, 4, dir, 0, 1920, 1080);
        ring.ShowQuadrantMap([Color.FromRgb(0x56, 0xB0, 0xF0), Color.FromRgb(0x46, 0xD4, 0xC5),
                              Color.FromRgb(0xC3, 0x8F, 0xF1), Color.FromRgb(0xE4, 0xEB, 0xF2)]);
        ring.SetOrientation(true, dir);
        panel.Children.Add(ring);
    }
    // 2 倍渲染: 徽标里的字很小, 放大后才看得出底/字色与位置关系。
    // 放大走**提高 DPI**(RenderTargetBitmap 按 DPI 缩放视觉), 不是 LayoutTransform ——
    // 后者不改排布尺寸, 渲染出来会被裁成原来那么大 (第一版就是这么被裁掉右半边的)。
    double zoom = onlyDir >= 0 ? 3 : 2;
    panel.Measure(new Size(520, double.PositiveInfinity));
    panel.Arrange(new Rect(0, 0, 520, panel.DesiredSize.Height));
    panel.UpdateLayout();
    var bmp = new RenderTargetBitmap((int)(520 * zoom),
                                     (int)Math.Ceiling(panel.DesiredSize.Height * zoom),
                                     96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
    bmp.Render(panel);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var fs = File.Create(path);
    enc.Save(fs);
    Console.WriteLine($"dumped -> {path} ({bmp.PixelWidth}x{bmp.PixelHeight})");
}

static void Run()
{
    var app = new AmbientLight.App();   // 不触发 OnStartup (单例守卫/托盘/主窗/核心)
    app.InitializeComponent();          // 入口 Main 里才调用, 探针必须手动调: 加载 App.xaml 全局资源
    // `App.Instance` 是在 OnStartup 里赋的 (`_instance = this`), 而探针不跑 OnStartup →
    // 任何走 `App.Instance.xxx` 的代码路径都会 NRE。这里用反射把它补上, 让"有会话"这类
    // 路径也能在无头宿主里跑到 (设备行按 key 配对需要 App 实例)。
    typeof(AmbientLight.App)
        .GetField("_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?.SetValue(null, app);
    Console.WriteLine("INFO App.Instance 已注入 (反射补位, 仅探针用) = "
                      + (AmbientLight.App.Instance is not null));
    Console.WriteLine($"GroupLabel in app resources: {app.Resources.Contains("GroupLabel")}");

    foreach (var pt in new[]
    {
        typeof(AmbientLight.Views.HomePage),
        typeof(AmbientLight.Views.SettingsPageAudio),
        typeof(AmbientLight.Views.SettingsPageCapture),
        typeof(AmbientLight.Views.SettingsPageColor),
        typeof(AmbientLight.Views.SettingsPageAdvanced),
    })
    {
        try
        {
            var page = (UIElement)Activator.CreateInstance(pt)!;
            // Page 只能挂在 Window/Frame 下 → 用 Frame 宿主
            var frame = new Frame { Width = 760, Height = 1400 };
            frame.Content = page;
            frame.Measure(new Size(760, 1400));
            frame.Arrange(new Rect(0, 0, 760, 1400));
            Console.WriteLine($"OK   {pt.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL {pt.Name}");
            Console.WriteLine(ex.ToString());
        }
    }

    Console.WriteLine();
    Console.WriteLine("---- SettingRow visual tree ----");
    var row = new AmbientLight.Controls.SettingRow
    {
        Title = "测试标题",
        Description = "测试说明",
        Content = new Button { Content = "右侧控件" },
    };
    var host = new ContentControl { Content = row, Width = 700 };
    host.Measure(new Size(700, 100));
    host.Arrange(new Rect(0, 0, 700, 100));
    row.ApplyTemplate();
    Dump(row, 0);

    Console.WriteLine();
    Console.WriteLine("---- native ABI 版本 (防止旁边躺着一个旧 DLL) ----");
    // ACMonitorInfo 在 v1.5 加宽了 adapter 缓冲 —— 布局对不上时读出来的是错位的分辨率/适配器,
    // **不抛异常**。所以这条必须查: DLL 加载得了, 且版本不低于托管代码对齐的那一版。
    try
    {
        uint v = AmbientCore.Version();
        int vmaj = (int)((v >> 16) & 0xFF), vmin = (int)((v >> 8) & 0xFF), vpat = (int)(v & 0xFF);
        var (rmaj, rmin) = AmbientCore.RequiredAbi;
        bool abiOk = vmaj == rmaj && vmin >= rmin;
        Console.WriteLine((abiOk ? "OK   " : "FAIL ") +
                          $"AmbientCore v{vmaj}.{vmin}.{vpat}, 托管侧要求 >= {rmaj}.{rmin} = {abiOk}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL 加载 AmbientCore.dll: {ex.GetType().Name}: {ex.Message}");
    }

    // 卡片间距: 断言的是"一致", 不是"等于 4" —— 用户调间距时不该报错, 但"某一张被动画
    // 把基准 margin 抹掉"这种真 bug 会表现为"唯独它跟别人不一样"。
    Console.WriteLine();
    Console.WriteLine("---- 卡片间距 (卡片级元素的下边距必须彼此一致) ----");
    foreach (var pt in new[]
    {
        typeof(AmbientLight.Views.HomePage),
        typeof(AmbientLight.Views.SettingsPageAudio),
        typeof(AmbientLight.Views.SettingsPageCapture),
        typeof(AmbientLight.Views.SettingsPageColor),
        typeof(AmbientLight.Views.SettingsPageAdvanced),
    })
    {
        var page = (DependencyObject)Activator.CreateInstance(pt)!;
        var cards = LogicalDescendants(page).OfType<FrameworkElement>().Where(IsCardLevel).ToList();
        double norm = cards.Count > 0 ? cards[0].Margin.Bottom : 0;   // 以首张为基准 (约定值)
        var bad = new List<string>();
        // 末张允许不留尾距 (0)
        for (int i = 1; i < cards.Count - 1; i++)
        {
            if (Math.Abs(cards[i].Margin.Bottom - norm) > 0.01)
            {
                string name = cards[i].Name.Length > 0 ? "#" + cards[i].Name : "";
                bad.Add($"{cards[i].GetType().Name}{name}={cards[i].Margin.Bottom}(基准 {norm})");
            }
        }
        Console.WriteLine(bad.Count == 0
            ? $"OK   {pt.Name}: {cards.Count} 个卡片级元素, 下边距彼此一致 (基准 {norm})"
            : $"FAIL {pt.Name}: {string.Join(", ", bad)}");
    }

    // SettingRow 头部结构: 从 App 资源模板装出来 (标题/说明真的进了两行文本); 空说明收起第二行。
    {
        var advRows = new AmbientLight.Views.SettingsPageAdvanced();
        var rows = LogicalDescendants(advRows).OfType<AmbientLight.Controls.SettingRow>().ToList();
        int wired = 0, mismatch = 0;
        foreach (var r in rows)
        {
            if (r.Header is not Grid hg || hg.Children.Count < 2
                || hg.Children[0] is not TextBlock t0 || hg.Children[1] is not TextBlock t1)
                continue;
            bool titleOk = t0.Text == (r.Title ?? "");
            bool descOk = string.IsNullOrEmpty(r.Description)
                ? t1.Visibility == Visibility.Collapsed && t1.Text.Length == 0
                : t1.Visibility == Visibility.Visible && t1.Text == r.Description;
            if (titleOk && descOk) wired++;
            else mismatch++;
        }
        Console.WriteLine((rows.Count > 0 && mismatch == 0 && wired == rows.Count ? "OK   " : "FAIL ") +
                          $"SettingRow 头部模板接线: {wired}/{rows.Count} 行的标题/说明进了两行文本 (不匹配 {mismatch})");
    }

    // XAML 字面量 → {DynamicResource} 后的接线抽查 —— 断言"XAML 显示值 == 字典值 (同键同源)"。
    // 动机: DynamicResource 缺键/断链是**静默**的 (文本变空), 出图对照能兜底, 这里再给一条数值断言
    // (不写死文案, 只验证两边取自同一个字典)。
    {
        var hp8 = new AmbientLight.Views.HomePage();
        var cap8 = new AmbientLight.Views.SettingsPageCapture();
        var ring8 = new AmbientLight.Controls.LedRingView();
        string K(string k) => AmbientLight.Localization.Loc.T(k);
        var xamlWired = new (string Desc, object? Got, string Want)[]
        {
            ("主页 WarnBar.Title", (hp8.FindName("WarnBar") as Wpf.Ui.Controls.InfoBar)?.Title, K("Home.Warn.Title")),
            ("主页 DiagBar.Title", (hp8.FindName("DiagBar") as Wpf.Ui.Controls.InfoBar)?.Title, K("Home.Diag.Title")),
            ("主页 BrightnessRow.Title", (hp8.FindName("BrightnessRow") as AmbientLight.Controls.SettingRow)?.Title, K("Home.Brightness.Title")),
            ("主页 MetricsEmptyText", (hp8.FindName("MetricsEmptyText") as TextBlock)?.Text, K("Home.Metrics.Empty")),
            ("采集页 RotateRow.Title", (cap8.FindName("RotateRow") as AmbientLight.Controls.SettingRow)?.Title, K("Cap.Rotate.Title")),
            ("灯环 HintTitle", (ring8.FindName("HintTitle") as TextBlock)?.Text, K("Ring.Preview")),
        };
        var xamlBad = xamlWired.Where(c => !Equals(c.Got, c.Want))
                               .Select(c => $"{c.Desc}: {c.Got ?? "(null)"} != {c.Want}").ToList();
        Console.WriteLine((xamlBad.Count == 0 ? "OK   " : "FAIL ") +
                          $"XAML {{DynamicResource}} 文案接线抽查 {xamlWired.Length} 处 (显示值 == 字典值): " +
                          (xamlBad.Count == 0 ? "全部同源" : string.Join("; ", xamlBad)));
    }

    Console.WriteLine();
    Console.WriteLine("---- ModeCard visual check (HomePage 资源模板) ----");
    var home = new AmbientLight.Views.HomePage();
    var tpl = home.TryFindResource("ModeCardTemplate");
    Console.WriteLine($"ModeCardTemplate resolved: {tpl != null} ({tpl?.GetType().Name})");

    // 渐变/画刷里嵌 DynamicResource 解析失败**不抛异常**, 只表现为"那一层不显示" → 必须显式断言
    // "能解析成真渐变"; 不断言 stop 数 (那是设计值)。
    Console.WriteLine();
    Console.WriteLine("---- HomePage banner 资源 (光带静默失效回归) ----");
    foreach (var key in new[]
    {
        "BannerBaseBrush", "BannerStreakBlueBrush", "BannerStreakGreenBrush",
        "BannerStreakPurpleBrush", "BannerStreakMonoBrush",
    })
    {
        var br = home.TryFindResource(key) as System.Windows.Media.GradientBrush;
        int stops = br?.GradientStops.Count ?? 0;
        bool ok = br is System.Windows.Media.LinearGradientBrush && stops >= 2;
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"{key}: {(br == null ? "未找到" : $"{br.GetType().Name} / {stops} stops")}");
    }

    Console.WriteLine();
    Console.WriteLine("---- HomePage 纯色拾色器: 默认折叠 ----");
    var frame2 = new Frame { Width = 760, Height = 1600 };
    frame2.Content = home;
    Layout(frame2);

    var solidCard = home.FindName("SolidCard") as Expander;
    bool solidCollapsed = solidCard != null && !solidCard.IsExpanded;
    Console.WriteLine((solidCollapsed ? "OK   " : "FAIL ") +
                      $"SolidCard 默认折叠 = {solidCollapsed} (展开态只露 Header: HEX 框 + 色块)");

    // banner 点亮态的三条光层必须自带圆角 —— WPF 的 Border **只把 Background 裁到圆角,
    // 不裁子元素**, 所以光层 Rectangle 漏写 RadiusX/RadiusY 就会把 banner 四角撑成直角
    // (灰色态是圆角、点亮态变直角)。内圆角 = 外圆角 − 边框厚度。
    Console.WriteLine();
    Console.WriteLine("---- HomePage banner 光层圆角 (点亮态不得变直角) ----");
    var banner = home.FindName("Banner") as System.Windows.Controls.Border;
    double outerR = banner?.CornerRadius.TopLeft ?? 0;
    double innerR = Math.Max(0, outerR - (banner?.BorderThickness.Top ?? 0));
    var lightRects = new List<System.Windows.Shapes.Rectangle>();
    if (home.FindName("LightsLayer") is System.Windows.Controls.Grid lights)
        lightRects.AddRange(lights.Children.OfType<System.Windows.Shapes.Rectangle>());
    var sharp = lightRects.Where(r => r.RadiusX != innerR || r.RadiusY != innerR).ToList();
    Console.WriteLine((lightRects.Count == 3 && sharp.Count == 0 ? "OK   " : "FAIL ") +
                      $"光层 {lightRects.Count} 条 (期望 3)，内圆角要求 {innerR}（外 {outerR} − 边框 {banner?.BorderThickness.Top}）" +
                      (sharp.Count > 0
                          ? $"; 未圆角: {string.Join(", ", sharp.Select((r, i) => $"#{i}={r.RadiusX}"))}"
                          : ""));
    // banner 里不得再出现噪点层 (无时域抖动时静态噪点只会把画面弄脏)
    bool ditherGone = home.FindName("Dither") == null;
    Console.WriteLine((ditherGone ? "OK   " : "FAIL ") + "Dither 噪点层已移除");

    // 提示条显隐**回到 IsOpen 原生行为**, 不用 AnimatedVisibility —— InfoBar 模板是用
    // IsOpen 直接切 Visibility(Collapsed) 的, Collapsed 的元素整个退出布局, 高度动画没有任何可见效果;
    // 两套机制并存只会互相打架。这条断言用来守住"只用 IsOpen", 防止又被人加上 AnimatedVisibility。
    Console.WriteLine();
    Console.WriteLine("---- HomePage 提示条 (显隐只用 IsOpen, 勿再加 AnimatedVisibility) ----");
    foreach (var name in new[] { "WarnBar", "DiagBar" })
    {
        var bar = home.FindName(name) as Wpf.Ui.Controls.InfoBar;
        bool untouched = bar != null && AmbientLight.Controls.AnimatedVisibility.GetExpanded(bar);
        bool ok = bar != null && !bar.IsOpen && untouched;
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"{name}: 默认 IsOpen={bar?.IsOpen} (要求 False), " +
                          $"AnimatedVisibility.Expanded={untouched} (要求 True = 未被接管)");
    }

    // "连接中断 → banner 不点亮 + 胶囊说'正在尝试重连' + 按钮说'关闭'" 属于行为约定,
    // 无头探针验不到 (要真实会话状态机), 留给真机复验; 口径同源由 ApplyBannerLook/IsLit 与
    // "按钮 = App.CoreRunning" 保证。

    // "刷新/重新枚举"这类按钮必须是纯图标 (无按钮底/边框)。
    // 断言方式: 按**名字**找那四个已知的刷新按钮, 要求它们都用 IconOnlyButtonTemplate ——
    // 不断言"全页只有 N 个纯图标按钮"(那会随界面调整误报)。
    var tplRes = home.TryFindResource("IconOnlyButtonTemplate");
    var named = new (object Owner, string Name)[]
    {
        (home, "RefreshDevicesBtn"), (home, "RefreshMonitorsBtn"),
    };
    bool namedOk = tplRes is ControlTemplate
                   && named.All(x => x.Owner is FrameworkElement fe
                                     && fe.FindName(x.Name) is Wpf.Ui.Controls.Button b
                                     && ReferenceEquals(b.Template, tplRes));
    Console.WriteLine((namedOk ? "OK   " : "FAIL ") +
                      $"IconOnlyButtonTemplate 可从 App 级资源解析 = {tplRes is ControlTemplate}; " +
                      $"主页两个刷新按钮都用它 = {namedOk}");
    // 模板换成"只有 ContentPresenter"之后图标还画得出来吗。注意: 无窗口宿主下 **Page 自身模板不套用**
    // (M5.1 老坑), 所以页内那个按钮的可视树本来就是空的 → 必须把模板套到独立宿主上单独布局量。
    if (tplRes is ControlTemplate iconTpl)
    {
        var standalone = new Wpf.Ui.Controls.Button
        {
            Template = iconTpl,
            Content = new Wpf.Ui.Controls.SymbolIcon
            {
                Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowClockwise24, FontSize = 14,
            },
        };
        var host3 = new ContentControl { Content = standalone, Width = 120 };
        host3.Measure(new Size(120, 60));
        host3.Arrange(new Rect(0, 0, 120, 60));
        standalone.ApplyTemplate();
        int icons = 0;
        CountSymbolIcons(standalone);
        Console.WriteLine((icons >= 1 ? "OK   " : "FAIL ") +
                          $"IconOnlyButtonTemplate 渲染出的 SymbolIcon 数量 = {icons} (期望 >= 1)");
        void CountSymbolIcons(DependencyObject d)
        {
            if (d is Wpf.Ui.Controls.SymbolIcon) icons++;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                CountSymbolIcons(VisualTreeHelper.GetChild(d, i));
        }
    }

    // 左侧菜单自适应折叠的决策真值表。最容易改错的一条是"恢复只对自动折的那次生效"
    // (用户自己折起来的菜单不能被自动展开), 而改错**不会抛异常**, 只会静默变成错的交互 ——
    // 正是最该被回归网住的东西。
    Console.WriteLine();
    Console.WriteLine("---- 左侧菜单自适应折叠决策 (MainWindow.DecidePaneForWidth) ----");
    var paneCases = new (bool Wide, bool Open, bool Auto, bool? SetOpen, bool AutoAfter, string Desc)[]
    {
        (false, true,  false, false, true,  "变窄 + 展开着 → 自动折叠并记账"),
        (false, false, false, null,  false, "变窄 + 本来就折着 → 不动 (这是用户自己折的)"),
        (true,  false, true,  true,  false, "变宽 + 上次是自动折的 → 恢复展开"),
        (true,  false, false, null,  false, "变宽 + 用户自己折的 → 保持折叠"),
        (true,  true,  true,  true,  false, "变宽 + 已经展开着 → 空操作, 只清标记"),
        (false, true,  true,  false, true,  "再次变窄 + 展开着 → 重新折叠"),
    };
    foreach (var c in paneCases)
    {
        var (setOpen, auto) = AmbientLight.Views.MainWindow.DecidePaneForWidth(c.Wide, c.Open, c.Auto);
        bool ok = setOpen == c.SetOpen && auto == c.AutoAfter;
        string got = setOpen?.ToString() ?? "不动";
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"{c.Desc} → IsPaneOpen={got}, 自动标记={auto}" +
                          (ok ? "" : $"  (期望 {c.SetOpen?.ToString() ?? "不动"} / {c.AutoAfter})"));
    }

    // 真 bug 回归 (不依赖具体数值): AnimatedVisibility 的基准 Margin 只能在元素
    // Loaded 时捕获 (解析期读到的是默认 0 → 展开后下边距永久少一截)。
    // 判据改成**与同页同类元素比对**: 展开后 SolidCard 的下边距必须等于另一个 CardExpander
    // (FxExpander) 的 —— 用户改间距两边一起改, 不再误报; 而"被抹成 0"这个 bug 一定会露馅。
    if (solidCard != null && home.FindName("FxExpander") is FrameworkElement refCard)
    {
        solidCard.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        AmbientLight.Controls.AnimatedVisibility.SetExpanded(solidCard, true);
        var afterExpand = solidCard.ReadLocalValue(FrameworkElement.MarginProperty) as Thickness?;
        double want = refCard.Margin.Bottom;
        bool ok = afterExpand is { } tt && Math.Abs(tt.Bottom - want) < 0.01;
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"AnimatedVisibility 基准 Margin: 展开后 SolidCard.Bottom={afterExpand?.Bottom}, " +
                          $"同页 CardExpander 基准={want} (要求相等)");
    }

    if (solidCard != null)
    {
        // CardExpander 的展开高度由模板内的动画属性驱动 (AnimationProperties.AnimationTagValue),
        // 无 Dispatcher 泵的探针里动画不走 → 内容恒 0 高, 量不出行距。
        // 故把拾色器内容整体摘出来单独布局 —— 行距属内容自身布局, 与展开器外壳无关。
        try
        {
            var picker = (FrameworkElement)solidCard.Content;
            solidCard.Content = null;
            var host2 = new ContentControl { Content = picker, Width = 700 };
            host2.Measure(new Size(700, 800));
            host2.Arrange(new Rect(0, 0, 700, 800));
            host2.UpdateLayout();

            // 用 LayoutInformation.GetLayoutSlot: slot 相对**父容器**, 且**含 margin** — 但 StackPanel
            // 的 slot 是首尾相接的 (槽高 = 元素高 + 上下边距), 故槽间隙恒 0, 不能直接相减。
            // 真"行距" = 下一行内容顶 - 上一行内容底; 内容顶 = slot.Y + Margin.Top。
            // 这里只**打印**实测值供肉眼核对, 不做判定 —— 行距是设计值, 用户随时会调。
            FrameworkElement Row(string name) => (FrameworkElement)home.FindName(name)!;
            var rows = new[] { Row("HueRow"), Row("SatRow") };
            var channel = new[] { "H", "S" };
            var tops = rows.Select(r => LayoutInformation.GetLayoutSlot(r).Y + r.Margin.Top).ToArray();
            for (int i = 0; i < rows.Length; i++)
                Console.WriteLine($"INFO row {channel[i]}: top={tops[i]:F2}" +
                                  $"  bottom={tops[i] + rows[i].ActualHeight:F2}" +
                                  $"  (高 {rows[i].ActualHeight:F2}, margin={rows[i].Margin})");
            Console.WriteLine($"INFO 行距 H→S = {tops[1] - (tops[0] + rows[0].ActualHeight):F2}px " +
                              "(仅供参考, 不判定)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"行距测量失败: {ex.Message}");
        }
    }

    // 高级页「关于」组的两个断言 + 窗口级 ContentDialog 宿主。
    // ① ContentDialog 宿主: WPF-UI 的 ShowAsync() 在宿主为空时直接抛 "DialogHost was not set",
    //    而那只在用户点按钮时才暴露 → 断言宿主存在且类型正确 (宿主注册发生在 Loaded, 探针不显示窗口,
    //    所以这里查 XAML 声明本身, 不用 GetForWindow)。
    // ② 「恢复默认配置」必须在「配置目录」的**折叠内** —— 破坏性操作不该裸奔在页面上。
    Console.WriteLine();
    Console.WriteLine("---- 高级页「关于」组 ----");
    try
    {
        var win = new AmbientLight.Views.MainWindow();
        bool hostOk = win.FindName("RootDialogHost") is Wpf.Ui.Controls.ContentDialogHost;
        Console.WriteLine((hostOk ? "OK   " : "FAIL ") +
                          $"MainWindow.RootDialogHost 是 ContentDialogHost = {hostOk}");
        // Mica/亚克力 (PROJECT_PLAN §8.2): 走 FluentWindow 官方管线 —— XAML 声明 WindowBackdropType,
        // 由 OnSourceInitialized → OnBackdropTypeChanged 做完整序列。这里的**硬契约**是:
        // 只要用了 backdrop, ExtendsContentIntoTitleBar 就必须为 True, 否则 4.3.0 直接抛
        // InvalidOperationException ("Cannot apply backdrop effect if ... is false")。
        // 不断言具体是哪种 backdrop (改成 Acrylic 也不该报警)。
        bool backdropSet = win.WindowBackdropType != Wpf.Ui.Controls.WindowBackdropType.None;
        bool backdropOk = !backdropSet || win.ExtendsContentIntoTitleBar;
        Console.WriteLine((backdropOk ? "OK   " : "FAIL ") +
                          $"半透明: WindowBackdropType={win.WindowBackdropType}, " +
                          $"ExtendsContentIntoTitleBar={win.ExtendsContentIntoTitleBar} " +
                          $"(用了 backdrop 就必须为 True) = {backdropOk}");
        // 左侧菜单文案 —— 按 TargetPageType 找 item (不依赖顺序), 断言 XAML ↔ 字典同源
        if (win.FindName("RootNav") is Wpf.Ui.Controls.NavigationView nav8)
        {
            var navWant = new (Type Page, string Key)[]
            {
                (typeof(AmbientLight.Views.HomePage), "Nav.Home"),
                (typeof(AmbientLight.Views.SettingsPageCapture), "Nav.Capture"),
                (typeof(AmbientLight.Views.SettingsPageAudio), "Nav.Audio"),
                (typeof(AmbientLight.Views.SettingsPageColor), "Nav.Color"),
                (typeof(AmbientLight.Views.SettingsPageAdvanced), "Nav.Adv"),
            };
            var navBad = navWant.Where(w =>
                    !nav8.MenuItems.OfType<Wpf.Ui.Controls.NavigationViewItem>().Any(it =>
                        it.TargetPageType == w.Page && Equals(it.Content, AmbientLight.Localization.Loc.T(w.Key))))
                .Select(w => w.Key).ToList();
            Console.WriteLine((navBad.Count == 0 ? "OK   " : "FAIL ") +
                              $"导航文案 {navWant.Length} 项 (XAML ↔ 字典同源): " +
                              (navBad.Count == 0 ? "全部一致" : string.Join(", ", navBad)));
        }
        else
        {
            Console.WriteLine("FAIL 未找到 RootNav (导航文案断言无法执行)");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL MainWindow 实例化: {ex.GetType().Name}: {ex.Message}");
    }

    // 校准窗文案走 {DynamicResource} —— 用反射实例化一次 (私有构造; 不 Show),
    // 保证 XAML 解析得过 + 窗口标题与字典同源。该窗不在 --dump-page 的六图里, 这里是唯一防线。
    try
    {
        var calCtor = typeof(AmbientLight.Views.CalibrateWindow).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new[] { typeof(int), typeof(string) }, null);
        var calWin = (Window)calCtor!.Invoke(new object[] { 0, "0_1" });
        bool calTitleOk = Equals(calWin.Title, AmbientLight.Localization.Loc.T("Calib.WindowTitle"));
        Console.WriteLine((calTitleOk ? "OK   " : "FAIL ") +
                          $"校准窗实例化 + 标题与字典同源 = {calTitleOk} ({calWin.Title})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL 校准窗实例化: {ex.GetType().Name}: {ex.Message}");
    }

    var advanced = new AmbientLight.Views.SettingsPageAdvanced();
    bool hasUpdateRow = advanced.FindName("UpdateRow") != null;
    Console.WriteLine((hasUpdateRow ? "OK   " : "FAIL ") + $"「版本更新」行存在 = {hasUpdateRow}");

    bool restoreInExpander = false;
    if (advanced.FindName("RestoreDefaultsBtn") is FrameworkElement restoreBtn)
        restoreInExpander = LogicalDescendants(advanced).OfType<Wpf.Ui.Controls.CardExpander>()
            .Any(c => LogicalDescendants(c).Contains(restoreBtn));
    Console.WriteLine((restoreInExpander ? "OK   " : "FAIL ") +
                      $"「恢复默认配置」按钮在 CardExpander 折叠内 = {restoreInExpander}");

    // ================= M5.1 主页构件重设计 (docs/M5.1_HOME_REDESIGN_PLAN.md §2~§7) =================
    // 无头宿主里 Loaded 不触发, 所以先让页面自己做一次"不依赖 App/核心"的界面装配,
    // 才有真实的磁贴/指标条结构可断言。
    home.BuildForProbe();

    Console.WriteLine();
    Console.WriteLine("---- §2 固件特效: 图标磁贴网格 ----");
    var fxPanel = home.FindName("FxPanel") as Panel;   // 磁贴已改为 XAML 静态子元素 (WrapPanel)
    var fxTpl = home.TryFindResource("FxTileTemplate");
    var fxTiles = fxPanel?.Children.OfType<System.Windows.Controls.RadioButton>().ToList() ?? [];
    // 磁贴数必须等于**MCU 模式数** (数据驱动), 不写死 11 —— 加一款特效不该让探针报警
    int mcuCount = AmbientLight.Views.ModeCatalog.All.Count(AmbientCore.IsMcuMode);
    bool tilesOk = fxTpl is ControlTemplate && fxPanel is not null && fxTiles.Count == mcuCount
                   && fxTiles.All(t => ReferenceEquals(t.Template, fxTpl));
    Console.WriteLine((tilesOk ? "OK   " : "FAIL ") +
                      $"磁贴 {fxTiles.Count} 个 == MCU 模式数 {mcuCount}, 全部使用 FxTileTemplate = {tilesOk}");
    int tilesWithIcon = fxTiles.Count(t => t.Content is Panel p
        && p.Children.OfType<Wpf.Ui.Controls.SymbolIcon>().Any());
    Console.WriteLine((tilesWithIcon == fxTiles.Count ? "OK   " : "FAIL ") +
                      $"带图标的磁贴 = {tilesWithIcon}/{fxTiles.Count} (每一款都要有图标)");
    // 磁贴里显示的必须是模式名 (数据驱动), 不是我自己编的文案
    var tileNames = fxTiles.Select(t => t.Content is Panel p
        ? string.Concat(p.Children.OfType<TextBlock>().Select(x => x.Text)) : "").ToList();
    var expectNames = AmbientLight.Views.ModeCatalog.All.Where(AmbientCore.IsMcuMode)
                                   .Select(AmbientLight.Views.ModeCatalog.Name).ToList();
    bool namesOk = tileNames.SequenceEqual(expectNames);
    Console.WriteLine((namesOk ? "OK   " : "FAIL ") +
                      $"磁贴文案 == 模式表里的名字 = {namesOk}" +
                      (namesOk ? "" : $"  (实得 [{string.Join(", ", tileNames)}])"));
    // 主模式卡: 同为 XAML 静态声明 —— 三张、都带 ModeCardTemplate、每张有 Tag(=模式号, 供选中态同步)
    var cardGrid = home.FindName("PrimaryModeGrid") as Panel;
    var modeCards = cardGrid?.Children.OfType<System.Windows.Controls.RadioButton>().ToList() ?? [];
    bool cardsOk = modeCards.Count == 3
        && modeCards.All(c => c.Template is ControlTemplate
                              && c.Tag is string s && int.TryParse(s, out _));
    Console.WriteLine((cardsOk ? "OK   " : "FAIL ") +
                      $"主模式卡 {modeCards.Count} 张 (期望 3), 均带模板与 Tag = {cardsOk}");
    // 折叠头右侧的「当前 XX」胶囊: 非 MCU 模式时必须收起 (收起状态靠它一眼看出用哪款)
    var fxPill = home.FindName("FxCurrentPill") as UIElement;
    var fxPillText = home.FindName("FxCurrentText") as TextBlock;
    bool pillOk = fxPill is not null && fxPillText is not null
                  && fxPill.Visibility == Visibility.Collapsed;
    Console.WriteLine((pillOk ? "OK   " : "FAIL ") +
                      $"「当前 XX」胶囊: 存在={fxPill is not null}, 非 MCU 模式默认可见性={fxPill?.Visibility} (期望 Collapsed)");

    Console.WriteLine();
    Console.WriteLine("---- §3 纯色拾色器: HSV (H+S 两根; V 就是主页「亮度」行) ----");
    var hueSlider = home.FindName("HueSlider") as Slider;
    var satSlider = home.FindName("SatSlider") as Slider;
    bool noRgb = home.FindName("SRSlider") == null && home.FindName("SGSlider") == null
                 && home.FindName("SBSlider") == null;
    Console.WriteLine((noRgb ? "OK   " : "FAIL ") + $"RGB 三根滑块已删除 = {noRgb}");
    bool hsvRanges = hueSlider is { Minimum: 0, Maximum: 360 } && satSlider is { Minimum: 0, Maximum: 100 };
    Console.WriteLine((hsvRanges ? "OK   " : "FAIL ") +
                      $"H 范围 {hueSlider?.Minimum}-{hueSlider?.Maximum} (期望 0-360), " +
                      $"S 范围 {satSlider?.Minimum}-{satSlider?.Maximum} (期望 0-100)");
    // 轨道被"透明化"覆盖是这套做法的关键: 渐变由下层 Border 承担, 若覆盖失效, 不透明轨道会把
    // 渐变整个盖住 → 功能静默失效 (不抛异常)。
    bool trackOverridden = hueSlider?.Resources["SliderTrackFill"] is SolidColorBrush hb && hb.Color.A == 0
                           && satSlider?.Resources["SliderTrackFill"] is SolidColorBrush sb && sb.Color.A == 0;
    Console.WriteLine((trackOverridden ? "OK   " : "FAIL ") +
                      $"H/S 滑块自身轨道已覆盖为透明 = {trackOverridden} (否则渐变被盖住, 静默失效)");
    // 上面只证明"资源写对了"; 这条进一步证明模板真的取到了它 (WPF-UI 4.3.0 Slider 模板里
    // 轨道是 Border#TrackBackground, Background = DynamicResource SliderTrackFill)。
    hueSlider?.ApplyTemplate();
    var trackBorder = hueSlider?.Template.FindName("TrackBackground", hueSlider) as System.Windows.Controls.Border;
    bool trackPainted = trackBorder?.Background is SolidColorBrush { Color.A: 0 };
    Console.WriteLine((trackPainted ? "OK   " : "FAIL ") +
                      $"实际轨道 Border#TrackBackground.Background = {trackBorder?.Background} (期望透明)");
    Console.WriteLine();
    Console.WriteLine("---- §3 预设色板 (自绘圆形 + 覆写悬停 + 预设/自定义分区) ----");
    var presetStrip = home.FindName("PresetStrip") as Panel;   // 已改为 XAML 静态子元素 (WrapPanel)
    var presetTpl = home.TryFindResource("PresetSwatchTemplate");
    var presetBtns = presetStrip?.Children.OfType<Button>().ToList() ?? [];
    // 只断言**机制与规则**, 不断言条数/几何数值 —— 用户加个预设、调个尺寸不该让探针报警。
    //   ① 每个色块都用自绘模板: 否则退回默认 Button 模板, 悬停会把色块铺成灰色 (真 bug);
    //   ② 预设与自定义之间有竖向分割线 ("分区");
    //   ③ 自绘模板里确实是圆 (Ellipse), 且悬停由模板自己的触发器接管。
    bool swatchTplOk = presetTpl is ControlTemplate
                       && presetBtns.Count > 0
                       && presetBtns.All(b => ReferenceEquals(b.Template, presetTpl));
    bool hasDivider = presetStrip?.Children.OfType<System.Windows.Shapes.Rectangle>().Any() == true;
    Console.WriteLine((swatchTplOk ? "OK   " : "FAIL ") +
                      $"色板 {presetBtns.Count} 个色块全部使用 PresetSwatchTemplate = {swatchTplOk}");
    Console.WriteLine((hasDivider ? "OK   " : "FAIL ") +
                      $"预设与自定义之间有竖向分割线 = {hasDivider}");
    // 空槽 = Tag 未设置; 记过色的槽 Tag = 颜色 (点它才会下发)
    int customSlots = presetBtns.Count(b => b.Tag is null);
    Console.WriteLine((customSlots > 0 ? "OK   " : "FAIL ") +
                      $"自定义色槽 {customSlots} 个 (空槽不可点, 不继承前任的颜色)");
    int ellipses = 0;
    bool hoverOnly = false;
    if (presetTpl is ControlTemplate presetTplX)
    {
        var probeBtn = new Button { Template = presetTplX, Background = Brushes.Red, BorderBrush = Brushes.Blue };
        var probeHost = new ContentControl { Content = probeBtn, Width = 40 };
        probeHost.Measure(new Size(40, 40));
        probeHost.Arrange(new Rect(0, 0, 40, 40));
        probeBtn.ApplyTemplate();
        void Walk(DependencyObject d)
        {
            if (d is System.Windows.Shapes.Ellipse) ellipses++;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(probeBtn);
        hoverOnly = presetTplX.Triggers.OfType<Trigger>()
            .Any(t => t.Property == UIElement.IsMouseOverProperty);
    }
    Console.WriteLine((ellipses >= 1 ? "OK   " : "FAIL ") +
                      $"色块是自绘圆形 (Ellipse 数 = {ellipses}; 用默认模板会变成方形+灰底)");
    Console.WriteLine((hoverOnly ? "OK   " : "FAIL ") +
                      $"悬停行为已由自绘触发器接管 = {hoverOnly} (不再套用默认模板的灰色高亮)");
    var hueBar = (home.FindName("HueTrackBar") as Border)?.Background as GradientBrush;
    Console.WriteLine((hueBar is LinearGradientBrush && hueBar.GradientStops.Count >= 2 ? "OK   " : "FAIL ") +
                      $"色相轨道是一个多段渐变 = {hueBar?.GetType().Name} / {hueBar?.GradientStops.Count} stops " +
                      "(不是纯色条 → 轨道被不透明轨道盖住的话这里也不为 null, 所以还要看下面那条)");
    var satBar = (home.FindName("SatTrackBar") as Border)?.Background as GradientBrush;
    var satStops = satBar?.GradientStops;
    // **规则**: S 轨道必须真的跟着 H 走 —— 起点 = 白 (HSV 里 S=0、V=1 就是白),
    // 终点 = 当前 H 的全饱和色。不断言具体 stop 数或颜色字面值。
    var hsvEnd = AmbientLight.Views.HomePage.HsvToRgb(hueSlider?.Value ?? 0, 1.0, 1.0);
    bool satLinked = satStops is { Count: >= 2 }
        && satStops[0].Color == Colors.White
        && satStops[^1].Color == Color.FromRgb((byte)hsvEnd.r, (byte)hsvEnd.g, (byte)hsvEnd.b);
    Console.WriteLine((satLinked ? "OK   " : "FAIL ") +
                      $"饱和度轨道 白 → 当前 H 全饱和色 ({satStops?[0].Color} → {satStops?[^1].Color}, " +
                      $"当前 H 应为 #{hsvEnd.r:X2}{hsvEnd.g:X2}{hsvEnd.b:X2})");
    // RGB↔HSV 往返: 拾色器不存 HSV, 全靠这两个函数互转 —— 写错只会**静默偏色**。
    // 用"任意取样都能回到原值"来自证 (±1/通道), 不写死某个颜色的期望输出。
    var samples = new (int r, int g, int b)[]
    {
        (255, 225, 214), (0, 0, 0), (255, 255, 255), (18, 52, 86), (200, 30, 30), (7, 7, 9), (0, 128, 255),
    };
    var rtBad = new List<string>();
    foreach (var (p0, p1, p2) in samples)
    {
        var (h2, s2, v2) = AmbientLight.Views.HomePage.RgbToHsv(p0, p1, p2);
        var (br2, bg2, bb2) = AmbientLight.Views.HomePage.HsvToRgb(h2, s2, v2);
        if (Math.Abs(br2 - p0) > 1 || Math.Abs(bg2 - p1) > 1 || Math.Abs(bb2 - p2) > 1)
            rtBad.Add($"#{p0:X2}{p1:X2}{p2:X2}→#{br2:X2}{bg2:X2}{bb2:X2}");
    }
    Console.WriteLine((rtBad.Count == 0 ? "OK   " : "FAIL ") +
                      $"RGB↔HSV 往返自证 {samples.Length} 组 = " +
                      (rtBad.Count == 0 ? "全部回到原值 (±1/通道)" : string.Join(", ", rtBad)));

    Console.WriteLine();
    Console.WriteLine("---- §4 banner 底边辉光带 (数值驱动 + alpha 叠加) ----");
    var glow = home.FindName("GlowBand") as System.Windows.Controls.Border;
    double glowInner = Math.Max(0, outerR - (banner?.BorderThickness.Top ?? 0));
    bool glowEffect = glow?.Effect is System.Windows.Media.Effects.BlurEffect;
    bool glowRound = glow is not null
                     && glow.CornerRadius.BottomLeft == glowInner
                     && glow.CornerRadius.BottomRight == glowInner;
    bool glowMask = glow?.OpacityMask is LinearGradientBrush;
    var glowStops = (glow?.Background as GradientBrush)?.GradientStops;
    Console.WriteLine((glowEffect ? "OK   " : "FAIL ") + $"辉光带带 BlurEffect = {glowEffect}");
    Console.WriteLine((glowRound ? "OK   " : "FAIL ") +
                      $"辉光带底圆角 = {glow?.CornerRadius.BottomLeft}/{glow?.CornerRadius.BottomRight} " +
                      $"(要求 {glowInner} = 外圆角 − 边框厚)");
    Console.WriteLine((glowMask ? "OK   " : "FAIL ") + $"辉光带上透下实的 OpacityMask = {glowMask}");
    // 关键: 辉光的亮度必须压在 **alpha** 上 —— 否则不亮的地方会变成一条黑带, 把 banner 压暗。
    // 口径: "应该是将自己的亮度叠加上去, 不亮的地方要透明"。
    // 判据用**不变量**表达 (端点透明 + 存在峰值 + 所有 stop 同色), 不写死段数与峰值下标。
    bool sameRgb = glowStops is { Count: >= 3 }
                   && glowStops.All(s => s.Color.R == glowStops[0].Color.R
                                         && s.Color.G == glowStops[0].Color.G
                                         && s.Color.B == glowStops[0].Color.B);
    bool bellOk = sameRgb
                  && glowStops![0].Color.A == 0 && glowStops[^1].Color.A == 0
                  && glowStops.Any(s => s.Color.A == 255);
    Console.WriteLine((bellOk ? "OK   " : "FAIL ") +
                      $"辉光画刷 {glowStops?.Count} stops: 端点 alpha={glowStops?[0].Color.A}/{glowStops?[^1].Color.A}" +
                      $" (要求 0), 有 alpha=255 的峰值 stop, 且所有 stop 同色 (亮度只由 alpha 表达) = {bellOk}");
    bool advancedHasGlowSwitch = advanced.FindName("BannerGlowToggle") != null;
    Console.WriteLine((advancedHasGlowSwitch ? "OK   " : "FAIL ") +
                      $"高级页「界面」组的 banner 辉光开关存在 = {advancedHasGlowSwitch}");

    Console.WriteLine();
    Console.WriteLine("---- §5 运行指标 (双层 / 去 CPU / 去着色) ----");
    var metricCases = new (int Mode, bool Running, string[] Expect, string Desc)[]
    {
        (0,  false, [],                     "未启动 → 空集 (页面显示一行灰字, 不铺 0.0)"),
        (0,  true,  ["proc", "wire", "fps"], "追光"),
        (1,  true,  ["audio", "wire"],      "拾音 (处理 ms 不适用 → 不入集)"),
        (11, true,  ["wire", "leds", "bright"], "纯色"),
        (6,  true,  ["cmd", "wire", "leds"], "固件特效 火焰燃烧"),
        (13, true,  ["cmd", "wire", "leds"], "固件特效 北极冰山"),
    };
    foreach (var c in metricCases)
    {
        var got = AmbientLight.Views.HomePage.MetricKeysForMode(c.Mode, c.Running);
        bool ok = got.SequenceEqual(c.Expect);
        Console.WriteLine((ok ? "OK   " : "FAIL ") + $"{c.Desc} → [{(got.Length == 0 ? "空" : string.Join(", ", got))}]" +
                          (ok ? "" : $"  (期望 [{string.Join(", ", c.Expect)}])"));
    }
    // CPU 从 M1 起就是 native 的"预留"字段 (从不赋值) → 任何模式的指标集里都不该再出现它
    bool noCpu = Enumerable.Range(0, 14).All(m => !AmbientLight.Views.HomePage.MetricKeysForMode(m, true).Contains("cpu"));
    Console.WriteLine((noCpu ? "OK   " : "FAIL ") + $"CPU 已从所有模式的指标集里移除 = {noCpu}");
    // 文案: **不断言具体措辞** (那是随手可改的产品文案), 只断言"每个指标键都配了专属标签" ——
    // 文案源在 Strings.zh.xaml; Loc.T 缺键时返回 "Metric.<key>" 本身 → 以此判漏配 (真 bug)。
    var allMetricKeys = Enumerable.Range(0, 14)
        .SelectMany(m => AmbientLight.Views.HomePage.MetricKeysForMode(m, true))
        .Distinct().ToList();
    var missingLabels = allMetricKeys
        .Where(k => AmbientLight.Views.HomePage.MetricLabel(k) == "Metric." + k).ToList();
    Console.WriteLine((missingLabels.Count == 0 ? "OK   " : "FAIL ") +
                      $"指标文案齐备 = {missingLabels.Count == 0}" +
                      (missingLabels.Count > 0 ? $" (缺: {string.Join(", ", missingLabels)})" : "") +
                      "  · 当前措辞: " +
                      string.Join(" / ", allMetricKeys.Select(k => $"{k}={AmbientLight.Views.HomePage.MetricLabel(k)}")));
    // 模式名/说明文案齐备 (14 个模式; 缺键会显示 key 本身)
    int modeNameMissing = AmbientLight.Views.ModeCatalog.All
        .Count(m => AmbientLight.Views.ModeCatalog.Name(m) == "Mode.Name." + m);
    bool descMissing = AmbientLight.Views.ModeCatalog.All.Any(m =>
        AmbientLight.Views.ModeCatalog.Desc(m).Length == 0);
    Console.WriteLine((modeNameMissing == 0 && !descMissing ? "OK   " : "FAIL ") +
                      $"模式名文案齐备 = {modeNameMissing == 0} (14 个), 说明非空 = {!descMissing}");
    // 文案字典已合并 (Loc 冒烟: 常用按钮能解析出真值)
    bool locMerged = AmbientLight.Localization.Loc.T("Common.GotIt") != "Common.GotIt";
    Console.WriteLine((locMerged ? "OK   " : "FAIL ") + $"Strings.zh.xaml 已合并 (Loc 冒烟) = {locMerged}");
    // 多行文案必须含**真换行**: 值里的 &#10; 若忘了配 xml:space="preserve" 会被 XAML 归一化成
    // 空格 (静默失效 —— 对话框里两段话挤成一行)。实测与复现工具 .workbuddy/tmp/nl-probe。
    var multilineKeys = new[] { "Dev.List.NotFound", "App.Launch.TooMany", "App.Launch.Failed",
                                "App.Crash.UiException", "App.Start.NotFound.Detail",
                                "Adv.Update.Body", "Adv.Restore.Body", "Calib.NeedSession" };
    var nlBad = multilineKeys.Where(k => !AmbientLight.Localization.Loc.T(k).Contains('\n')).ToList();
    Console.WriteLine((nlBad.Count == 0 ? "OK   " : "FAIL ") +
                      $"多行文案含真换行 {multilineKeys.Length} 条 (&#10; 配 xml:space=preserve): " +
                      (nlBad.Count == 0 ? "全部 ✓" : string.Join(", ", nlBad)));
    // 核心诊断映射: native 英文原文 → 字典中文 (Views/DiagText, 纯函数真值表)。
    // 命中的必须**真翻译** (输出 != 原文); 未收录的原文必须**原样透传** (宁显英文, 不吞信息)。
    var diagCases = new (string Raw, string Want)[]
    {
        ("serial write failed on COM5, reconnecting",
            AmbientLight.Localization.Loc.F("Diag.SerialWriteFailed", "COM5")),
        ("device lost: no usable port (VID_2E3C&PID_5740)",
            AmbientLight.Localization.Loc.T("Diag.NoUsablePort")),
        ("capture build failed (D3D/resources), slow retry",
            AmbientLight.Localization.Loc.T("Diag.CaptureFailed")),
        ("core thread crashed, see log/ambientcore.log",
            AmbientLight.Localization.Loc.T("Diag.CoreCrashed")),
    };
    var diagBad = diagCases.Where(c => AmbientLight.Views.DiagText.Translate(c.Raw) != c.Want)
                           .Select(c => c.Raw).ToList();
    bool diagTranslated = diagCases.All(c => AmbientLight.Views.DiagText.Translate(c.Raw) != c.Raw);
    bool diagPass = AmbientLight.Views.DiagText.Translate("future unknown message") == "future unknown message"
                    && AmbientLight.Views.DiagText.Translate("") == "";
    bool diagOk = diagBad.Count == 0 && diagTranslated && diagPass;
    Console.WriteLine((diagOk ? "OK   " : "FAIL ") +
                      $"核心诊断映射 {diagCases.Length} 条 (native → 字典, 命中即翻译) + 未知透传: " +
                      (diagOk ? "全部 ✓" : $"错配 {string.Join(", ", diagBad)} / 翻译 {diagTranslated} / 透传 {diagPass}"));
    // 双层结构: 栏数必须等于指标数 (数据驱动), 每栏 = 标签行 + 数值行, 数值初值不能是空/0
    // (0 会被读成"卡住了"); 等宽分栏。结构在 XAML 模板里 → 这里验"数据 → 模板 → 实际渲染"整条线。
    void CollectTextBlocks(DependencyObject d, List<TextBlock> into)
    {
        if (d is TextBlock t) into.Add(t);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            CollectTextBlocks(VisualTreeHelper.GetChild(d, i), into);
    }
    var metricsPanel = home.FindName("MetricsPanel") as ItemsControl;
    var metricsEmpty = home.FindName("MetricsEmptyText") as TextBlock;
    var keysForProbe = AmbientLight.Views.HomePage.MetricKeysForMode(0, true);
    home.RenderMetricsForProbe(keysForProbe);
    // 逼出容器 (ItemsControl 的容器在布局时生成; 控件不接窗口也能对它自身 Measure/Arrange)
    metricsPanel!.Measure(new Size(800, 120));
    metricsPanel.Arrange(new Rect(0, 0, 800, 120));
    metricsPanel.UpdateLayout();
    var cellTexts = new List<List<TextBlock>>();
    for (int i = 0; i < metricsPanel.Items.Count; i++)
    {
        var tbs = new List<TextBlock>();
        if (metricsPanel.ItemContainerGenerator.ContainerFromIndex(i) is { } cont)
            CollectTextBlocks(cont, tbs);
        cellTexts.Add(tbs);
    }
    bool twoLine = cellTexts.Count == keysForProbe.Length && cellTexts.All(t =>
        t.Count == 2 && t[0].Text.Length > 0 && t[1].Text.Length > 0 && t[1].Text != "0");
    bool equalCols = metricsPanel.ItemsPanel.LoadContent()
                     is System.Windows.Controls.Primitives.UniformGrid { Rows: 1 };
    Console.WriteLine((twoLine ? "OK   " : "FAIL ") +
                      $"双层结构: {cellTexts.Count} 栏 == 指标数 {keysForProbe.Length}, 每栏 2 行 (标签/数值), " +
                      $"数值初值非空非 0 = {twoLine}");
    Console.WriteLine((equalCols ? "OK   " : "FAIL ") +
                      $"等宽分栏 (UniformGrid 单行) = {equalCols}");
    // 接线断言: 改数据项 → 看**渲染出来的**文本是否跟随 (证明模板里的绑定真的活着)
    bool valueWired = false;
    if (metricsPanel.Items.Count > 0
        && metricsPanel.Items[0] is AmbientLight.Views.HomePage.MetricCell mc
        && cellTexts.Count > 0 && cellTexts[0].Count == 2)
    {
        mc.Value = "接线探针";
        valueWired = cellTexts[0][1].Text == "接线探针";
    }
    Console.WriteLine((valueWired ? "OK   " : "FAIL ") +
                      $"指标值绑定是活的 (改数据项 → 渲染文本跟随) = {valueWired}");
    // 没指标时 (灯带没开) 收成一行占位, 而不是铺 N 栏空值; 同时指标卡本身要挂着 AnimatedVisibility
    // 且初始是**收起**的 (关闭时整块高度动画收掉, 与拾音页「输入/输出设备」同一套机制)
    home.RenderMetricsForProbe([]);
    bool emptyCollapsed = metricsPanel.Items.Count == 0
                          && metricsEmpty?.Visibility == Visibility.Visible;
    var metricsCard = home.FindName("MetricsCard") as FrameworkElement;
    bool cardWired = metricsCard != null
                     && !AmbientLight.Controls.AnimatedVisibility.GetExpanded(metricsCard);
    Console.WriteLine((emptyCollapsed ? "OK   " : "FAIL ") +
                      $"空指标集 → 单行灰字占位 (不铺空值) = {emptyCollapsed}");
    Console.WriteLine((cardWired ? "OK   " : "FAIL ") +
                      $"指标卡挂 AnimatedVisibility 且初始收起 = {cardWired} " +
                      "(与拾音页「输入/输出设备」同一套收展机制)");

    Console.WriteLine();
    Console.WriteLine("---- §7 布局预览 (折叠, 中心只放静态几何参数) ----");
    var layoutCard = home.FindName("LayoutCard") as Expander;
    Console.WriteLine((layoutCard is { IsExpanded: false } ? "OK   " : "FAIL ") +
                      $"LayoutCard 默认折叠 = {!(layoutCard?.IsExpanded ?? true)}");
    Console.WriteLine((home.FindName("LayoutSummary") is TextBlock ? "OK   " : "FAIL ") +
                      "布局预览 Header 摘要元素存在 (收起时显示 「N 灯 · M 边」)");
    Console.WriteLine((home.FindName("Ring") is not null ? "OK   " : "FAIL ") +
                      "几何环仍在 LayoutCard 内 (M4 的真实比例几何原样保留)");
    // 校准向导的"安装示意" (替换了旧的"起点 0 号 + ↺ 逆时针"一行小字):
    // 环线 + 箭头表示绕向, "起点"徽标贴在 0 号灯那个**角**上。这里断三件事:
    //   ① 起点角/镜像规则 (纯函数, 与核心段序同构); ② 示意的可见性 (线/箭头 vs 屏幕底、徽标字 vs 徽标底);
    //   ③ ★接线: 真的画一遍, 断言"徽标的位置真的跟着绕向跑到另一侧"。
    Console.WriteLine();
    Console.WriteLine("---- 校准向导安装示意 (环线 + 箭头 + 起点徽标) ----");
    var calRing = new AmbientLight.Controls.LedRingView();
    bool calRingFindOk = calRing.FindName("OrientationRow") is null;   // 旧那行文字应已从 XAML 移除
    Console.WriteLine((calRingFindOk ? "OK   " : "FAIL ") +
                      $"旧的那行文字已移除 (OrientationRow 不存在) = {calRingFindOk}");
    bool cornerRule = AmbientLight.Controls.LedRingView.StartCornerOf(0)
                          == AmbientLight.Controls.LedRingView.RingStartCorner.BottomRight
                      && AmbientLight.Controls.LedRingView.StartCornerOf(1)
                          == AmbientLight.Controls.LedRingView.RingStartCorner.BottomLeft
                      && !AmbientLight.Controls.LedRingView.MirrorGuide(0)
                      && AmbientLight.Controls.LedRingView.MirrorGuide(1);
    Console.WriteLine((cornerRule ? "OK   " : "FAIL ") +
                      $"起点角规则 (逆时针=右下 / 顺时针=左下) 与镜像规则 = {cornerRule}");
    var guideRing = new AmbientLight.Controls.LedRingView
    {
        Surface = AmbientLight.Controls.LedRingView.RingSurface.AlwaysDark,
    };
    guideRing.Configure(60, 4, direction: 0);
    guideRing.SetOrientation(true, direction: 0);          // 先打开示意 (主页默认是关的)
    var guideCanvas = guideRing.FindName("RingCanvas") as System.Windows.Controls.Canvas;
    var guideShapes = guideCanvas?.Children.OfType<System.Windows.Shapes.Path>().ToArray() ?? [];
    var guideBadge = guideCanvas?.Children.OfType<System.Windows.Controls.Border>()
                              .FirstOrDefault(b => (b.Child as TextBlock)?.Text == "起点");
    // 两个 Path: 环线(描边) 与 箭头(填充)。★ 环线必须是**开口**路径 —— 设计要求
    // "绕向线在起点徽标处断开" (像灯带在这里起头/收尾), 所以断口不能靠遮罩糊, 得真是开口。
    var loopPath = guideShapes.FirstOrDefault(p => p.Stroke is not null);
    var headPath = guideShapes.FirstOrDefault(p => p.Fill is not null);
    bool loopOpen = (loopPath?.Data as PathGeometry)?.Figures.FirstOrDefault()?.IsClosed == false;
    bool guideDrawn = loopOpen && headPath is not null && guideBadge is not null;
    Console.WriteLine((guideDrawn ? "OK   " : "FAIL ") +
                      $"画布上: 环线是开口路径(在起点处断开={loopOpen}) + 箭头 + 「起点」徽标 = {guideDrawn}");
    double badgeLeftDir0 = guideBadge is null ? 0 : System.Windows.Controls.Canvas.GetLeft(guideBadge);

    guideRing.SetOrientation(true, direction: 1);            // 换成顺时针: 徽标应跑到另一侧
    var badgeDir1 = guideCanvas?.Children.OfType<System.Windows.Controls.Border>()
                              .FirstOrDefault(b => (b.Child as TextBlock)?.Text == "起点");
    double badgeLeftDir1 = badgeDir1 is null ? 0 : System.Windows.Controls.Canvas.GetLeft(badgeDir1);
    // 两个方向的几何都要"对称且形状正确":
    //   ① 环线包围盒 ≈ 指定内缩后的矩形 (圆角不外鼓 → 包围盒正好等于矩形);
    //      —— 这条能抓住"sweep 写反 → 圆角朝外鼓"这类**位置对、形状错**的 bug
    //      (第一版顺时针那条就是这么错的: 包围盒只大一点点, 只有渲染成图才看出来);
    //   ② 箭头/徽标左右对称 (镜像关系)。
    double? loopW = null, loopH = null, loopX = null;
    var arrowX = new(double X, int Dir)[2];
    var badgeX = new double[2];
    foreach (var d in new[] { 0, 1 })
    {
        guideRing.SetOrientation(true, d);
        var lo = guideCanvas?.Children.OfType<System.Windows.Shapes.Path>()
                             .FirstOrDefault(x => x.Stroke is not null);
        var he = guideCanvas?.Children.OfType<System.Windows.Shapes.Path>()
                             .FirstOrDefault(x => x.Fill is not null);
        var bd = guideCanvas?.Children.OfType<System.Windows.Controls.Border>()
                             .FirstOrDefault(b => (b.Child as TextBlock)?.Text == "起点");
        var b = lo?.Data.Bounds ?? default;
        if (d == 1)   // dir 1 的期望值拿来跟 dir 0 比 (两者应完全相同)
        {
            bool same = loopW is { } lw && Math.Abs(b.Width - lw) < 0.5
                        && loopH is { } lh && Math.Abs(b.Height - lh) < 0.5
                        && loopX is { } lx && Math.Abs(b.X - lx) < 0.5;
            Console.WriteLine((same ? "OK   " : "FAIL ") +
                              $"两个方向的环线几何一致 (顺时针那条的圆角没朝外鼓) = {same} · " +
                              $"{b.X:F1},{b.Y:F1} {b.Width:F1}x{b.Height:F1}");
        }
        else { loopW = b.Width; loopH = b.Height; loopX = b.X; }
        arrowX[d] = (System.Windows.Controls.Canvas.GetLeft(he), d);
        badgeX[d] = System.Windows.Controls.Canvas.GetLeft(bd);
    }
    // 对称: 徽标在右侧时的箭头 x + 徽标在左侧时的箭头 x ≈ 环线左右边界之和 - 箭头宽 - 间距差
    // 这里只做"顺时针的箭头比逆时针的更靠左"这个弱断言 (强断言需要环线边界, 用上面那条兜)
    bool arrowMirrored = arrowX[1].X < arrowX[0].X;
    bool badgeMirrored = badgeX[1] < badgeX[0];
    Console.WriteLine((arrowMirrored && badgeMirrored ? "OK   " : "FAIL ") +
                      $"箭头与徽标都跟着绕向换侧 (箭头 x {arrowX[0].X:F0}→{arrowX[1].X:F0} · " +
                      $"徽标 x {badgeX[0]:F0}→{badgeX[1]:F0})");
    var badgeBg = (badgeDir1?.Background as SolidColorBrush)?.Color;
    var badgeFg = (badgeDir1?.Child as TextBlock)?.Foreground is SolidColorBrush fgB ? fgB.Color : (Color?)null;
    var headFill = (headPath?.Fill as SolidColorBrush)?.Color;
    var wantGc = AmbientLight.Controls.LedRingView.GuideColors(true);
    Console.WriteLine($"INFO 徽标实测 bg={badgeBg} fg={badgeFg} · 箭头 fill={headFill} · " +
                      $"期望(深面) bg={wantGc.BadgeBg} fg={wantGc.BadgeFg}");
    guideRing.SetOrientation(false, 0);
    bool guideGone = (guideCanvas?.Children.OfType<System.Windows.Shapes.Path>().Count() ?? 1) == 0
                     && !(guideCanvas?.Children.OfType<System.Windows.Controls.Border>()
                                       .Any(b => (b.Child as TextBlock)?.Text == "起点") ?? true);
    Console.WriteLine((guideGone ? "OK   " : "FAIL ") + $"SetOrientation(false) 后示意全部撤掉 = {guideGone}");
    foreach (var dark in new[] { true, false })
    {
        var gcp = AmbientLight.Controls.LedRingView.GuideColors(dark);
        var screenFill = Color.FromRgb(dark ? (byte)0x10 : (byte)0xF5,
                                       dark ? (byte)0x10 : (byte)0xF5,
                                       dark ? (byte)0x16 : (byte)0xF5);
        double lineD = Math.Abs(AmbientLight.Services.ThemePalette.RelativeLuminance(
                                    AmbientLight.Services.ThemePalette.Blend(gcp.Line, screenFill))
                                - AmbientLight.Services.ThemePalette.RelativeLuminance(screenFill));
        // 徽标底是**半透明**的 → 必须先把"底"合成到屏幕底上, 再拿字去比 (否则 RGB 相同的两个色 → 1.00)
        var badgeOnScreen = AmbientLight.Services.ThemePalette.Blend(gcp.BadgeBg, screenFill);
        double badgeCr = AmbientLight.Services.ThemePalette.ContrastRatio(
            AmbientLight.Services.ThemePalette.Blend(gcp.BadgeFg, badgeOnScreen), badgeOnScreen);
        Console.WriteLine((lineD >= 0.05 && badgeCr >= 4.5 ? "OK   " : "FAIL ") +
                          $"{(dark ? "深面" : "浅面")}: 环线在屏幕底上可见 (|ΔL| {lineD:F3} ≥ 0.05) · " +
                          $"徽标字对比度 {badgeCr:F2} (≥ 4.5)");
    }

    Console.WriteLine();
    Console.WriteLine("---- 布局/磁贴补充项 (只验规则, 不验数值/文案) ----");
    // 磁贴"内容居中"是明确的设计要求 → 断言 ContentPresenter 的对齐 (枚举语义), 不是坐标数字。
    // 不断言"磁贴组没有居中"这类**未改动**断言 —— 那只会让下次调整布局时误报。
    var fxTplX = home.TryFindResource("FxTileTemplate") as ControlTemplate;
    bool contentCentered = false;
    if (fxTplX != null)
    {
        var fxProbe = new RadioButton
        {
            Template = fxTplX, Content = "测试", Width = 152, Height = 56,
        };
        var fxHost = new ContentControl { Content = fxProbe, Width = 152, Height = 56 };
        fxHost.Measure(new Size(152, 56));
        fxHost.Arrange(new Rect(0, 0, 152, 56));
        fxProbe.ApplyTemplate();
        ContentPresenter? cp = null;
        void WalkCp(DependencyObject d)
        {
            if (cp == null && d is ContentPresenter c) cp = c;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) WalkCp(VisualTreeHelper.GetChild(d, i));
        }
        WalkCp(fxProbe);
        contentCentered = cp?.HorizontalAlignment == HorizontalAlignment.Center;
    }
    Console.WriteLine((contentCentered ? "OK   " : "FAIL ") +
                      $"固件特效磁贴**内**的内容水平居中 = {contentCentered}");
    // 显示器行: 用**合成输入**验"输入里的东西都被显示出来 + 序号从 1 起 + 不再出现设备名",
    // 不再写死本机分辨率/显卡型号。
    try
    {
        const string probeAdapter = "ProbeAdapter X";
        const int probeW = 1234, probeH = 567;
        var mrow = home.BuildMonitorRowForProbe(1, probeAdapter, probeW, probeH);
        var mhost = new ContentControl { Content = mrow, Width = 700 };
        mhost.Measure(new Size(700, 60));
        mhost.Arrange(new Rect(0, 0, 700, 60));
        var texts = new List<TextBlock>();
        bool anyToolTip = false, badgeCircle = false;
        void WalkMon(DependencyObject d)
        {
            if (d is TextBlock t) texts.Add(t);
            // 徽标是正圆: CornerRadius 必须等于半边长 (这条是**自证**的, 改尺寸也不会误报)
            if (d is System.Windows.Controls.Border b && b.Width > 0
                && Math.Abs(b.CornerRadius.TopLeft - b.Width / 2) < 0.01 && b.Child is TextBlock)
                badgeCircle = true;
            if (d is FrameworkElement fe && fe.ToolTip != null) anyToolTip = true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) WalkMon(VisualTreeHelper.GetChild(d, i));
        }
        WalkMon(mrow);
        // [diag] 绑定没生效时看这里: 数据项到底有没有挂上
        var rowItem = ((mrow as FrameworkElement)?.DataContext
                       ?? (mrow as ContentPresenter)?.Content) as AmbientLight.Views.HomePage.MonitorRowItem;
        Console.WriteLine($"       [diag] 行根={mrow.GetType().Name}, 数据项=" +
                          (rowItem is null ? "(null)" : $"#{rowItem.Number} / {rowItem.ResolutionText}"));
        string joined = string.Join(" | ", texts.Select(t => t.Text));
        bool resOk = texts.Any(t => t.Text.Contains($"{probeW}×{probeH}"));
        bool adOk = texts.Any(t => t.Text.Contains(probeAdapter));
        bool noDispName = !joined.Contains("DISPLAY");
        Console.WriteLine((resOk && adOk && noDispName ? "OK   " : "FAIL ") +
                          $"显示器行显示传入的分辨率/适配器 = {resOk && adOk}, 不含设备名 = {noDispName}" +
                          $"  · 实际文本: {joined}");
        Console.WriteLine((badgeCircle ? "OK   " : "FAIL ") +
                      $"编号徽标是正圆 (CornerRadius == 半边长) = {badgeCircle}");
        Console.WriteLine((texts.Any(t => t.Text == "2") ? "OK   " : "FAIL ") +
                      $"编号显示 index+1 = {texts.Any(t => t.Text == "2")} (序号从 1 起)");
        Console.WriteLine((!anyToolTip ? "OK   " : "FAIL ") + $"显示器行已无悬浮提示 = {!anyToolTip}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL 显示器行: {ex.GetType().Name}: {ex.Message}");
    }
    // 校准向导必须在采集页, 且**紧跟在灯序偏移下面**; 高级页不再有它。
    // 行标题用**子串**匹配 (容忍以后改措辞), 只断言结构与顺序。
    var capturePage = new AmbientLight.Views.SettingsPageCapture();
    var advPage = new AmbientLight.Views.SettingsPageAdvanced();
    List<string> TitlesOf(object page) => LogicalDescendants((DependencyObject)page)
        .OfType<AmbientLight.Controls.SettingRow>().Select(r => r.Title).ToList();
    var capTitles = TitlesOf(capturePage);
    var advTitles = TitlesOf(advPage);
    int iRot = capTitles.FindIndex(t => t.Contains("灯序"));
    int iCal = capTitles.FindIndex(t => t.Contains("校准"));
    bool inAdv = advTitles.Any(t => t.Contains("校准"));
    bool movedOk = iRot >= 0 && iCal == iRot + 1 && !inAdv;
    Console.WriteLine((movedOk ? "OK   " : "FAIL ") +
                      $"校准向导在采集页且紧接灯序偏移之后 = {movedOk}" +
                      $" (位置 {iCal} / 灯序 {iRot}; 高级页{(inAdv ? "仍有" : "已无")})");
    // 颜色页「色相强度」必须**有**说明文案 (断言"非空", 不断言措辞)
    var colorPage = new AmbientLight.Views.SettingsPageColor();
    var hueRow = LogicalDescendants(colorPage).OfType<AmbientLight.Controls.SettingRow>()
        .FirstOrDefault(r => r.Title.Contains("色相"));
    bool hueDesc = !string.IsNullOrWhiteSpace(hueRow?.Description);
    Console.WriteLine((hueDesc ? "OK   " : "FAIL ") +
                      $"「色相强度」有说明文案 = {hueDesc}  · 当前: {hueRow?.Description}");

    Console.WriteLine();
    Console.WriteLine("---- 设备行胶囊口径 (有会话时必须看会话状态) ----");
    // 两个踩过的现象都由这条规则解释: 拔线后 slot 还在 → 旧代码一律"运行中";
    // 托盘关灯 → 会话消失但当时没有刷新信号。这里只断言**色调**(严重度), 文案只打印 ——
    // 措辞随手可改, 而"灯带不在位不能显示成绿色"是硬的。
    var pillWithSlot = new (int State, string Desc, AmbientLight.Views.HomePage.PillTone Expect)[]
    {
        (AmbientCore.StateRunning, "运行中", AmbientLight.Views.HomePage.PillTone.Green),
        (AmbientCore.StateStarting, "正在连接", AmbientLight.Views.HomePage.PillTone.Blue),
        (AmbientCore.StateWaitDisp, "锁屏/熄屏", AmbientLight.Views.HomePage.PillTone.Gray),
        (AmbientCore.StateDevLost, "灯带被拔掉", AmbientLight.Views.HomePage.PillTone.Amber),
        (AmbientCore.StateError, "不可恢复错误", AmbientLight.Views.HomePage.PillTone.Red),
        (AmbientCore.StateStopped, "已停止", AmbientLight.Views.HomePage.PillTone.Gray),
    };
    foreach (var c in pillWithSlot)
    {
        var (label, tone) = AmbientLight.Views.HomePage.DevicePill(
            hasSlot: true, state: c.State, inUse: true, devType: 0, likelyOurs: false);
        Console.WriteLine((tone == c.Expect ? "OK   " : "FAIL ") +
                          $"有会话 + {c.Desc} → 色调 {tone} (期望 {c.Expect}) · 文案「{label}」");
    }
    var pillNoSlot = new (bool InUse, int DevType, bool Ours, AmbientLight.Views.HomePage.PillTone Expect, string Desc)[]
    {
        (true, -1, true, AmbientLight.Views.HomePage.PillTone.Blue, "无会话 + 端口被占 + 自己正在起"),
        (true, -1, false, AmbientLight.Views.HomePage.PillTone.Amber, "无会话 + 端口被别人占"),
        (false, -1, false, AmbientLight.Views.HomePage.PillTone.Amber, "无会话 + 握手无应答"),
        (false, 0, false, AmbientLight.Views.HomePage.PillTone.Gray, "无会话 + 设备可用"),
    };
    foreach (var c in pillNoSlot)
    {
        var (label, tone) = AmbientLight.Views.HomePage.DevicePill(
            hasSlot: false, state: AmbientCore.StateStopped, inUse: c.InUse, devType: c.DevType, likelyOurs: c.Ours);
        Console.WriteLine((tone == c.Expect ? "OK   " : "FAIL ") +
                          $"{c.Desc} → 色调 {tone} (期望 {c.Expect}) · 文案「{label}」");
    }

    Console.WriteLine();
    Console.WriteLine("---- 设备行 ↔ 会话配对 (纯函数真值表) ----");
    // 场景: 手动指定一个"在位但不是灯带"的口后开启 → banner 显示正在重连、诊断说未应答,
    // 但**设备行胶囊仍是「就绪」**。根因: 配对原来只看端口, 而连不上的会话 `ACStatus.comPort`
    // 是空的 (端口只在握手成功时回填) → 行认不出这个会话 → 显示乐观的「就绪」。
    // 规则: ① 设备 key 相同 > ② 端口相同 > ③ 端口被占且恰有一个非停止会话(兜底)。
    const int run = AmbientCore.StateRunning, lost = AmbientCore.StateDevLost, stop = AmbientCore.StateStopped;
    var oneLostSession = new List<(int Slot, string Key, string SessionPort, int State)>
    {
        (0, "0_1", "", lost),                 // 连不上: 端口为空
    };
    var twoSessions = new List<(int Slot, string Key, string SessionPort, int State)>
    {
        (0, "0_1", "", lost), (1, "0_2", "COM6", run),
    };
    var matchCases = new (string RowKey, string RowPort, bool InUse,
                          List<(int Slot, string Key, string SessionPort, int State)> Sess,
                          int Expect, string Desc)[]
    {
        ("0_1", "COM5", false, oneLostSession, 0,
         "★ key 相同 (会话端口为空) → 仍配上 (用户踩的这条)"),
        ("0_9", "COM5", false, oneLostSession, -1,
         "key 不同 + 端口不同 + 端口没被占 → 不配 (不能张冠李戴)"),
        ("0_9", "COM6", false, twoSessions, 1, "key 不同但端口相同 → 按端口配"),
        ("0_9", "COM5", true, oneLostSession, 0, "端口被占 + 恰一个非停止会话 → 兜底归属"),
        ("0_9", "COM5", true, twoSessions, -1, "端口被占 + 两个会话 → 无法归属, 不配"),
        ("0_9", "COM5", false, twoSessions, -1, "端口没被占 → 不做兜底"),
        ("0_1", "COM5", false, new List<(int, string, string, int)> { (0, "0_1", "COM5", stop) }, -1,
         "已停止的会话不算 (state==STOPPED 跳过)"),
        ("0_1", "COM5", false, twoSessions, 0, "key 优先于端口 (两个会话都有时按 key 挑)"),
    };
    foreach (var c in matchCases)
    {
        int got = AmbientLight.Views.HomePage.MatchSessionSlot(c.RowKey, c.RowPort, c.InUse, c.Sess);
        Console.WriteLine((got == c.Expect ? "OK   " : "FAIL ") +
                          $"{c.Desc} → slot {got} (期望 {c.Expect})");
    }

    Console.WriteLine();
    Console.WriteLine("---- 胶囊的两条端口规则 (手动指定端口) ----");
    // 场景: 手动指定端口后胶囊还显示「就绪」。规则:
    //   ① 配置的口**本机不存在** → 「端口不在位」(不点开启就知道连不上);
    //   ② 有会话 + DEV_LOST + 手动指定 → 「指定端口无应答」(而不是"灯带不在位" —— 那暗示灯带被拔了);
    //   ③ "口在位但不是灯带"**不做**预检 (要真开端口试握手) → 只能等开启后由诊断说明。
    var portPillCases = new (bool HasSlot, int State, bool Manual, bool Missing, string Expect, string Desc)[]
    {
        (false, AmbientCore.StateStopped, true, true, "端口不在位", "无会话 + 指定口不在位"),
        (false, AmbientCore.StateStopped, true, false, "就绪", "无会话 + 指定口在位 (设备可用)"),
        (true, AmbientCore.StateDevLost, true, false, "指定端口无应答", "有会话 DEV_LOST + 手动指定"),
        (true, AmbientCore.StateDevLost, false, false, "灯带不在位", "有会话 DEV_LOST + 自动探测 (原文案不变)"),
        (true, AmbientCore.StateRunning, true, false, "运行中", "连上了 → 状态优先, 与端口规则无关"),
    };
    foreach (var c in portPillCases)
    {
        var (label, _) = AmbientLight.Views.HomePage.DevicePill(
            hasSlot: c.HasSlot, state: c.State, inUse: false, devType: 0, likelyOurs: false,
            manualPort: c.Manual, portMissing: c.Missing);
        Console.WriteLine((label == c.Expect ? "OK   " : "FAIL ") +
                          $"{c.Desc} → 「{label}」 (期望「{c.Expect}」)");
    }

    Console.WriteLine();
    Console.WriteLine("---- §8.3 连接: 端口下拉 (纯函数 + 行存在) ----");
    var portItems = AmbientLight.Views.SettingsPageAdvanced.BuildPortItems("COM7", ["COM5", "COM10", "com5"]);
    bool autoFirst = portItems.Count > 0 && portItems[0].Port == null
                     && AmbientLight.Views.SettingsPageAdvanced.PortValueOf(portItems[0]) == "";
    bool allPresent = portItems.Any(i => i.Port == "COM5") && portItems.Any(i => i.Port == "COM10");
    bool keepsConfigured = portItems.Any(i => i.Port == "COM7");   // 不在位也要留着, 别吞掉用户设置
    bool noDup = portItems.Select(i => i.Port ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count()
                 == portItems.Count;
    Console.WriteLine((autoFirst ? "OK   " : "FAIL ") +
                      $"第一项是「自动检测」且写回空串 = {autoFirst} (空串才会让 native 按 VID/PID 自动跟随)");
    Console.WriteLine((allPresent && noDup ? "OK   " : "FAIL ") +
                      $"在位端口全列出且去重 (大小写不敏感) = {allPresent && noDup}");
    Console.WriteLine((keepsConfigured ? "OK   " : "FAIL ") +
                      $"配置端口当前不在位时仍保留 = {keepsConfigured}  · 选项: " +
                      string.Join(" | ", portItems.Select(i => i.Label)));
    bool rowsExist = advTitles.Any(t => t.Contains("端口")) && advTitles.Any(t => t.Contains("波特率"));
    bool baudOk = AmbientLight.Views.SettingsPageAdvanced.BaudChoices.Contains(2000000)
                  && AmbientLight.Views.SettingsPageAdvanced.BaudChoices[0] == 2000000;
    Console.WriteLine((rowsExist ? "OK   " : "FAIL ") + $"高级页有「端口」「波特率」两行 = {rowsExist}");
    Console.WriteLine((baudOk ? "OK   " : "FAIL ") +
                      $"波特率候选含默认值 2000000 且排首位 = {baudOk}  · " +
                      string.Join(" / ", AmbientLight.Views.SettingsPageAdvanced.BaudChoices));
    // ⚠️ 真 bug 回归: 光有"候选数组"不够, 下拉必须**真的被填上** —— 曾忘了给 ComboBox
    // 设 ItemsSource, 空列表上设 SelectedIndex 既不报错也不生效, 于是用户看到空下拉。
    // 这里断言"控件里确实有项, 且选中项与配置往返一致" (数据驱动, 不写死数量/文案)。
    advPage.BuildForProbe();
    var baudCombo = advPage.FindName("BaudCombo") as ComboBox;
    var portCombo = advPage.FindName("PortCombo") as ComboBox;
    int baudCount = baudCombo?.Items.Count ?? 0;
    bool baudFilled = baudCount == AmbientLight.Views.SettingsPageAdvanced.BaudChoices.Length
                      && baudCombo!.SelectedIndex == Array.IndexOf(
                             AmbientLight.Views.SettingsPageAdvanced.BaudChoices,
                             AmbientLight.Services.ConfigStore.Device.BaudRate);
    Console.WriteLine((baudFilled ? "OK   " : "FAIL ") +
                      $"波特率下拉已填充 {baudCount} 项 (== 候选数) 且选中项 == 配置值 " +
                      $"{AmbientLight.Services.ConfigStore.Device.BaudRate} = {baudFilled}");
    string portSel = portCombo?.SelectedItem is AmbientLight.Views.SettingsPageAdvanced.PortItem pi
        ? AmbientLight.Views.SettingsPageAdvanced.PortValueOf(pi) : "(无选中)";
    bool portFilled = portCombo is { Items.Count: > 0 }
                      && string.Equals(portSel, AmbientLight.Services.ConfigStore.Device.ComPort,
                                       StringComparison.OrdinalIgnoreCase);
    Console.WriteLine((portFilled ? "OK   " : "FAIL ") +
                      $"端口下拉已填充 {portCombo?.Items.Count ?? 0} 项, 选中项写回 \"{portSel}\" " +
                      $"== 配置的端口 \"{AmbientLight.Services.ConfigStore.Device.ComPort}\" = {portFilled}");
    Console.WriteLine("INFO 本机 COM 口 (注册表 SERIALCOMM): " +
                      string.Join(", ", AmbientLight.Services.SerialPorts.All().DefaultIfEmpty("(无)")));

    Console.WriteLine();
    Console.WriteLine("---- M5.2 主题: 纯函数 (配置解析 / 高对比度兜底) ----");
    // 配置值是**字符串**, 手改过 json / 旧文件缺字段 / 大小写不一致都可能发生 → 解析必须永不抛,
    // 未知一律落到"跟随系统"。
    var themeParseCases = new (string? Raw, AmbientLight.Services.ThemeChoice Expect, string Desc)[]
    {
        ("system", AmbientLight.Services.ThemeChoice.System, "正常值"),
        ("light", AmbientLight.Services.ThemeChoice.Light, "正常值"),
        ("dark", AmbientLight.Services.ThemeChoice.Dark, "正常值"),
        ("LIGHT", AmbientLight.Services.ThemeChoice.Light, "大写"),
        ("  Dark  ", AmbientLight.Services.ThemeChoice.Dark, "前后空白"),
        ("", AmbientLight.Services.ThemeChoice.System, "空串"),
        (null, AmbientLight.Services.ThemeChoice.System, "null"),
        ("weird-value", AmbientLight.Services.ThemeChoice.System, "未知值"),
    };
    foreach (var c in themeParseCases)
    {
        var got = AmbientLight.Services.ThemeService.Parse(c.Raw);
        Console.WriteLine((got == c.Expect ? "OK   " : "FAIL ") +
                          $"主题解析「{c.Raw ?? "null"}」({c.Desc}) → {got} (期望 {c.Expect})");
    }
    bool themeRoundTrip = Enum.GetValues<AmbientLight.Services.ThemeChoice>()
        .All(ch => AmbientLight.Services.ThemeService.Parse(
                       AmbientLight.Services.ThemeService.ToKey(ch)) == ch);
    Console.WriteLine((themeRoundTrip ? "OK   " : "FAIL ") + $"主题 key ↔ 枚举 往返一致 = {themeRoundTrip}");
    // D3: 高对比度回退深色 (HC 的 4 套字典我们没走查过)
    bool hcToDark = AmbientLight.Services.ThemeService.ResolveForApply(
                        AmbientLight.Services.ThemeChoice.System, Wpf.Ui.Appearance.SystemTheme.HC1, true)
                    == Wpf.Ui.Appearance.ApplicationTheme.Dark
                    && AmbientLight.Services.ThemeService.ResolveForApply(
                        AmbientLight.Services.ThemeChoice.System, Wpf.Ui.Appearance.SystemTheme.HCWhite, true)
                    == Wpf.Ui.Appearance.ApplicationTheme.Dark;
    Console.WriteLine((hcToDark ? "OK   " : "FAIL ") + $"高对比度回退深色 (M5.2 D3) = {hcToDark}");
    // 固定浅/深**不受系统影响** —— 这条能抓住"忘了分流、把固定主题也交给系统"这类错
    bool fixedWins = AmbientLight.Services.ThemeService.ResolveForApply(
                         AmbientLight.Services.ThemeChoice.Light, Wpf.Ui.Appearance.SystemTheme.Dark, false)
                     == Wpf.Ui.Appearance.ApplicationTheme.Light
                     && AmbientLight.Services.ThemeService.ResolveForApply(
                         AmbientLight.Services.ThemeChoice.Dark, Wpf.Ui.Appearance.SystemTheme.Light, false)
                     == Wpf.Ui.Appearance.ApplicationTheme.Dark;
    Console.WriteLine((fixedWins ? "OK   " : "FAIL ") + $"固定浅/深主题不受系统设置影响 = {fixedWins}");
    bool sysFollow = AmbientLight.Services.ThemeService.ResolveForApply(
                         AmbientLight.Services.ThemeChoice.System, Wpf.Ui.Appearance.SystemTheme.Dark, false)
                     == Wpf.Ui.Appearance.ApplicationTheme.Dark
                     && AmbientLight.Services.ThemeService.ResolveForApply(
                         AmbientLight.Services.ThemeChoice.System, Wpf.Ui.Appearance.SystemTheme.Light, false)
                     == Wpf.Ui.Appearance.ApplicationTheme.Light;
    Console.WriteLine((sysFollow ? "OK   " : "FAIL ") + $"「跟随系统」按系统深浅解析 = {sysFollow}");

    Console.WriteLine();
    Console.WriteLine("---- M5.2 主题: 机制就位 (静默失败的前置条件 + 切换真的生效) ----");
    // ★ ApplicationThemeManager.Apply 内部是 ResourceDictionaryManager("wpf.ui;")
    //   .UpdateDictionary("theme", …) —— 按"Source 同时含 wpf.ui; 与 theme"找到 App.xaml 里那个
    //   ThemesDictionary **整个替换**; **找不到就 return(false): 不抛异常、什么都不做**。
    //   所以"它还是不是 Application.Resources 的直接成员"是整条链路唯一的前置条件, 只能靠断言。
    var merged = app.Resources.MergedDictionaries;
    bool themeDictPresent = merged.Any(d => d.Source is not null
        && d.Source.ToString().Contains("wpf.ui;", StringComparison.OrdinalIgnoreCase)
        && d.Source.ToString().Contains("theme", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine((themeDictPresent ? "OK   " : "FAIL ") +
                      $"主题字典是 Application.Resources.MergedDictionaries 的直接成员 = {themeDictPresent}");
    Console.WriteLine("INFO 合并字典: " +
                      string.Join(" | ", merged.Select(d => d.Source?.ToString() ?? "(无 Source)")));
    var themeBefore = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme();
    // 切换真实性: ① 公开的 GetAppTheme() 只在字典**真的换掉**时才更新 (能当 isUpdated 的旁证);
    //           ② 再加一条**方向性**语义域断言 —— 浅色主题的卡片底色必然是亮的。
    // 不锁具体色值: 用户随手调一个主题色不该让探针报假警, 但"切了却不生效"必须报。
    bool lightSwapped = SwapTheme(Wpf.Ui.Appearance.ApplicationTheme.Light);
    Console.WriteLine((lightSwapped ? "OK   " : "FAIL ") +
                      "切到浅色: GetAppTheme == Light 且卡片底色是亮色");
    bool darkSwapped = SwapTheme(Wpf.Ui.Appearance.ApplicationTheme.Dark);
    Console.WriteLine((darkSwapped ? "OK   " : "FAIL ") +
                      "切到深色: GetAppTheme == Dark 且卡片底色是暗色");
    var lightCardBg = CardSurfaceUnder(Wpf.Ui.Appearance.ApplicationTheme.Light);
    var darkCardBg = CardSurfaceUnder(Wpf.Ui.Appearance.ApplicationTheme.Dark);
    SwapTheme(themeBefore == Wpf.Ui.Appearance.ApplicationTheme.Unknown
        ? Wpf.Ui.Appearance.ApplicationTheme.Dark : themeBefore);

    Console.WriteLine();
    Console.WriteLine("---- M5.2 主题: 强调色\"中介刷子\" (改强调色后按钮/滑块要立刻跟上) ----");
    // 现象: 改系统强调色 → **自绘控件**(直接引 AccentFillColorDefaultBrush)马上变,
    // 而 WPF-UI 的**按钮/滑块要等下次换主题**才变。原因 (查 4.3.0 源码):
    //   按钮/滑块引的是**住在主题字典里**的中介刷子 ——
    //     AccentButtonBackground.Color = {DynamicResource AccentFillColorDefault}
    //     SliderThumbBackground.Color   = {DynamicResource SystemAccentColorPrimary}
    //   而强调色还原只改写 Application.Resources **顶层**键 → 字典里那些中介刷子不重算。
    // 下面断言**修复后的不变量**: ApplyAccentOnly() 之后中介刷子必须与它们引用的键一致。
    var dictBefore = ThemeDictInstance();
    Color? AccentKey(string k) => Application.Current.TryFindResource(k) is Color c ? c : null;
    Color? BrushColorOf(string k) => (Application.Current.TryFindResource(k) as SolidColorBrush)?.Color;
    Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
        Colors.Magenta, Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme(), false);
    Console.WriteLine("INFO 只改强调色键时, 中介刷子是否落后于键 = " +
                      $"{BrushColorOf("AccentButtonBackground") != AccentKey("AccentFillColorDefault")} (机制使然, 不是 bug)");
    AmbientLight.Services.ThemeService.ApplyAccentOnly();
    bool buttonBrushesAgree = BrushColorOf("AccentButtonBackground") == AccentKey("AccentFillColorDefault")
                              && BrushColorOf("AccentButtonBackgroundPointerOver") == AccentKey("AccentFillColorSecondary")
                              && BrushColorOf("AccentButtonBackgroundPressed") == AccentKey("AccentFillColorTertiary");
    Console.WriteLine((buttonBrushesAgree ? "OK   " : "FAIL ") +
                      $"按钮中介刷子与强调色键一致 = {buttonBrushesAgree} · " +
                      $"AccentButtonBackground={BrushColorOf("AccentButtonBackground")} " +
                      $"vs AccentFillColorDefault={AccentKey("AccentFillColorDefault")}");
    bool thumbBrushAgrees = BrushColorOf("SliderThumbBackground") == AccentKey("SystemAccentColorPrimary");
    Console.WriteLine((thumbBrushAgrees ? "OK   " : "FAIL ") +
                      $"滑块中介刷子与 SystemAccentColorPrimary 一致 = {thumbBrushAgrees} · " +
                      $"{BrushColorOf("SliderThumbBackground")} vs {AccentKey("SystemAccentColorPrimary")}");
    bool reinflated = ThemeDictInstance() != dictBefore;
    Console.WriteLine((reinflated ? "OK   " : "FAIL ") + $"ApplyAccentOnly 重新膨胀了主题字典 = {reinflated}");

    Console.WriteLine();
    Console.WriteLine("---- M5.2 主题: 高级页主题下拉 (控件 ↔ 配置 往返) ----");
    // 教训: 断言了常量数组却没断言控件 → 空下拉漏到用户手里。这里做**往返**断言。
    var themeCombo = advPage.FindName("ThemeCombo") as ComboBox;
    int themeComboCount = themeCombo?.Items.Count ?? 0;
    bool themeComboFilled = themeComboCount == AmbientLight.Views.SettingsPageAdvanced.ThemeChoices.Length;
    Console.WriteLine((themeComboFilled ? "OK   " : "FAIL ") +
                      $"主题下拉已填充 {themeComboCount} 项 (== 候选数) = {themeComboFilled}");
    string themeKeyBefore = AmbientLight.Services.ConfigStore.App.Theme;
    var themeRtCases = new (string Key, AmbientLight.Services.ThemeChoice Expect, string Desc)[]
    {
        ("light", AmbientLight.Services.ThemeChoice.Light, "浅色"),
        ("dark", AmbientLight.Services.ThemeChoice.Dark, "深色"),
        ("weird", AmbientLight.Services.ThemeChoice.System, "未知值 → 跟随系统"),
        ("system", AmbientLight.Services.ThemeChoice.System, "跟随系统"),
    };
    foreach (var c in themeRtCases)
    {
        AmbientLight.Services.ConfigStore.App.Theme = c.Key;
        advPage.BuildForProbe();                       // 无头宿主不触发 Loaded → 手工装配
        int idx = Array.IndexOf(AmbientLight.Views.SettingsPageAdvanced.ThemeChoices,
                                AmbientLight.Services.ThemeService.Parse(c.Key));
        bool ok = themeCombo?.SelectedIndex == idx;
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"配置 theme=\"{c.Key}\" ({c.Desc}) → 下拉选中 {themeCombo?.SelectedIndex} (期望 {idx})");
    }
    AmbientLight.Services.ConfigStore.App.Theme = themeKeyBefore;

    // 系统偏好监听 (SystemEvents 是**静态**事件): 挂/摘必须幂等。
    // 摘不掉 = 那个隐藏窗口吊着进程, 用户点"退出"后进程不消失 (M5.2 用 Attach/Detach 包起来的原因)。
    bool hookOk = true;
    try
    {
        var hookWin = new AmbientLight.Views.MainWindow();
        AmbientLight.Services.ThemeService.Attach(hookWin);
        AmbientLight.Services.ThemeService.Attach(hookWin);   // 重复挂: 不应重复订阅
        AmbientLight.Services.ThemeService.Detach();
        AmbientLight.Services.ThemeService.Detach();          // 没挂就摘: 不应抛
    }
    catch (Exception ex)
    {
        hookOk = false;
        Console.WriteLine($"INFO 挂/摘异常: {ex.GetType().Name}: {ex.Message}");
    }
    Console.WriteLine((hookOk ? "OK   " : "FAIL ") + $"主题服务的系统监听可重复挂/摘 (幂等不抛) = {hookOk}");

    Console.WriteLine();
    Console.WriteLine("---- M5.2 主题: 语义色板对比度 (浅色主题写浅色字就翻在这条) ----");
    // 可计算的不变量: 胶囊前景色 vs **它实际落到的底色** 的 WCAG 对比度必须 ≥ 3.0
    // (3.0 = 非文本/大字号阈值; 10.5px 的药丸字在实测里都在 6 以上)。
    // 底色不是常量: 深色套落在 banner 渐变上 → 取该画刷里**最亮**的一段当最坏情况 (底越亮越糊);
    // 浅色套落在卡片上 → 取浅色主题的 CardBackground 与白底合成 (半透明底色最坏情况就是纯白)。
    const double MinContrast = 3.0;
    var bannerBrush = home.TryFindResource("BannerBaseBrush") as GradientBrush;
    Console.WriteLine("INFO banner 底渐变段数 = " + (bannerBrush?.GradientStops.Count ?? 0));
    Color bannerWorst = bannerBrush?.GradientStops.Select(s => s.Color)
                            .OrderByDescending(AmbientLight.Services.ThemePalette.RelativeLuminance)
                            .FirstOrDefault() ?? Colors.Black;
    var tones = new[]
    {
        AmbientLight.Services.Tone.Green, AmbientLight.Services.Tone.Amber,
        AmbientLight.Services.Tone.Blue, AmbientLight.Services.Tone.Red,
        AmbientLight.Services.Tone.Gray,
    };
    foreach (var tone in tones)
    {
        var (bg, fg) = AmbientLight.Services.ThemePalette.Of(tone, dark: true);
        double cr = AmbientLight.Services.ThemePalette.ContrastRatio(
            fg, AmbientLight.Services.ThemePalette.Blend(bg, bannerWorst));
        Console.WriteLine((cr >= MinContrast ? "OK   " : "FAIL ") +
                          $"深色套 {tone}: 字色 vs banner 最亮段 -> 对比度 {cr:F2} (≥ {MinContrast})");
    }
    foreach (var tone in tones)
    {
        var (bg, fg) = AmbientLight.Services.ThemePalette.Of(tone, dark: false);
        double cr = AmbientLight.Services.ThemePalette.ContrastRatio(
            fg, AmbientLight.Services.ThemePalette.Blend(bg, lightCardBg));
        Console.WriteLine((cr >= MinContrast ? "OK   " : "FAIL ") +
                          $"浅色套 {tone}: 字色 vs 浅色卡片底 -> 对比度 {cr:F2} (≥ {MinContrast})");
    }
    foreach (var (state, desc) in new (int, string)[]
    {
        (AmbientCore.StateRunning, "运行中"), (AmbientCore.StateStarting, "连接中"),
        (AmbientCore.StateWaitDisp, "等待画面"), (AmbientCore.StateDevLost, "灯带不在位"),
        (AmbientCore.StateError, "异常"), (AmbientCore.StateStopped, "已停止"),
    })
    {
        var (dot, text) = AmbientLight.Services.ThemePalette.StateOf(state, dark: true);
        double cd = AmbientLight.Services.ThemePalette.ContrastRatio(dot, bannerWorst);
        double ct = AmbientLight.Services.ThemePalette.ContrastRatio(text, bannerWorst);
        double cw = AmbientLight.Services.ThemePalette.ContrastRatio(dot, darkCardBg);
        Console.WriteLine((cd >= MinContrast && ct >= MinContrast ? "OK   " : "FAIL ") +
                          $"banner 胶囊 {desc}: 圆点/文字 vs 最亮段 -> {cd:F2} / {ct:F2}" +
                          $" · vs 深色卡片底 {cw:F2}");
    }

    Console.WriteLine();
    Console.WriteLine("---- M5.2 主题: 文字色层级的对比度 (浅色下说明文字不能发虚) ----");
    // 动机: 浅色模式下说明/次级文字太淡 —— 量化后是 WPF-UI 的 TextFillColorTertiaryBrush
    // 浅色只有 #72000000 (45% 黑), 落在卡片底上对比度 3.33。
    // 结论: **不自造刷子**, 说明文字改用 WPF-UI 的 TextFillColorSecondaryBrush;
    // 这里断言两条**可计算的不变量**, 都不锁具体色值:
    //   ① 说明文字档 (Secondary) 在两个主题下都 ≥ 4.0;
    //   ② 层级必须单调递减 Primary > Secondary > Tertiary (同一个底上) —— 这条能抓住"两个键被写反"。
    const double MinSecondary = 4.0;
    double ContrastOf(string key, Color card) =>
        Application.Current.TryFindResource(key) is SolidColorBrush b
            ? AmbientLight.Services.ThemePalette.ContrastRatio(
                AmbientLight.Services.ThemePalette.Blend(b.Color, card), card)
            : 0;
    foreach (var (ch, desc) in new (AmbientLight.Services.ThemeChoice, string)[]
    {
        (AmbientLight.Services.ThemeChoice.Dark, "深色"),
        (AmbientLight.Services.ThemeChoice.Light, "浅色"),
    })
    {
        AmbientLight.Services.ThemeService.Apply(ch);   // 走生产路径, 保证资源确实换过
        var card = CardSurface();
        double primary = ContrastOf("TextFillColorPrimaryBrush", card);
        double secondary = ContrastOf("TextFillColorSecondaryBrush", card);
        double tertiary = ContrastOf("TextFillColorTertiaryBrush", card);
        Console.WriteLine((secondary >= MinSecondary ? "OK   " : "FAIL ") +
                          $"{desc}主题: 说明文字 (Secondary) 对比度 {secondary:F2} (≥ {MinSecondary}) · " +
                          $"Primary {primary:F2} / Tertiary {tertiary:F2}");
        bool monotonic = primary > secondary && secondary > tertiary;
        Console.WriteLine((monotonic ? "OK   " : "FAIL ") +
                          $"{desc}主题: 文字层级单调递减 (Primary > Secondary > Tertiary) = {monotonic}");
        Console.WriteLine($"INFO {desc}主题卡片底 = #{card.R:X2}{card.G:X2}{card.B:X2}, " +
                          $"Primary={ContrastOf("TextFillColorPrimaryBrush", card):F2}");
    }
    SwapTheme(themeBefore == Wpf.Ui.Appearance.ApplicationTheme.Unknown
        ? Wpf.Ui.Appearance.ApplicationTheme.Dark : themeBefore);

    Console.WriteLine();
    Console.WriteLine("---- D1-B 实验: 点亮态 banner 浅底 + 深字 (可计算的验收规则) ----");
    // 原方案 A = 点亮态恒深色; 本实验分支 = 点亮态也随主题 (浅色下浅底深字)。
    // 靠肉眼判断不够 → 这里断两条**可计算**的规则 (两个主题都要过):
    //   ① 点亮态文字 vs banner 底色**每一段渐变**的对比度 (取最差) ≥ 4.5 / 说明文字 ≥ 4.0;
    //   ② 光带峰值色 / 辉光色 铺在该底色上时, 亮度差 |ΔL| ≥ 0.02 (即"有可感的明暗差" ——
    //      方案 A 的理由正是"加亮型辉光在浅底上会失效", 这条就是对它的量化检验)。
    //      **阈值 0.02 是量出来的下限, 不是拍的**: 深色主题现状 (M5.1 定稿的含蓄光带) 就是 0.033,
    //      辉光是 0.377 —— 所以 0.02 只用来挡"彻底看不见", 真正的看点是浅色主题也要过。
    bool baseKeysOk = AmbientLight.Views.HomePage.BannerBaseKey(true) == "BannerBaseBrush"
                      && AmbientLight.Views.HomePage.BannerBaseKey(false) == "BannerBaseLightBrush";
    Console.WriteLine((baseKeysOk ? "OK   " : "FAIL ") + $"底色画刷键按主题选择 = {baseKeysOk}");

    foreach (var (ch, desc) in new (AmbientLight.Services.ThemeChoice, string)[]
    {
        (AmbientLight.Services.ThemeChoice.Dark, "深色"),
        (AmbientLight.Services.ThemeChoice.Light, "浅色"),
    })
    {
        AmbientLight.Services.ThemeService.Apply(ch);
        bool dark = ch == AmbientLight.Services.ThemeChoice.Dark;

        // ① 文字: 用被测的**纯函数**取值, 底面用被测的**纯函数**选键 → 接线与取值一起断
        var colors = AmbientLight.Views.HomePage.LitBannerColors(dark,
            Application.Current.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush priB
                ? priB.Color : Colors.Transparent,
            Application.Current.TryFindResource("TextFillColorSecondaryBrush") is SolidColorBrush secB
                ? secB.Color : Colors.Transparent,
            Application.Current.TryFindResource("ControlFillColorSecondaryBrush") is SolidColorBrush ctlB
                ? ctlB.Color : Colors.Transparent);
        // ⚠️ banner 的渐变是 **HomePage.xaml 的页面资源**, 不是 App 资源 → 必须从 home 上找
        // (用 Application.Current.TryFindResource 会一直是 null, 对比度就永远算成哨兵值 99)
        var baseBrush = home.TryFindResource(
            AmbientLight.Views.HomePage.BannerBaseKey(dark)) as LinearGradientBrush;
        double worstTitle = 99, worstSub = 99;
        if (baseBrush is not null)
            foreach (var gs in baseBrush.GradientStops)
            {
                double t = AmbientLight.Services.ThemePalette.ContrastRatio(
                    AmbientLight.Services.ThemePalette.Blend(colors.Title, gs.Color), gs.Color);
                double s = AmbientLight.Services.ThemePalette.ContrastRatio(
                    AmbientLight.Services.ThemePalette.Blend(colors.Sub, gs.Color), gs.Color);
                worstTitle = Math.Min(worstTitle, t);
                worstSub = Math.Min(worstSub, s);
            }
        Console.WriteLine((baseBrush is not null && worstTitle >= 4.5 ? "OK   " : "FAIL ") +
                          $"{desc}主题: 点亮态标题 vs 底色渐变**最差**段 = {worstTitle:F2} (≥ 4.5)");
        Console.WriteLine((worstSub >= 4.0 ? "OK   " : "FAIL ") +
                          $"{desc}主题: 点亮态说明文字 = {worstSub:F2} (≥ 4.0) · " +
                          $"标题 {colors.Title} / 胶囊底 {colors.PillBg}");

        // ② 光带 / 辉光 在底色上"看得见" (ΔL)
        if (baseBrush is not null)
        {
            var baseStops = baseBrush.GradientStops.Select(g => g.Color).ToArray();
            Color PeakOf(string key)
            {
                var b = home.TryFindResource(key) as LinearGradientBrush;
                if (b is null) return Colors.Transparent;
                var p = b.GradientStops.OrderByDescending(g => g.Color.A).First().Color;
                // 与底色合成一次 (alpha 坡的峰值也不是全不透明)
                return AmbientLight.Services.ThemePalette.Blend(p, baseStops[^1]);
            }
            var streak = PeakOf("BannerStreakBlueBrush");
            var glowC = AmbientLight.Services.ThemePalette.Blend(
                Color.FromRgb(0x56, 0xB0, 0xF0), baseStops[^1]);       // ModeGlowColor("Blue")
            double dStreak = Math.Abs(AmbientLight.Services.ThemePalette.RelativeLuminance(streak)
                                      - AmbientLight.Services.ThemePalette.RelativeLuminance(baseStops[^1]));
            double dGlow = Math.Abs(AmbientLight.Services.ThemePalette.RelativeLuminance(glowC)
                                    - AmbientLight.Services.ThemePalette.RelativeLuminance(baseStops[^1]));
            Console.WriteLine((dStreak >= 0.02 ? "OK   " : "FAIL ") +
                              $"{desc}主题: 光带峰值在底色上可见 (|ΔL| {dStreak:F3} ≥ 0.02)");
            Console.WriteLine((dGlow >= 0.02 ? "OK   " : "FAIL ") +
                              $"{desc}主题: 辉光色在底色上可见 (|ΔL| {dGlow:F3} ≥ 0.02)");
        }
    }
    AmbientLight.Services.ThemeService.Apply(
        AmbientLight.Services.ThemeService.Parse(AmbientLight.Services.ConfigStore.App.Theme));

    Console.WriteLine();
    Console.WriteLine("---- LedRingView 外观: 一个 Surface 开关驱动矩形/文字/描边 ----");
    // 2026-10-07 方案 2: 控件同时用在"跟随主题的主页卡片"与"恒深的校准窗面板"上,
    // 所以整支外观由 Surface 一个开关 + 一份调色板 SurfaceColors(dark) 决定。
    foreach (var (ch, dark, desc) in new[]
             {
                 (AmbientLight.Services.ThemeChoice.Dark, true, "深面"),
                 (AmbientLight.Services.ThemeChoice.Light, false, "浅面"),
             })
    {
        // ⚠️ 必须**真的切主题**: "浅面"的文字色取自该主题的资源 (拿深色主题的白字去算浅面 = 白压白)。
        // 教训与 D1-B 那段一样: 判主题/取主题色的东西, 光改变量没用。
        AmbientLight.Services.ThemeService.Apply(ch);
        var tp = Application.Current.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush pri
            ? pri.Color : Colors.Black;
        var ts = Application.Current.TryFindResource("TextFillColorSecondaryBrush") is SolidColorBrush sec
            ? sec.Color : Colors.Gray;
        var sc = AmbientLight.Controls.LedRingView.SurfaceColors(dark, tp, ts);
        _ = desc;
        double titleCr = AmbientLight.Services.ThemePalette.ContrastRatio(
            AmbientLight.Services.ThemePalette.Blend(sc.Title, sc.RectFill), sc.RectFill);
        double hintCr = AmbientLight.Services.ThemePalette.ContrastRatio(
            AmbientLight.Services.ThemePalette.Blend(sc.Hint, sc.RectFill), sc.RectFill);
        Console.WriteLine((titleCr >= 4.5 && hintCr >= 4.5 ? "OK   " : "FAIL ") +
                          $"{(dark ? "深面" : "浅面")}: 文字 vs 屏幕矩形 标题 {titleCr:F2} / 说明 {hintCr:F2} (≥ 4.5)");
        // 阈值 0.02 是**量出来的下限**: 深面边框 #2E2E38 / 底 #101016 是 M5.1 定稿的含蓄描边 (0.023),
        // 浅面则是 0.286 (浅色主题下卡片本来就接近白, 全靠这道边框把"屏幕"认出来)。
        double edge = Math.Abs(AmbientLight.Services.ThemePalette.RelativeLuminance(sc.RectStroke)
                               - AmbientLight.Services.ThemePalette.RelativeLuminance(sc.RectFill));
        Console.WriteLine((edge >= 0.02 ? "OK   " : "FAIL ") +
                          $"{(dark ? "深面" : "浅面")}: 矩形边框在矩形底上可见 (|ΔL| {edge:F3} ≥ 0.02)");
        // 灯珠描边: 与"灯珠所在的那块底"(主页卡片 / 校准窗面板) 比, 而不是与矩形比
        var bg = dark ? Color.FromRgb(0x14, 0x14, 0x18) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        double od = Math.Abs(AmbientLight.Services.ThemePalette.RelativeLuminance(
                                AmbientLight.Services.ThemePalette.Blend(sc.Outline, bg))
                             - AmbientLight.Services.ThemePalette.RelativeLuminance(bg));
        Console.WriteLine((od >= 0.05 ? "OK   " : "FAIL ") +
                          $"{(dark ? "深面" : "浅面")}: 灯珠描边在其底色上可见 (|ΔL| {od:F3} ≥ 0.05)");
    }
    AmbientLight.Services.ThemeService.Apply(
        AmbientLight.Services.ThemeService.Parse(AmbientLight.Services.ConfigStore.App.Theme));
    var themePrimary = Application.Current.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush pri2
        ? pri2.Color : Colors.Black;
    var themeSecondary = Application.Current.TryFindResource("TextFillColorSecondaryBrush") is SolidColorBrush sec2
        ? sec2.Color : Colors.Gray;
    bool outlineOrder = AmbientLight.Services.ThemePalette.RelativeLuminance(
                            AmbientLight.Controls.LedRingView.SurfaceColors(true, themePrimary, themeSecondary).Outline)
                        > AmbientLight.Services.ThemePalette.RelativeLuminance(
                            AmbientLight.Controls.LedRingView.SurfaceColors(false, themePrimary, themeSecondary).Outline);
    Console.WriteLine((outlineOrder ? "OK   " : "FAIL ") + $"描边方向: 深面给亮描边 / 浅面给暗描边 = {outlineOrder}");

    // ★ 接线: 建一圈灯, 断言"一个开关真的驱动三样东西"
    var ringProbe = new AmbientLight.Controls.LedRingView();
    ringProbe.Configure(60, 4, direction: 0);
    var canvas = ringProbe.FindName("RingCanvas") as System.Windows.Controls.Canvas;
    var dots = canvas?.Children.OfType<System.Windows.Shapes.Ellipse>().ToArray() ?? [];
    var rectProbe = canvas?.Children.OfType<System.Windows.Shapes.Rectangle>().FirstOrDefault();
    int noStroke = dots.Count(e => e.Stroke is not System.Windows.Media.SolidColorBrush || e.StrokeThickness <= 0);
    Console.WriteLine((dots.Length == 60 && noStroke == 0 ? "OK   " : "FAIL ") +
                      $"60 颗灯珠全部带描边 = {dots.Length == 60 && noStroke == 0} (共 {dots.Length} 颗)");
    var blockProbe = ringProbe.FindName("HintBlock") as System.Windows.Controls.StackPanel;
    Console.WriteLine((blockProbe?.MaxWidth == 300 ? "OK   " : "FAIL ") +
                      $"中心文字固定 MaxWidth=300 (不随 Viewbox 缩放) = {blockProbe?.MaxWidth}");
    bool chipGone = true;
    for (DependencyObject? up = blockProbe; up is not null
         && up is not AmbientLight.Controls.LedRingView; up = System.Windows.Media.VisualTreeHelper.GetParent(up))
        if (up is System.Windows.Controls.Border b && b.Background is System.Windows.Media.SolidColorBrush bb
            && bb.Color.A != 0)
            chipGone = false;
    Console.WriteLine((chipGone ? "OK   " : "FAIL ") +
                      $"底衬已移除 (文字到控件根之间没有带底色的 Border) = {chipGone}");

    ringProbe.Surface = AmbientLight.Controls.LedRingView.RingSurface.AlwaysDark;
    var dk = AmbientLight.Controls.LedRingView.SurfaceColors(true, themePrimary, themeSecondary);
    bool darkSwitch = (rectProbe?.Fill as System.Windows.Media.SolidColorBrush)?.Color == dk.RectFill
                      && (rectProbe?.Stroke as System.Windows.Media.SolidColorBrush)?.Color == dk.RectStroke
                      && (ringProbe.FindName("HintTitle") as System.Windows.Controls.TextBlock)?.Foreground
                             is System.Windows.Media.SolidColorBrush tf && tf.Color == dk.Title
                      && dots.All(e => (e.Stroke as System.Windows.Media.SolidColorBrush)?.Color == dk.Outline);
    Console.WriteLine((darkSwitch ? "OK   " : "FAIL ") +
                      $"Surface=AlwaysDark 一次驱动矩形/文字/描边 = {darkSwitch}");

    ringProbe.Surface = AmbientLight.Controls.LedRingView.RingSurface.Theme;
    var tm = AmbientLight.Controls.LedRingView.SurfaceColors(
        AmbientLight.Services.ThemeService.IsDark, themePrimary, themeSecondary);
    bool themeSwitch = (rectProbe?.Fill as System.Windows.Media.SolidColorBrush)?.Color == tm.RectFill
                       && (ringProbe.FindName("HintTitle") as System.Windows.Controls.TextBlock)?.Foreground
                              is System.Windows.Media.SolidColorBrush tf2 && tf2.Color == tm.Title;
    Console.WriteLine((themeSwitch ? "OK   " : "FAIL ") +
                      $"Surface=Theme 时跟随当前主题 (IsDark={AmbientLight.Services.ThemeService.IsDark}) = {themeSwitch}");


    Console.WriteLine();
    Console.WriteLine("---- v1.6: MCU 模式下禁用亮度 + 追光两开关的配置往返 ----");
    // ① 亮度可用性 (纯函数): MCU 特效由固件渲染, 亮度无落点 → UI 必须禁用滑块
    bool brightTable = AmbientLight.Views.HomePage.BrightnessEnabled(0)      // 追光 ✓
                       && AmbientLight.Views.HomePage.BrightnessEnabled(1)   // 拾音 ✓ (它是最高亮度上限)
                       && AmbientLight.Views.HomePage.BrightnessEnabled(11)  // 纯色 ✓
                       && !new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13 }     // MCU 全系 ✗
                              .Any(m => AmbientLight.Views.HomePage.BrightnessEnabled(m));
    Console.WriteLine((brightTable ? "OK   " : "FAIL ") +
                      $"亮度可用性真值表 (追光/拾音/纯色 可用 · MCU 全系禁用) = {brightTable}");

    // ② 柔化平滑**档位** (0=关 / 1=抑制闪光 / 2=抑制闪光+时域柔和) 的配置往返
    bool roundTrip = true;
    foreach (int m in new[] { 0, 1, 2 })
    {
        var d2 = new AmbientLight.Services.DeviceSettings { ChaseSmoothMode = m };
        var c2 = d2.ToCoreConfig();
        var b2 = new AmbientLight.Services.DeviceSettings();
        b2.FromCoreConfig(c2);
        roundTrip &= c2.chaseSmoothMode == m && b2.ChaseSmoothMode == m;
    }
    Console.WriteLine((roundTrip ? "OK   " : "FAIL ") + $"柔化平滑档位往返 0/1/2 = {roundTrip}");
    // 默认必须是"关" (用户口径: 默认保持原来的高刷跟屏感)
    bool defOff = new AmbientLight.Services.DeviceSettings().ChaseSmoothMode == 0
                  && AmbientLight.Interop.ACConfig.CreateDefault().chaseSmoothMode == 0;
    Console.WriteLine((defOff ? "OK   " : "FAIL ") + $"档位默认关 = {defOff}");
    // ③ 兼容迁移 (纯函数): v1.6 早期"两个正交开关"→ 等效档位 —— 四个组合打真值表
    var mig = new (int Guard, int Smooth, int Want)[]
    {
        (1, 0, 1), (0, 1, 2), (1, 1, 2), (0, 0, 0),
    };
    bool migOk = mig.All(x =>
    {
        var d3 = new AmbientLight.Services.DeviceSettings { ChaseFlashGuard = x.Guard, ChaseTemporalSmooth = x.Smooth };
        AmbientLight.Services.DeviceSettings.MigrateLegacySmooth(d3);
        return d3.ChaseSmoothMode == x.Want;
    });
    Console.WriteLine((migOk ? "OK   " : "FAIL ") + $"'两个开关'→档位 迁移真值表 (4 组) = {migOk}");

    Console.WriteLine();
    Console.WriteLine("---- 滑块皮肤 (本地魔改: 官方挂点 + 半拇指左移; 三种填充模式) ----");
    // 第四轮定稿: 填充只有一种画法 = WPF 官方的 `PART_SelectionRange`; "什么时候不画"必须**声明式**。
    // 断言按这个契约分五组: 模板长什么样 / Track / Anchor / None / 禁用, 外加"重新模板化后不掉状态"。
    var slHost = new System.Windows.Controls.Grid { Width = 200, Height = 40 };
    void HostSlider(Slider s)
    {
        slHost.Children.Clear();
        slHost.Children.Add(s);
        slHost.Measure(new Size(200, 40));
        slHost.Arrange(new Rect(0, 0, 200, 40));
        s.ApplyTemplate();
        slHost.UpdateLayout();
    }
    static FrameworkElement? RangeHostOf(Slider sl)
        => sl.Template?.FindName("SelectionRangeHost", sl) as FrameworkElement;
    // ⚠️ 挂点现在是 `Border` (需要"只给自由端圆角", Rectangle 只能四角同半径) ——
    //    WPF 对挂点只要求 `FrameworkElement` (源码: TemplatePart 声明与 UpdateSelectionRange…)
    static FrameworkElement? PartOf(Slider sl)
        => sl.Template?.FindName("PART_SelectionRange", sl) as FrameworkElement;
    static System.Windows.Controls.Primitives.Track? TrackOf(Slider sl)
        => sl.Template?.FindName("PART_Track", sl) as System.Windows.Controls.Primitives.Track;
    static System.Windows.Controls.Primitives.Thumb? ThumbOf(Slider sl)
        => sl.Template?.FindName("Thumb", sl) as System.Windows.Controls.Primitives.Thumb;

    // ① 我们的模板赢了上游 + **旧的自绘填充条必须已经删掉**
    //    (前三轮在"减少"重复按钮的模板里画过一条, 它是"运行时改元素"那套时机的源头 ✗)
    var slProbe = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 200 };
    HostSlider(slProbe);
    var slPart = PartOf(slProbe);
    var slRangeHost = RangeHostOf(slProbe);
    static bool OldFillBarGone(Slider sl)
    {
        var btn = TrackOf(sl)?.DecreaseRepeatButton;
        return btn?.Template is not null && btn.Template.FindName("TrackFillBar", btn) is null;
    }
    bool tplOk = slPart is { Height: 4 } && slRangeHost is not null && OldFillBarGone(slProbe);
    Console.WriteLine((tplOk ? "OK   " : "FAIL ") +
                      "本地模板生效 (官方挂点 + 装它的 Canvas 都在, 旧的自绘填充条已删) = " + tplOk);

    // ② Track 模式: 官方挂点开、两端 = Minimum/Value、几何**真的算出来**(宽度 > 20px: 静默变 0 只能靠它抓),
    //    且那层 Canvas 被**左移半个拇指** → 填充左端贴轨道左沿 (用户验收口径: 进度条不能"缺一截")
    double trackW = TrackOf(slProbe)?.ActualWidth ?? 0, thumbW = ThumbOf(slProbe)?.ActualWidth ?? 0;
    double fillLeftOnScreen = slPart is null ? double.NaN
        : Canvas.GetLeft(slPart) + (slRangeHost?.Margin.Left ?? 0);
    bool trackMode = slProbe.IsSelectionRangeEnabled && slProbe.SelectionStart == 0
                     && Math.Abs(slProbe.SelectionEnd - 50) < 0.001
                     && slPart is { ActualWidth: > 20 } && thumbW > 0 && trackW > 0
                     && Math.Abs((slRangeHost?.Margin.Left ?? 0) + thumbW / 2) < 0.01
                     && Math.Abs(fillLeftOnScreen) < 1.0;
    Console.WriteLine((trackMode ? "OK   " : "FAIL ") +
                      $"Track 模式: 挂点开={slProbe.IsSelectionRangeEnabled} 端点={slProbe.SelectionStart:F0}→" +
                      $"{slProbe.SelectionEnd:F0} 填充宽={slPart?.ActualWidth:F0}px(轨道{trackW:F0}/拇指{thumbW:F0}) " +
                      $"左移={slRangeHost?.Margin.Left:F1} 屏上左端={fillLeftOnScreen:F1}(应≈0) = {trackMode}");

    // ③ Anchor 模式: 同一个挂点, 端点由 Converter 折算 (min/max → "往中性值左边拖"也能画)
    var slAnchor = new Slider { Minimum = 0, Maximum = 2, Value = 0.5, Width = 200 };
    AmbientLight.Controls.SliderSkin.SetFill(slAnchor, AmbientLight.Controls.SliderFill.Anchor);
    AmbientLight.Controls.SliderSkin.SetAnchor(slAnchor, 1.0);
    HostSlider(slAnchor);
    var aPart = PartOf(slAnchor);
    double aThumbW = ThumbOf(slAnchor)?.ActualWidth ?? 0;
    double aTrackW = TrackOf(slAnchor)?.ActualWidth ?? 0;
    double aWant = 0.5 / 2.0 * (aTrackW - aThumbW);          // 值 0.5、中性 1 → 占全行程的 1/4
    bool anchorMode = slAnchor.IsSelectionRangeEnabled && RangeHostOf(slAnchor)?.Visibility == Visibility.Visible
                      && slAnchor.SelectionStart == 0.5 && slAnchor.SelectionEnd == 1.0
                      && aPart is not null && Math.Abs(aPart.ActualWidth - aWant) < 2.0;
    Console.WriteLine((anchorMode ? "OK   " : "FAIL ") +
                      $"Anchor 模式: 挂点开={slAnchor.IsSelectionRangeEnabled} 端点={slAnchor.SelectionStart:F1}→" +
                      $"{slAnchor.SelectionEnd:F1} 填充宽={aPart?.ActualWidth:F0}px(期望≈{aWant:F0}) = {anchorMode}");

    // ④ Fill=None (色带轨道): 官方挂点必须关 **而且那层 Canvas 必须真的收起** ——
    //    ⚠️ 这是第四轮抓到"色带仍被盖"的断言: 光把 IsSelectionRangeEnabled 置假**不够**,
    //       WPF 不会因此藏起 PART_SelectionRange (它带着上次算的几何一直可见) ✗
    var slNone = new Slider { Minimum = 0, Maximum = 360, Value = 24, Width = 200 };
    AmbientLight.Controls.SliderSkin.SetFill(slNone, AmbientLight.Controls.SliderFill.None);
    HostSlider(slNone);
    bool noneMode = !slNone.IsSelectionRangeEnabled && RangeHostOf(slNone) is { Visibility: Visibility.Collapsed };
    Console.WriteLine((noneMode ? "OK   " : "FAIL ") +
                      $"None 模式 (色带轨道): 挂点关={!slNone.IsSelectionRangeEnabled} · " +
                      $"填充层收起={RangeHostOf(slNone)?.Visibility} = {noneMode}");

    // ⑤ 禁用态: 轨道/拇指换成禁用色, **进度填充要在** (只是变成灰的) —— 但填充色必须与**轨道底**
    //    是不同的键! 早先两者都用 `ControlFillColorDisabledBrush` ⇒ 同色叠加, 只有填充那一段亮一档,
    //    左端留一条暗条 = "缺一截" ✗ (这条断言就是钉住那个坑的)
    var slDisabled = new Slider { Minimum = 0, Maximum = 100, Value = 50, Width = 200, IsEnabled = false };
    HostSlider(slDisabled);
    var dPart = PartOf(slDisabled);
    var dTrack = slDisabled.Template?.FindName("TrackBackground", slDisabled) as System.Windows.Controls.Border;
    var disabledFillKey = Application.Current.TryFindResource("TextFillColorDisabledBrush") as SolidColorBrush;
    Color dFill = ((dPart as Border)?.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
    Color dTrackColor = (dTrack?.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
    bool disabledMode = RangeHostOf(slDisabled) is { Visibility: Visibility.Visible }
                        && dPart is { ActualWidth: > 20 } && disabledFillKey is not null
                        && dFill == disabledFillKey.Color
                        && dFill != dTrackColor;
    Console.WriteLine((disabledMode ? "OK   " : "FAIL ") +
                      $"禁用态: 填充层={RangeHostOf(slDisabled)?.Visibility} 填充={dFill} (期望=拇指禁用色 " +
                      $"{disabledFillKey?.Color}) · 轨道底={dTrackColor} (两者必须不同, 否则又会「缺一截」) = {disabledMode}");

    // ⑥ **重新模板化后不能掉状态**: 换宿主 (等价于主题切换重建模板) 后, None 的收起必须还在 ——
    //    前三轮那套"代码钩子设 Visibility"就是在这里丢掉的 (用户看到的"仍被盖" ✗)
    slHost.Children.Clear();
    var slHost2 = new System.Windows.Controls.Grid { Width = 200, Height = 40 };
    slHost2.Children.Add(slNone);
    slHost2.Measure(new Size(200, 40));
    slHost2.Arrange(new Rect(0, 0, 200, 40));
    slHost2.UpdateLayout();
    bool survives = RangeHostOf(slNone) is { Visibility: Visibility.Collapsed };
    Console.WriteLine((survives ? "OK   " : "FAIL ") +
                      $"None 模式的收起在「换宿主重排」后仍然成立 (声明式, 不靠时机) = {survives}");

    // ⑦ Converter 真值表 (纯函数): Start 取 min、End 取 max —— "往左拖"才不会算出负宽度(什么都不画)
    var conv = new AmbientLight.Controls.SliderAnchorConverter();
    var cases = new (double Value, double Anchor, double Min, double WantStart, double WantEnd)[]
    {
        (0.5, 1.0, 0.0, 0.5, 1.0),      // 往左 (衰减) → 交换
        (1.5, 1.0, 0.0, 1.0, 1.5),      // 往右 (增益)
        (1.0, 1.0, 0.0, 1.0, 1.0),      // 正好在中性 (宽度 0)
        (30.0, double.NaN, 0.0, 0.0, 30.0),   // 没给中性值 → 退回 Minimum
    };
    string Anchor2(object prm, double v, double a, double m)
    {
        object r = conv.Convert(new object[] { v, a, m }, typeof(double), prm,
                                System.Globalization.CultureInfo.InvariantCulture);
        return r is double d ? d.ToString("F2") : "?";
    }
    bool convOk = cases.All(c =>
        Anchor2("start", c.Value, c.Anchor, c.Min) == c.WantStart.ToString("F2") &&
        Anchor2("end", c.Value, c.Anchor, c.Min) == c.WantEnd.ToString("F2"));
    Console.WriteLine((convOk ? "OK   " : "FAIL ") + $"填充端点折算真值表 (min/max, 4 组) = {convOk}");

    // ⑦′ 填充两端形状: **挨着拇指的那一端方角、自由端圆角**。
    //     为什么: 填充右端天生停在拇指左沿 (拇指是画在它上面的圆), 两端都圆时那个半圆端在禁用态
    //     (填充比拇指暗, 不再糊成一片) 会露出来 → 看着像进度条右端缺了个口 ✗ (用户第四轮验收反馈)。
    //     判据 = 拇指在 Value 处 → Value ≥ anchor 时"左圆右方", 反之"左方右圆"。
    var cornerConv = new AmbientLight.Controls.SliderFillCornerConverter();
    var cornerCases = new (double Value, double Anchor, double Min, bool LeftRound)[]
    {
        (1.5, 1.0, 0.0, true),      // 增益: 右端在拇指 → 左圆右方
        (0.5, 1.0, 0.0, false),     // 衰减: 左端在拇指 → 左方右圆
        (50.0, double.NaN, 0.0, true),   // Track (无中性值) → 右端在拇指 → 左圆右方
    };
    CornerRadius Want(bool leftRound) => leftRound ? new CornerRadius(2, 0, 0, 2) : new CornerRadius(0, 2, 2, 0);
    bool cornerConvOk = cornerCases.All(c =>
    {
        object r = cornerConv.Convert(new object[] { c.Value, c.Anchor, c.Min }, typeof(CornerRadius),
                                      null!, System.Globalization.CultureInfo.InvariantCulture);
        return r is CornerRadius cr && cr == Want(c.LeftRound);
    });
    Console.WriteLine((cornerConvOk ? "OK   " : "FAIL ") +
                      $"填充圆角真值表 (挨拇指那端方角, 3 组) = {cornerConvOk}");
    // ⚠️ 真值表挡不住"绑定没接上" → 再看三个**已布局**滑块上挂点的**实际** CornerRadius
    //    (Track 实测右端在拇指; slAnchor 是值 0.5/中性 1 = 衰减; 另造一个 1.5 的增益)
    var slAnchorUp = new Slider { Minimum = 0, Maximum = 2, Value = 1.5, Width = 200 };
    AmbientLight.Controls.SliderSkin.SetFill(slAnchorUp, AmbientLight.Controls.SliderFill.Anchor);
    AmbientLight.Controls.SliderSkin.SetAnchor(slAnchorUp, 1.0);
    HostSlider(slAnchorUp);
    CornerRadius Cob(Slider sl) => (PartOf(sl) as Border)?.CornerRadius ?? default;
    bool cornerWired = Cob(slProbe) == Want(true) && Cob(slAnchor) == Want(false) && Cob(slAnchorUp) == Want(true);
    Console.WriteLine((cornerWired ? "OK   " : "FAIL ") +
                      $"圆角真的接到挂点上: Track={Cob(slProbe)} · Anchor衰减={Cob(slAnchor)} · " +
                      $"Anchor增益={Cob(slAnchorUp)} (期望 2,0,0,2 / 0,2,2,0 / 2,0,0,2) = {cornerWired}");

    // ⑧ 页面声明: 色带轨道的滑块必须 None (否则填充盖掉渐变换); 乘数型滑块必须 Anchor+中性值
    var homeProbe = home;
    var colorPageProbe = new AmbientLight.Views.SettingsPageColor();
    var noneNames = new[] { "HueSlider", "SatSlider" };                       // 纯色拾色器的色带轨道
    var noneBad = noneNames.Where(n => (homeProbe.FindName(n) as Slider) is not { } s4
        || AmbientLight.Controls.SliderSkin.GetFill(s4) != AmbientLight.Controls.SliderFill.None).ToArray();
    var multiNames = new[] { "HueSlider", "RGainSlider", "GGainSlider", "BGainSlider" };  // 乘数型
    var multiBad = multiNames.Where(n => (colorPageProbe.FindName(n) as Slider) is not { } s5
        || AmbientLight.Controls.SliderSkin.GetFill(s5) != AmbientLight.Controls.SliderFill.Anchor
        || Math.Abs(AmbientLight.Controls.SliderSkin.GetAnchor(s5) - 1.0) > 0.001).ToArray();
    Console.WriteLine((noneBad.Length == 0 ? "OK   " : "FAIL ") +
                      $"色带轨道滑块不画填充 (Fill=None)" + (noneBad.Length > 0 ? $" · 缺: {string.Join(",", noneBad)}" : ""));
    Console.WriteLine((multiBad.Length == 0 ? "OK   " : "FAIL ") +
                      $"乘数型滑块 Fill=Anchor + 中性值 1" + (multiBad.Length > 0 ? $" · 缺: {string.Join(",", multiBad)}" : ""));
    Console.WriteLine($"INFO 填充几何: Track {slPart?.ActualWidth:F0}x{slPart?.ActualHeight:F0} " +
                      $"@Left={Canvas.GetLeft(slPart):F1} (值 50% → 约占轨道一半) · " +
                      $"Anchor {aPart?.ActualWidth:F0}x{aPart?.ActualHeight:F0} @Left={Canvas.GetLeft(aPart):F1}");

    Console.WriteLine();
    Console.WriteLine("---- D1-B: 光带 / 辉光的浅色版 (主题化资源解析) ----");
    // 用户的调参入口有两处: XAML 的 BannerStreak<色系>Brush(Light) 与代码里的 ModeGlowColor(mode, dark)。
    // 这里断三件事: ① 键的约定 (浅色版 = 深色版 + "Light"); ② **资源真能解析**(解析失败是静默的);
    // ③ 辉光基色与同色系光带**同色相** ("辉光与光带同一套色系"规则)。
    var colorModes = new (int Mode, string Key)[] { (0, "Blue"), (1, "Green"), (11, "Purple"), (2, "Mono") };
    bool namingOk = true;
    foreach (var (m, k) in colorModes)
        namingOk &= AmbientLight.Views.HomePage.StreakBrushKey(m, true) == $"BannerStreak{k}Brush"
                    && AmbientLight.Views.HomePage.StreakBrushKey(m, false) == $"BannerStreak{k}BrushLight";
    Console.WriteLine((namingOk ? "OK   " : "FAIL ") + $"光带键命名约定 (浅色版 = 深色版 + Light) = {namingOk}");

    foreach (var ch in new[] { AmbientLight.Services.ThemeChoice.Dark,
                               AmbientLight.Services.ThemeChoice.Light })
    {
        // ⚠️ 必须**真的切主题**: CrossFadeStreak / UpdateGlow 判主题用的是 ThemeService.IsDark
        // ("已生效的资源字典"), 光把 `dark` 变量改掉是没用的 —— 第一版断言就是这么假 FAIL 的。
        AmbientLight.Services.ThemeService.Apply(ch);
        bool dark = AmbientLight.Services.ThemeService.IsDark;
        var missing = colorModes
            .Where(x => home.TryFindResource(AmbientLight.Views.HomePage.StreakBrushKey(x.Mode, dark))
                        is not LinearGradientBrush)
            .Select(x => x.Key).ToArray();
        Console.WriteLine((missing.Length == 0 ? "OK   " : "FAIL ") +
                          $"{(dark ? "深色" : "浅色")}主题: 4 条光带画刷都能解析 = {missing.Length == 0}" +
                          (missing.Length > 0 ? $" · 缺: {string.Join(",", missing)}" : ""));
        // 同色系: 辉光基色 vs 该色系光带的主色, 色相差 ≤ 20° (近乎无彩色的 (饱和 < 0.05) 跳过色相检查 ——
        // #E4EBF2 这种近白灰的 hue 数值不稳定, 强行比会误报)
        var bad = new List<string>();
        foreach (var (m, k) in colorModes)
        {
            var glowC = AmbientLight.Views.HomePage.ModeGlowColor(m, dark);
            var brush = home.TryFindResource(AmbientLight.Views.HomePage.StreakBrushKey(m, dark))
                        as LinearGradientBrush;
            var peak = brush?.GradientStops.OrderByDescending(g => g.Color.A).First().Color ?? Colors.Transparent;
            double hs = AmbientLight.Services.ThemePalette.SaturationOf(peak);
            double hg = AmbientLight.Services.ThemePalette.SaturationOf(glowC);
            if (hs < 0.05 || hg < 0.05) continue;      // 无色系不配色相
            double dh = Math.Abs(AmbientLight.Services.ThemePalette.HueOf(peak)
                                 - AmbientLight.Services.ThemePalette.HueOf(glowC));
            if (dh > 180) dh = 360 - dh;
            if (dh > 20) bad.Add($"{k}(Δ{dh:F0}°)");
        }
        Console.WriteLine((bad.Count == 0 ? "OK   " : "FAIL ") +
                          $"{(dark ? "深色" : "浅色")}主题: 辉光与光带同色系 = {bad.Count == 0}" +
                          (bad.Count > 0 ? $" · 偏: {string.Join(",", bad)}" : ""));

        // ★ 接线: 换主题后必须**真的**换上本主题那一条 (资源建了没人用 = 白建)。
        // ⚠️ 不能只盯 StreakA: CrossFadeStreak 在 A/B 之间交替 (一条淡出、一条淡入),
        // 所以判据 = "A 或 B 里有一条正是本主题的刷子", 另一条允许还留着上一主题的 (那正是交叉淡化)。
        var streakA = home.FindName("StreakA") as System.Windows.Shapes.Rectangle;
        var streakB = home.FindName("StreakB") as System.Windows.Shapes.Rectangle;
        home.CrossFadeStreak(0, animate: false);
        var want = home.TryFindResource(AmbientLight.Views.HomePage.StreakBrushKey(0, dark));
        bool wired = ReferenceEquals(streakA?.Fill, want) || ReferenceEquals(streakB?.Fill, want);
        Console.WriteLine((wired ? "OK   " : "FAIL ") +
                          $"{(dark ? "深色" : "浅色")}主题: 光带真的换成本主题那一条 (A/B 任一) = {wired}");
        string PeakOfBrush(object? br) => br is LinearGradientBrush g
            ? g.GradientStops.OrderByDescending(x => x.Color.A).First().Color.ToString() : "null";
        Console.WriteLine($"INFO {(dark ? "深色" : "浅色")}: IsDark={AmbientLight.Services.ThemeService.IsDark} " +
                          $"期望={PeakOfBrush(want)} A={PeakOfBrush(streakA?.Fill)} B={PeakOfBrush(streakB?.Fill)} " +
                          $"(A.Opacity={streakA?.Opacity:F0} B.Opacity={streakB?.Opacity:F0})");
    }

    Console.WriteLine();
    Console.WriteLine("---- 设备行端口文案 (显示\"实际会用的口\") ----");
    // 场景: 高级页给某台手动指定了 COM4 后, 设备行仍显示枚举到的 COM5, 点开启却去连 COM4。
    // 规则: **手动指定优先** (它是"必须用它") > 会话端口 > 枚举端口;
    //       `Warn`(警示色) 只在"配置端口 ≠ 本行枚举端口"时才给 —— 一致时只是普通文字。
    var portCases = new (string? Sess, string? Cfg, string? Enum, string Text, bool Manual, bool Warn, string Desc)[]
    {
        ("COM5", "COM4", "COM5", "COM4 (手动指定)", true, true, "手动指定 + 有会话 → 仍以配置为准 (会话口只是此刻在用)"),
        ("", "COM4", "COM5", "COM4 (手动指定)", true, true, "手动指定 + 无会话"),
        ("", "COM5", "COM5", "COM5 (手动指定)", true, false, "指定口与枚举一致 → **不**警示 (两根灯带各钉各的口)"),
        ("", "com5", "COM5", "com5 (手动指定)", true, false, "大小写不敏感 → 视为同一个口"),
        ("COM5", "", "COM5", "COM5", false, false, "没指定 → 会话端口"),
        ("", "", "COM5", "COM5", false, false, "没指定 + 无会话 → 枚举端口"),
        (null, "   ", "COM6", "COM6", false, false, "全空白配置视为未指定"),
    };
    foreach (var c in portCases)
    {
        var (text, manual, warn) = AmbientLight.Views.HomePage.PortLabel(c.Sess, c.Cfg, c.Enum);
        bool ok = text == c.Text && manual == c.Manual && warn == c.Warn;
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"{c.Desc} → \"{text}\" (标记 {manual}/{warn}), 期望 \"{c.Text}\" ({c.Manual}/{c.Warn})");
    }

    // ★ **接线断言** (教训: 断言了常量没断言控件, 于是空下拉漏到用户手里):
    //   光验纯函数不够 —— 真正的 bug 是调用点把"配置里的端口"多套了一个 managedHere 条件,
    //   导致**无会话时永远显示枚举端口**。这里真的建一行, 看第二行文字是什么。
    // 另两条: ① 端口不在位时胶囊要是「端口不在位」; ② 警示色真的落到第二行上。
    // 行元素现在是 ContentPresenter (模板渲染) → 逻辑树里看不到模板内容, 改用**宿主 + 视觉树**读文本
    // (模板应用发生在 Measure; 控件不接窗口也能对它自身 Measure/Arrange)。
    List<TextBlock> RowTextBlocks(UIElement el)
    {
        var host = new ContentControl { Content = el, Width = 700 };
        host.Measure(new Size(700, 160));
        host.Arrange(new Rect(0, 0, 700, 160));
        var list = new List<TextBlock>();
        void RWalk(DependencyObject d)
        {
            if (d is TextBlock t) list.Add(t);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                RWalk(VisualTreeHelper.GetChild(d, i));
        }
        RWalk(host);
        return list;
    }
    const string probeKey = "9_3";                       // 用个不会被选中的 key, 免得影响别的断言
    var cautionBrush = Application.Current.TryFindResource("SystemFillColorCautionBrush") as SolidColorBrush;
    try
    {
        // 注: `ACDeviceInfo.comPort` 是 fixed byte 缓冲, 托管侧没法直接赋字符串 → 这里留空。
        // 不影响断言意图: 若调用点漏传配置端口, 第二行会是**空的** (没有枚举端口可回落),
        // 而不是 "COM4 (手动指定)" —— 正是用户看到的那两种情况。
        AmbientLight.Services.ConfigStore.Devices[probeKey] =
            new AmbientLight.Services.DeviceSettings { ComPort = "COM4" };   // 本机**存在**的口
        var devRow = home.BuildDeviceRow(new ACDeviceInfo
        {
            devType = 9, devId = 3, inUse = 0,
        }, sess: null, sessSlot: -1);
        var texts = RowTextBlocks(devRow).Select(t => t.Text).ToArray();
        bool wired = texts.Any(t => t.Contains("COM4 (手动指定)"));
        Console.WriteLine((wired ? "OK   " : "FAIL ") +
                          $"设备行(无会话)第二行真的显示了配置端口 = {wired} · 行内文字: " +
                          string.Join(" | ", texts.Where(t => t.Length > 0)));
        bool readyPill = texts.Contains("就绪");       // 口在位 → 设备可用, 胶囊仍是「就绪」
        Console.WriteLine((readyPill ? "OK   " : "FAIL ") + $"在位的指定口 → 胶囊仍是「就绪」= {readyPill}");

        // ② 端口**不在位** → 胶囊「端口不在位」+ 第二行转警示色
        AmbientLight.Services.ConfigStore.Devices[probeKey] =
            new AmbientLight.Services.DeviceSettings { ComPort = "COM99" };  // 本机不存在的口
        var missRow = home.BuildDeviceRow(new ACDeviceInfo { devType = 9, devId = 3, inUse = 0 },
                                          sess: null, sessSlot: -1);
        var missTexts = RowTextBlocks(missRow);
        bool missPill = missTexts.Any(t => t.Text == "端口不在位");
        Console.WriteLine((missPill ? "OK   " : "FAIL ") +
                          $"指定了口但本机没有 → 胶囊「端口不在位」= {missPill} · 行内文字: " +
                          string.Join(" | ", missTexts.Select(t => t.Text).Where(s => s.Length > 0)));
        var lineColor = missTexts.FirstOrDefault(t => t.Text.Contains("COM99 (手动指定)"))
                                 ?.Foreground as SolidColorBrush;
        bool caution = cautionBrush is not null && lineColor is not null
                       && lineColor.Color == cautionBrush.Color;
        Console.WriteLine((caution ? "OK   " : "FAIL ") +
                          $"端口不在位时第二行用警示色 = {caution} · 实测 {lineColor?.Color} " +
                          $"(期望 {cautionBrush?.Color})");

        // ③ 完整场景: 手动指定"在位但不是灯带"的口 → 开启 → 会话连不上 (DEV_LOST,
        //    它的 devType/devId/comPort 都还没回填) → 行必须仍然**认出这个会话**并显示「指定端口无应答」,
        //    而且类型/编号要退回用枚举的值 (不能因为会话是 -1 就显示"未知 · 读取中")。
        AmbientLight.Services.ConfigStore.Devices[probeKey] =
            new AmbientLight.Services.DeviceSettings { ComPort = "COM4" };
        var lostSess = new ACStatus { devType = -1, devId = -1, state = AmbientCore.StateDevLost };
        var lostRow = home.BuildDeviceRow(new ACDeviceInfo { devType = 9, devId = 3, inUse = 0 },
                                          lostSess, sessSlot: 0);
        var lostTexts = RowTextBlocks(lostRow).Select(t => t.Text).Where(s => s.Length > 0).ToArray();
        bool lostPill = lostTexts.Contains("指定端口无应答");
        Console.WriteLine((lostPill ? "OK   " : "FAIL ") +
                          $"连不上的会话仍被行认出来 → 胶囊「指定端口无应答」= {lostPill} · " +
                          $"行内文字: {string.Join(" | ", lostTexts)}");
        bool fallbackType = lostTexts.Any(t => t.Contains("id 3")) && !lostTexts.Any(t => t.Contains("读取中"));
        Console.WriteLine((fallbackType ? "OK   " : "FAIL ") +
                          $"会话类型未回填时退回用枚举的类型/编号 (不显示\"读取中\") = {fallbackType}");
    }
    finally
    {
        AmbientLight.Services.ConfigStore.Devices.Remove(probeKey);
    }

    // 供电口径 (banner 端口行与设备行共用 PowerText, 2026-10-10 统一):
    // 只有 Running + bit0=1 才显示「供电 正常」; 其余一律不显示 (0 分不清真异常/还没读到)。
    // 同时兜"缺键" —— 显示出来的必须是字典值, 不能是 key 本身。
    var powerCases = new (int State, long Byte, bool Expect)[]
    {
        (AmbientCore.StateRunning, 1, true),
        (AmbientCore.StateRunning, 0, false),
        (AmbientCore.StateStarting, 1, false),
        (AmbientCore.StateWaitDisp, 1, false),
        (AmbientCore.StateDevLost, 1, false),
        (AmbientCore.StateStopped, 1, false),
    };
    int powerOk = powerCases.Count(c =>
    {
        string got = AmbientLight.Views.HomePage.PowerText(c.State, c.Byte);
        return c.Expect ? got.Length > 0 && got != "Dev.Power.Normal" : got.Length == 0;
    });
    Console.WriteLine((powerOk == powerCases.Length ? "OK   " : "FAIL ") +
                      $"供电口径 (仅 Running+bit0=1 显示「供电 正常」) = {powerOk}/{powerCases.Length}");

    Console.WriteLine();
    Console.WriteLine("---- 总开关按钮的放开条件 (纯函数真值表) ----");
    // 口径: 关闭后**收尾没完**不许点开启 (native StopEx 的 join 可能还在跑 1~5s);
    // 判据 = 不在处理本次点击 && 没有启动任务在飞 && 队列里没有未完成的生命周期调用。
    var toggleCases = new (bool Toggling, bool Starting, bool Busy, bool Expect, string Desc)[]
    {
        (false, false, false, true, "空闲"),
        (true, false, false, false, "正在处理这次点击"),
        (false, true, false, false, "启动任务在飞"),
        (false, false, true, false, "关闭收尾中 (端口还没释放)"),
        (false, true, true, false, "启动在飞 + 收尾中"),
    };
    foreach (var c in toggleCases)
    {
        bool got = AmbientLight.Views.HomePage.ToggleEnabled(c.Toggling, c.Starting, c.Busy);
        Console.WriteLine((got == c.Expect ? "OK   " : "FAIL ") +
                          $"开关按钮 可点({c.Desc}) = {got} (期望 {c.Expect})");
    }

    Console.WriteLine();
    Console.WriteLine("---- 布局预览中心提示的空行 (校准窗只显示标题时不该多一行) ----");
    // 问题: 校准窗里中心只有「LED 预览」一行时, 底衬却按两行给高度。
    var ring = new AmbientLight.Controls.LedRingView();
    var hint = ring.FindName("HintText") as TextBlock;
    bool hintHiddenByDefault = hint is not null && hint.Visibility == Visibility.Collapsed;
    ring.SetHint("");
    bool hintHiddenWhenEmpty = hint is not null && hint.Visibility == Visibility.Collapsed;
    ring.SetHint("60灯·4边");
    bool hintShownWhenSet = hint is not null && hint.Visibility == Visibility.Visible;
    ring.SetHint("   ");
    bool hintHiddenWhenBlank = hint is not null && hint.Visibility == Visibility.Collapsed;
    Console.WriteLine((hint is not null ? "OK   " : "FAIL ") + $"能找到 HintText = {hint is not null}");
    Console.WriteLine((hintHiddenByDefault ? "OK   " : "FAIL ") +
                      $"未设提示时整行收起 (不留空行) = {hintHiddenByDefault}");
    Console.WriteLine((hintHiddenWhenEmpty && hintHiddenWhenBlank ? "OK   " : "FAIL ") +
                      $"SetHint(\"\") / 全空白 → 收起 = {hintHiddenWhenEmpty && hintHiddenWhenBlank}");
    Console.WriteLine((hintShownWhenSet ? "OK   " : "FAIL ") + $"SetHint(\"60灯·4边\") → 显示 = {hintShownWhenSet}");

    app.Shutdown();
}

/// <summary>M5.2: 取当前那个 WPF-UI 主题字典**实例** (用来判断"有没有被重新膨胀")。</summary>
static ResourceDictionary? ThemeDictInstance()
{
    foreach (var d in Application.Current.Resources.MergedDictionaries)
    {
        string? uri = d.Source?.ToString();
        if (uri is not null
            && uri.Contains("wpf.ui;", StringComparison.OrdinalIgnoreCase)
            && uri.Contains("theme", StringComparison.OrdinalIgnoreCase))
            return d;
    }
    return null;
}

/// <summary>M5.2: 切一次主题并验证"真的生效了"。返回 false 即 FAIL (而不是让探针崩掉)。
/// 注意用 `updateAccent: false` —— 无窗口的探针宿主里没必要去动 WinRT 强调色那一路。
/// 副作用: 结束时停留在传入的主题上, 调用方负责恢复。
///
/// 判据是**方向性**的, 不锁具体色值:
///   · 卡片底必然是"浅色主题亮 / 深色主题暗";
///   · 主题文字色必须与卡片底反向 (深色主题: 白字 > 底色; 浅色主题: 近黑字 < 底色)。
/// 这两条任意一条不成立, 就说明"切了但资源没换" (Apply 的静默失败模式) 或者换错了。</summary>
static bool SwapTheme(Wpf.Ui.Appearance.ApplicationTheme theme)
{
    try
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            theme, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccent: false);
    }
    catch
    {
        return false;
    }
    if (Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() != theme) return false;

    double cardLum = AmbientLight.Services.ThemePalette.RelativeLuminance(CardSurface());
    var text = Application.Current.TryFindResource("TextFillColorPrimaryBrush") as SolidColorBrush;
    double textLum = text is null ? cardLum : AmbientLight.Services.ThemePalette.RelativeLuminance(text.Color);
    return theme == Wpf.Ui.Appearance.ApplicationTheme.Dark
        ? cardLum < 0.5 && textLum > cardLum
        : cardLum > 0.5 && textLum < cardLum;
}

/// <summary>M5.2: 当前主题下卡片底色的**实际观感**。
/// ⚠️ `CardBackground` 是半透明覆盖色 (浅色 #B3FFFFFF / 深色 #0DFFFFFF) —— 光看颜色通道永远是"接近白",
/// 必须叠在不透明的 `ApplicationBackgroundBrush` (浅 #FFFAFAFA / 深 #FF202020) 上才是肉眼看到的那个颜色。
/// (第一版断言就是这么翻的: 深色主题下"卡片底是暗色"永远判 false。)</summary>
static Color CardSurface()
{
    var bg = Application.Current.TryFindResource("CardBackground") as SolidColorBrush;
    if (bg is null) return Colors.Transparent;
    var baseBg = Application.Current.TryFindResource("ApplicationBackgroundBrush") as SolidColorBrush;
    return baseBg is null ? bg.Color : AmbientLight.Services.ThemePalette.Blend(bg.Color, baseBg.Color);
}

/// <summary>M5.2: 在**指定主题下**取卡片底色 (浅色套色板的参考底)。</summary>
static Color CardSurfaceUnder(Wpf.Ui.Appearance.ApplicationTheme theme)
{
    SwapTheme(theme);
    return CardSurface();
}

static void Layout(FrameworkElement el)
{
    el.UpdateLayout();
    el.Measure(new Size(el.Width, el.Height));
    el.Arrange(new Rect(0, 0, el.Width, el.Height));
    el.UpdateLayout();
}

/// <summary>逻辑树遍历 (无窗口宿主下 Page 可视树不完整, 但逻辑子级齐全)</summary>
static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
{
    foreach (var child in LogicalTreeHelper.GetChildren(root))
    {
        if (child is not DependencyObject d) continue;
        yield return d;
        foreach (var g in LogicalDescendants(d)) yield return g;
    }
}

/// <summary>页面上的"卡片级"元素 (间距定稿值只有它们参与)</summary>
static bool IsCardLevel(FrameworkElement fe) =>
    fe is Wpf.Ui.Controls.Card || fe is Wpf.Ui.Controls.CardControl || fe is Wpf.Ui.Controls.CardExpander;

static void Dump(DependencyObject d, int depth)
{
    var indent = new string(' ', depth * 2);
    var name = d is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? $" #{fe.Name}" : "";
    var text = d is TextBlock tb ? $" \"{tb.Text}\"" : "";
    Console.WriteLine($"{indent}{d.GetType().Name}{name}{text}");
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        Dump(VisualTreeHelper.GetChild(d, i), depth + 1);
}

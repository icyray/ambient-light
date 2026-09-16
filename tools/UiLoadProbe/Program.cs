// UiLoadProbe — 无界面加载探针 (M5.1 UI 轮排查用)
//
// ⚠️ **本文件的判定原则** (轮 29 用户明确要求, 改之前先读这一段):
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
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AmbientLight.Interop;

Console.OutputEncoding = Encoding.UTF8;
var t = new Thread(Run);
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
return;

static void Run()
{
    var app = new AmbientLight.App();   // 不触发 OnStartup (单例守卫/托盘/主窗/核心)
    app.InitializeComponent();          // 入口 Main 里才调用, 探针必须手动调: 加载 App.xaml 全局资源
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
    // 把基准 margin 抹掉"这种真 bug 会表现为"唯独它跟别人不一样" (轮 16 就是这么发现的)。
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

    // 轮 17: banner 点亮态的三条光层必须自带圆角 —— WPF 的 Border **只把 Background 裁到圆角,
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
    // banner 里不得再出现噪点层 (轮 17 用户要求去掉: 无时域抖动时静态噪点只会把画面弄脏)
    bool ditherGone = home.FindName("Dither") == null;
    Console.WriteLine((ditherGone ? "OK   " : "FAIL ") + "Dither 噪点层已移除 (轮 17)");

    // 轮 19: 提示条显隐**回到 IsOpen 原生行为**, 不再用 AnimatedVisibility —— InfoBar 模板是用
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

    // 轮 19: "连接中断 → banner 不点亮 + 胶囊说'正在尝试重连' + 按钮说'关闭'" 属于行为约定,
    // 无头探针验不到 (要真实会话状态机), 留给真机复验; 口径同源由 ApplyBannerLook/IsLit 与
    // "按钮 = App.CoreRunning" 保证。

    // 轮 18 ②/轮 29: "刷新/重新枚举"这类按钮必须是纯图标 (无按钮底/边框)。
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

    // 轮 20: 左侧菜单自适应折叠的决策真值表。最容易改错的一条是"恢复只对自动折的那次生效"
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

    // 轮 16 真 bug 回归 (轮 29 改成不依赖具体数值): AnimatedVisibility 的基准 Margin 只能在元素
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

    // 轮 21: 高级页「关于」组新增两项 + 窗口级 ContentDialog 宿主。
    // ① ContentDialog 宿主: WPF-UI 的 ShowAsync() 在宿主为空时直接抛 "DialogHost was not set",
    //    而那只在用户点按钮时才暴露 → 断言宿主存在且类型正确 (宿主注册发生在 Loaded, 探针不显示窗口,
    //    所以这里查 XAML 声明本身, 不用 GetForWindow)。
    // ② 「恢复默认配置」必须在「配置目录」的**折叠内** —— 破坏性操作不该裸奔在页面上。
    Console.WriteLine();
    Console.WriteLine("---- 高级页「关于」组 (轮 21) ----");
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
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL MainWindow 实例化: {ex.GetType().Name}: {ex.Message}");
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

    // ================= 轮 26: M5.1 主页构件重设计 (docs/M5.1_HOME_REDESIGN_PLAN.md §2~§7) =================
    // 无头宿主里 Loaded 不触发, 所以先让页面自己做一次"不依赖 App/核心"的界面装配,
    // 才有真实的磁贴/指标条结构可断言。
    home.BuildForProbe();

    Console.WriteLine();
    Console.WriteLine("---- §2 固件特效: 图标磁贴网格 ----");
    var fxPanel = home.FindName("FxPanel") as ItemsControl;
    var fxTpl = home.TryFindResource("FxTileTemplate");
    var fxTiles = fxPanel?.Items.OfType<System.Windows.Controls.RadioButton>().ToList() ?? [];
    // 磁贴数必须等于**MCU 模式数** (数据驱动), 不写死 11 —— 加一款特效不该让探针报警
    int mcuCount = AmbientCore.Modes.Count(m => AmbientCore.IsMcuMode(m.Index));
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
    var expectNames = AmbientCore.Modes.Where(m => AmbientCore.IsMcuMode(m.Index)).Select(m => m.Name).ToList();
    bool namesOk = tileNames.SequenceEqual(expectNames);
    Console.WriteLine((namesOk ? "OK   " : "FAIL ") +
                      $"磁贴文案 == 模式表里的名字 = {namesOk}" +
                      (namesOk ? "" : $"  (实得 [{string.Join(", ", tileNames)}])"));
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
    var presetStrip = home.FindName("PresetStrip") as ItemsControl;
    var presetTpl = home.TryFindResource("PresetSwatchTemplate");
    var presetBtns = presetStrip?.Items.OfType<Button>().ToList() ?? [];
    // 只断言**机制与规则**, 不断言条数/几何数值 —— 用户加个预设、调个尺寸不该让探针报警。
    //   ① 每个色块都用自绘模板: 否则退回默认 Button 模板, 悬停会把色块铺成灰色 (轮 27 真实 bug);
    //   ② 预设与自定义之间有竖向分割线 (轮 29 要求的"分区");
    //   ③ 自绘模板里确实是圆 (Ellipse), 且悬停由模板自己的触发器接管。
    bool swatchTplOk = presetTpl is ControlTemplate
                       && presetBtns.Count > 0
                       && presetBtns.All(b => ReferenceEquals(b.Template, presetTpl));
    bool hasDivider = presetStrip?.Items.OfType<System.Windows.Shapes.Rectangle>().Any() == true;
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
    Console.WriteLine("---- §4 banner 底边辉光带 (轮 27: 数值驱动 + alpha 叠加) ----");
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
    // 这是用户明确的诉求("应该是将自己的亮度叠加上去, 不亮的地方要透明")。
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
    Console.WriteLine("---- §5 运行指标 (轮 27: 双层 / 去 CPU / 去着色) ----");
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
    // MetricLabel 的 default 分支会原样返回 key, 所以 label == key 就等于"漏配文案"(真 bug)。
    var allMetricKeys = Enumerable.Range(0, 14)
        .SelectMany(m => AmbientLight.Views.HomePage.MetricKeysForMode(m, true))
        .Distinct().ToList();
    var missingLabels = allMetricKeys.Where(k => AmbientLight.Views.HomePage.MetricLabel(k) == k).ToList();
    Console.WriteLine((missingLabels.Count == 0 ? "OK   " : "FAIL ") +
                      $"指标文案齐备 = {missingLabels.Count == 0}" +
                      (missingLabels.Count > 0 ? $" (缺: {string.Join(", ", missingLabels)})" : "") +
                      "  · 当前措辞: " +
                      string.Join(" / ", allMetricKeys.Select(k => $"{k}={AmbientLight.Views.HomePage.MetricLabel(k)}")));
    // 双层结构: 栏数必须等于指标数 (数据驱动), 每栏 = 标签行 + 数值行, 数值初值不能是空/0
    // (0 会被读成"卡住了"); 等宽分栏。
    var metricsGrid = home.FindName("MetricsGrid") as Grid;
    var keysForProbe = AmbientLight.Views.HomePage.MetricKeysForMode(0, true);
    home.RenderMetricsForProbe(keysForProbe);
    var cells = metricsGrid?.Children.OfType<StackPanel>().ToList() ?? [];
    bool twoLine = cells.Count == keysForProbe.Length && cells.All(c => c.Children.Count == 2
        && c.Children[0] is TextBlock c0 && c0.Text.Length > 0
        && c.Children[1] is TextBlock c1 && c1.Text.Length > 0 && c1.Text != "0");
    bool equalCols = metricsGrid?.ColumnDefinitions.Count == cells.Count
                     && metricsGrid.ColumnDefinitions.All(cd => cd.Width.IsStar && cd.Width.Value == 1);
    Console.WriteLine((twoLine ? "OK   " : "FAIL ") +
                      $"双层结构: {cells.Count} 栏 == 指标数 {keysForProbe.Length}, 每栏 2 行 (标签/数值), " +
                      $"数值初值非空非 0 = {twoLine}");
    Console.WriteLine((equalCols ? "OK   " : "FAIL ") +
                      $"等宽分栏 = {equalCols} (列数 {metricsGrid?.ColumnDefinitions.Count}, 全 1*)");
    // 没指标时 (灯带没开) 收成一行占位, 而不是铺 N 栏空值; 同时指标卡本身要挂着 AnimatedVisibility
    // 且初始是**收起**的 (轮 29: 关闭时整块高度动画收掉, 与拾音页「输入/输出设备」同一套机制)
    home.RenderMetricsForProbe([]);
    bool emptyCollapsed = metricsGrid is { Children.Count: 1 } && metricsGrid.ColumnDefinitions.Count == 1;
    var metricsCard = home.FindName("MetricsCard") as FrameworkElement;
    bool cardWired = metricsCard != null
                     && !AmbientLight.Controls.AnimatedVisibility.GetExpanded(metricsCard);
    Console.WriteLine((emptyCollapsed ? "OK   " : "FAIL ") +
                      $"空指标集 → 单栏占位 (不铺空值) = {emptyCollapsed}");
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
    // 校准向导中心区 (轮 26): 行为断言 —— 打开时可见且两段文字都填了并带上传入的序号, 关闭时收起。
    // 不断言文案措辞与箭头字形 (那是设计值)。
    var calRing = new AmbientLight.Controls.LedRingView();
    var calRow = calRing.FindName("OrientationRow") as FrameworkElement;
    var calBadge = calRing.FindName("StartBadge") as TextBlock;
    var calArrow = calRing.FindName("DirArrow") as TextBlock;
    calRing.SetOrientation(true, 2, clockwise: true);
    bool calShown = calRow?.Visibility == Visibility.Visible
                    && !string.IsNullOrWhiteSpace(calBadge?.Text) && calBadge!.Text.Contains('2')
                    && !string.IsNullOrWhiteSpace(calArrow?.Text);
    calRing.SetOrientation(false, 0, clockwise: false);
    bool calHidden = calRow?.Visibility == Visibility.Collapsed;
    Console.WriteLine((calShown && calHidden ? "OK   " : "FAIL ") +
                      $"校准向导中心区: SetOrientation(true, 2, cw) 可见且文本含序号 2 = {calShown}; " +
                      $"SetOrientation(false) 收起 = {calHidden}");

    Console.WriteLine();
    Console.WriteLine("---- 轮 27~29 反馈项 (只验规则, 不验数值/文案) ----");
    // 磁贴"内容居中"是用户明确的需求 → 断言 ContentPresenter 的对齐 (枚举语义), 不是坐标数字。
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

    app.Shutdown();
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

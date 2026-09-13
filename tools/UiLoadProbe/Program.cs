// UiLoadProbe — 无界面加载探针 (M5.1 UI 轮排查用):
// 1) STA + App 全局资源 (GroupLabel StaticResource / WPF-UI 主题) 环境下实例化全部页面,
//    捕获 XamlParseException 的精确行号/位置/内层异常;
// 2) 强制 Measure/Arrange 触发模板应用, 导出 SettingRow 可视树验证 Header/图标/卡片渲染;
// 3) 主页纯色拾色器回归: 默认 IsExpanded=false (只露 Header) + RGB 三行实际行距一致
//    (量的是布局后的真实间隙, 不是 Margin 字面值)。
// 注意: 只 new App() 不 Run() —— OnStartup (单例守卫/托盘/主窗/核心) 不会触发。
// WPF 控件必须在 STA 线程创建 → 全部工作放 STA 线程执行。
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

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
    Console.WriteLine("---- 卡片间距 (卡片级元素的下边距; 5.1 定稿 = 4) ----");
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
        var bad = new List<string>();
        for (int i = 0; i < cards.Count; i++)
        {
            double bottom = cards[i].Margin.Bottom;
            bool last = i == cards.Count - 1;
            // 非末张必须 4; 末张允许 0 (不留尾距)。轮 16 教训: 只校验"是 0 或 4"会漏掉
            // "中间某张被 AnimatedVisibility 把基准 margin 抹成 0"这种差 4px 的 bug
            if (bottom != 4 && !(last && bottom == 0))
            {
                string name = cards[i].Name.Length > 0 ? "#" + cards[i].Name : "";
                bad.Add($"{cards[i].GetType().Name}{name}={bottom}");
            }
        }
        Console.WriteLine(bad.Count == 0
            ? $"OK   {pt.Name}: {cards.Count} 个卡片级元素, 非末张下边距均为 4"
            : $"FAIL {pt.Name}: {string.Join(", ", bad)}");
    }

    Console.WriteLine();
    Console.WriteLine("---- ModeCard visual check (HomePage 资源模板) ----");
    var home = new AmbientLight.Views.HomePage();
    var tpl = home.TryFindResource("ModeCardTemplate");
    Console.WriteLine($"ModeCardTemplate resolved: {tpl != null} ({tpl?.GetType().Name})");

    Console.WriteLine();
    Console.WriteLine("---- HomePage banner 资源 (光带静默失效回归) ----");
    foreach (var key in new[]
    {
        "BannerBaseBrush", "BannerStreakBlueBrush", "BannerStreakGreenBrush",
        "BannerStreakPurpleBrush", "BannerStreakMonoBrush",
    })
    {
        // 渐变画刷里的动态资源解析失败不会抛异常, 只表现为"光带不显示" → 必须显式断言
        var br = home.TryFindResource(key) as System.Windows.Media.GradientBrush;
        int stops = br?.GradientStops.Count ?? 0;
        bool ok = br is System.Windows.Media.LinearGradientBrush && stops >= 4;
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"{key}: {(br == null ? "未找到" : $"{br.GetType().Name} / {stops} stops")}");
    }

    Console.WriteLine();
    Console.WriteLine("---- HomePage 纯色拾色器: 默认折叠 + RGB 行距 ----");
    var frame2 = new Frame { Width = 760, Height = 1600 };
    frame2.Content = home;
    Layout(frame2);

    var solidCard = home.FindName("SolidCard") as Expander;
    Console.WriteLine($"SolidCard found: {solidCard != null}; 默认 IsExpanded = {solidCard?.IsExpanded}  (期望 False)");

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

    // 轮 18 ②: 设备列表的"刷新/重新枚举"必须是纯图标 (换成 IconOnlyButtonTemplate, 无按钮底/边框)
    var tplRes = home.TryFindResource("IconOnlyButtonTemplate");
    var iconBtns = LogicalDescendants(home).OfType<Wpf.Ui.Controls.Button>()
        .Where(b => b.Content is Wpf.Ui.Controls.SymbolIcon).ToList();
    bool iconOnly = tplRes != null && iconBtns.Count == 2
                    && iconBtns.All(b => ReferenceEquals(b.Template, tplRes));
    Console.WriteLine((iconOnly ? "OK   " : "FAIL ") +
                      $"纯图标按钮: {iconBtns.Count} 个 (期望 2), 全部使用 IconOnlyButtonTemplate = {iconOnly}");
    // 模板换成"只有 ContentPresenter"之后图标还画得出来吗。注意: 无窗口宿主下 **Page 自身模板不套用**
    // (M5.1 老坑), 所以上面那个按钮的可视树本来就是空的 → 必须把模板套到独立宿主上单独布局量。
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

    Console.WriteLine($"{(solidCard != null && solidCard.Margin.Bottom == 4 ? "OK  " : "FAIL")} " +
                      $"SolidCard.Margin = {solidCard?.Margin}  (非末张底距必须是 4)");

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

    // 轮 16 真 bug 回归: AnimatedVisibility 的基准 Margin 只能在元素 Loaded 时捕获
    // (解析期读到的是默认 0 → 展开后卡片间距永久少 4px)。
    // 无头探针不会触发 Loaded → 手动广播一次, 模拟元素真正进入可视树。
    if (solidCard != null)
    {
        solidCard.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var afterLoaded = solidCard.ReadLocalValue(FrameworkElement.MarginProperty);
        AmbientLight.Controls.AnimatedVisibility.SetExpanded(solidCard, true);
        var afterExpand = solidCard.ReadLocalValue(FrameworkElement.MarginProperty);
        bool ok = afterExpand is Thickness t && t.Bottom == 4;
        Console.WriteLine((ok ? "OK   " : "FAIL ") +
                          $"AnimatedVisibility 基准 Margin: Loaded 后={afterLoaded}, 展开后={afterExpand}  (期望 0,0,0,4)");
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
            FrameworkElement Row(string slider) => (FrameworkElement)((Slider)home.FindName(slider)!).Parent;
            var rows = new[] { Row("SRSlider"), Row("SGSlider"), Row("SBSlider") };
            var channel = new[] { "R", "G", "B" };
            var tops = rows.Select(r => LayoutInformation.GetLayoutSlot(r).Y + r.Margin.Top).ToArray();
            for (int i = 0; i < rows.Length; i++)
                Console.WriteLine($"  row {channel[i]}: top={tops[i]:F2}  bottom={tops[i] + rows[i].ActualHeight:F2}" +
                                  $"  (高 {rows[i].ActualHeight:F2}, margin={rows[i].Margin})");
            double gapRg = tops[1] - (tops[0] + rows[0].ActualHeight);
            double gapGb = tops[2] - (tops[1] + rows[1].ActualHeight);
            Console.WriteLine($"  行距 R→G = {gapRg:F2}px, G→B = {gapGb:F2}px, " +
                              $"差值 = {Math.Abs(gapRg - gapGb):F2}px  (期望差值 ≈ 0)");
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

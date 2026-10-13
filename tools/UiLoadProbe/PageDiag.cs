// PageDiag.cs — 整页出图工具（重构前后的**像素对照**用）。
//
// 用法: UiLoadProbe.exe --dump-page <前缀> [zh|en]
//   → <前缀>_home.png / _capture.png / _audio.png / _color.png / _advanced.png / _devices.png
//   第二个参数 = 语言 (M5.3): 省略则用 ConfigStore 默认 (zh); 出英文验收图用 `en`。
//
// 为什么需要它: 大范围结构平移（code-behind 拼控件 → XAML 模板）不允许改变观感,
// 而"数值断言全绿、观感照样错"是踩过的坑 → 每批改动前后出图对照是验收手段。
//
// 无头宿主要点（同 SliderDiag）:
//   · Page 不能直接塞进控件 → 把 `page.Content` 摘出来再托管;
//   · Loaded 不会自己来 → 需要时手动 RaiseEvent 广播;
//   · 出图必须经 VisualBrush 重绘（rtb.Render(带偏移的元素) 会量到假数据）;
//   · 探针没有 OnStartup → Poller 是 null, 而设置页 OnLoaded 会订阅它 → 反射填一个进去。
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AmbientLight.Interop;
using AmbientLight.Services;

namespace UiLoadProbe;

internal static class PageDiag
{
    /// <summary>出图宽度。默认 = MainWindow 默认宽度 880; 可用第 4 个参数出**窄窗**图
    /// (窗口 MinWidth=600) —— 这次就是靠它确认"模式卡说明只在窄窗下才折行"。</summary>
    private static double PageWidth = 880;

    public static void Dump(string prefix, string? lang = null, double width = 880)
    {
        PageWidth = width > 0 ? width : 880;
        var app = new AmbientLight.App();
        app.InitializeComponent();
        // M5.3 语言: 与 App.OnStartup 同序 —— 建页面**之前**先把字典摆对, 页面构造期取到的就是目标语言。
        if (!string.IsNullOrEmpty(lang))
        {
            ConfigStore.App.Lang = lang;
            LanguageService.ApplyAtStartup();
        }
        typeof(AmbientLight.App)
            .GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, app);
        // 页面 OnLoaded 会订阅 Poller（探针里是 null）→ 填一个只是"可订阅"的空服务
        // （不 Start：DispatcherTimer 在无消息泵的探针里不会跑）
        typeof(AmbientLight.App)
            .GetField("_poller", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(app, new PollingService());

        ShotPage("home", prefix, () =>
        {
            var p = new AmbientLight.Views.HomePage();
            p.BuildForProbe();                       // 装配模式区/拾色器/指标条（无头下 OnLoaded 不跑）
            return p;
        });
        ShotPage("capture", prefix, () => new AmbientLight.Views.SettingsPageCapture(), raisePageLoaded: true);
        ShotPage("audio", prefix, () => new AmbientLight.Views.SettingsPageAudio(), raisePageLoaded: true);
        ShotPage("color", prefix, () => new AmbientLight.Views.SettingsPageColor(), raisePageLoaded: true);
        ShotPage("advanced", prefix, () =>
        {
            var p = new AmbientLight.Views.SettingsPageAdvanced();
            p.BuildForProbe();                       // 填下拉（端口/波特率/主题）
            return p;
        });
        ShotDevices(prefix);
        ShotCalibrate(prefix);
        Console.WriteLine("PageDiag 完成。");
    }

    /// <summary>校准窗的**中央面板**（以前不在出图集里 → 它的排版只能靠肉眼开真窗口看）。
    /// 它是 Window（私有构造 + 需要会话）→ 用反射造出来, 只把 Content 摘出来按固定尺寸渲染。
    /// 动过它的按钮/文案时看这张图, 比读 XAML 靠谱 —— 本次就是靠它发现"4 个按钮在等宽四列里
    /// 遇到英文长文案会不会挤"。</summary>
    private static void ShotCalibrate(string prefix)
    {
        try
        {
            var ctor = typeof(AmbientLight.Views.CalibrateWindow).GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int), typeof(string) }, null);
            var win = (Window)ctor!.Invoke(new object[] { 0, "0_1" });
            var root = (FrameworkElement)win.Content;
            win.Content = null;                      // 别让窗口跟着一起渲染 (它按显示器铺满)
            var host = new ContentControl { Content = root, Width = 1280 };
            host.Measure(new Size(1280, 720));
            host.Arrange(new Rect(0, 0, 1280, 720));
            host.UpdateLayout();
            Shot(host, 1280, 720, $"{prefix}_calibrate.png", "calibrate (中央面板 + 四色底)");
            win.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ⚠️ calibrate 出图失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>渲染一个 Page 的完整内容（摘出 Content 托管 → 布局 → 出图）。</summary>
    private static void ShotPage(string name, string prefix, Func<Page> make, bool raisePageLoaded = false)
    {
        try
        {
            var page = make();
            if (raisePageLoaded)
            {
                // 无头宿主里 Page.Loaded 不会触发；设置页的"从配置重读"手写在 Loaded 里，
                // 这里手动广播一次（handler 幂等：只在 _loading 保护下写控件）。
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, page));
            }
            var root = (FrameworkElement)page.Content;
            page.Content = null;                     // Page 只能挂在 Window/Frame 下 → 摘内容单飞
            var host = new ContentControl { Content = root, Width = PageWidth };
            host.Measure(new Size(PageWidth, double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, PageWidth, Math.Max(host.DesiredSize.Height, 10)));
            host.UpdateLayout();
            RaiseLoaded(root);                       // AnimatedVisibility 的基准 Margin 在 Loaded 捕获
            host.Measure(new Size(PageWidth, double.PositiveInfinity));   // 基准就位后高度可能变
            double h = Math.Max(host.DesiredSize.Height, 10);
            host.Arrange(new Rect(0, 0, PageWidth, h));
            host.UpdateLayout();
            Shot(host, PageWidth, h, $"{prefix}_{name}.png", name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ⚠️ {name} 出图失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>设备行的三种代表状态（就绪 / 运行中 / 灯带不在位）—— 用假数据渲染真模板。
    /// 设备行是结构平移中改动最大的一块，出图对照就靠它。</summary>
    private static void ShotDevices(string prefix)
    {
        try
        {
            var home = new AmbientLight.Views.HomePage();
            home.BuildForProbe();                    // 顺带把 ConfigStore 的 "0_1" 默认项准备好

            var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = "设备行 — 代表状态（就绪 / 运行中 / 灯带不在位）",
                FontSize = 12, Margin = new Thickness(0, 0, 0, 6),
            });
            // 就绪：枚举到、无会话
            panel.Children.Add(home.BuildDeviceRow(FakeDev(0, 1, "COM5", inUse: 0), null, -1));
            // 运行中：有会话（Running + 供电 bit0=1）
            panel.Children.Add(home.BuildDeviceRow(
                FakeDev(0, 1, "COM5", inUse: 1), FakeStatus(0, 1, "COM5", AmbientCore.StateRunning, 1), 0));
            // 灯带不在位：会话 DEV_LOST（黑底色 = 不再有第二行小字时也对照得到）
            panel.Children.Add(home.BuildDeviceRow(
                FakeDev(0, 2, "COM6", inUse: 1), FakeStatus(0, 2, "COM6", AmbientCore.StateDevLost, 0), 1));

            panel.Measure(new Size(PageWidth, double.PositiveInfinity));
            double h = Math.Max(panel.DesiredSize.Height + 24, 10);
            panel.Arrange(new Rect(0, 0, PageWidth, h));
            panel.UpdateLayout();
            Shot(panel, PageWidth, h, $"{prefix}_devices.png", "devices");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ⚠️ devices 出图失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static unsafe ACDeviceInfo FakeDev(int type, int id, string port, int inUse)
    {
        var d = default(ACDeviceInfo);
        d.devType = type;
        d.devId = id;
        d.inUse = inUse;
        var bytes = Encoding.UTF8.GetBytes(port);
        // d 是局部变量 → 固定缓冲已被固定, 直接索引 (再套 fixed 会 CS0213)
        for (int i = 0; i < bytes.Length && i < 15; i++) d.comPort[i] = bytes[i];
        return d;
    }

    private static unsafe ACStatus FakeStatus(int type, int id, string port, int state, uint ledStatus)
    {
        var st = default(ACStatus);
        st.structSize = (uint)sizeof(ACStatus);
        st.state = state;
        st.devType = type;
        st.devId = id;
        st.ledStatusByte = ledStatus;
        var bytes = Encoding.UTF8.GetBytes(port);
        for (int i = 0; i < bytes.Length && i < 15; i++) st.comPort[i] = bytes[i];
        return st;
    }

    private static void RaiseLoaded(DependencyObject d)
    {
        if (d is FrameworkElement fe) fe.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) RaiseLoaded(VisualTreeHelper.GetChild(d, i));
    }

    /// <summary>经 VisualBrush 重绘到固定深色底（与主题无关，保证"改前/改后"可比）。</summary>
    private static void Shot(FrameworkElement el, double w, double h, string file, string caption)
    {
        double ew = el.ActualWidth > 2 ? el.ActualWidth : w;
        double eh = el.ActualHeight > 2 ? el.ActualHeight : h;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), null,
                             new Rect(0, 0, ew, eh));
            dc.DrawRectangle(new VisualBrush(el)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            }, null, new Rect(0, 0, ew, eh));
        }
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(ew), (int)Math.Ceiling(eh), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var fs = File.Create(file);
        enc.Save(fs);
        Console.WriteLine($"    dumped -> {file} ({rtb.PixelWidth}x{rtb.PixelHeight}) · {caption}");
    }
}

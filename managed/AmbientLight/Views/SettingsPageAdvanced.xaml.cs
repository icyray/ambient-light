// SettingsPageAdvanced.xaml.cs — 高级设置: minB/熄屏策略/colorfix + 测试图 + 配置目录
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AmbientLight.Interop;
using AmbientLight.Localization;
using AmbientLight.Services;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class SettingsPageAdvanced : Page
{
    // ComboBox 显示序 (熄灭/最暗/保持) ↔ 配置值 (2/1/0)
    private static readonly int[] PolicyChoices = [2, 1, 0];
    private bool _loading = true;   // InitializeComponent 解析期即有事件, 初值阶段一律忽略

    // ---- 连接: 端口 / 波特率 (§8.3) --------------------------------------
    /// <summary>波特率候选 (值) —— **与厂商同一套** (115200/57600/38400/28800/19200/14400/9600),
    /// 默认 2000000 排首位。CDC 虚拟串口下这个值通常被设备端忽略 (实测对波特率不敏感),
    /// 保留给 USB 转串口桥接或旧固件。</summary>
    public static readonly int[] BaudChoices =
        [2000000, 115200, 57600, 38400, 28800, 19200, 14400, 9600];

    /// <summary>波特率下拉的显示文案 —— 与 `BaudChoices` **同序同长**。
    /// ⚠️ 轮 33 踩过: 只写了 BaudChoices 却忘了给 ComboBox 设 ItemsSource → 下拉是空的
    /// (空列表上设 SelectedIndex 不报错也不生效)。所以下面构造里显式绑定, 探针也断言"确实填上了"。</summary>
    private static string[] BuildBaudLabels() =>
        [Loc.T("Adv.Baud.Default"), "115200", "57600", "38400", "28800", "19200", "14400", "9600"];

    // ---- 主题 (M5.2) ------------------------------------------------------
    /// <summary>主题下拉的值 (与显示文案**同序同长**)。
    /// ⚠️ 轮 33 教训: 有了候选表还必须给 ComboBox 设 ItemsSource —— 空列表上设 SelectedIndex
    /// 既不报错也不生效, 下拉就是空的。探针因此断言"控件里真有项 + 与配置往返一致"。</summary>
    public static readonly ThemeChoice[] ThemeChoices =
        [ThemeChoice.System, ThemeChoice.Light, ThemeChoice.Dark];

    private static string[] BuildThemeLabels() =>
        [Loc.T("Adv.Theme.System"), Loc.T("Adv.Theme.Light"), Loc.T("Adv.Theme.Dark")];

    /// <summary>端口下拉的一项。`Port = null` 表示"自动探测" (写回空串)。</summary>
    public sealed record PortItem(string? Port, string Label);
    /// <summary>端口下拉的选项 —— **纯函数**, 探针打真值表。规则:
    ///   ① 第一项永远是「自动检测」(Port = null → 配置写空串, native 才会按 VID/PID 自动跟随);
    ///   ② 本机所有 COM 口都列出来 (manual 指定就是为"认不到 VID/PID"准备的);
    ///   ③ 配置里那个端口若当前**不在位**, 也要列出来并标注 —— 否则用户会以为设置被吞了
    ///      (native 侧对"配置端口消失"有兜底: 唯一匹配就跟随, 多匹配就逐个握手判型)。
    /// ⚠️ 轮 34 补: 判"在位"必须用**本机全部 COM 口**(注册表 SERIALCOMM), **不能**拿 native 那份
    /// 按 VID/PID 过滤过的列表 —— 否则用户填的 COM4 会被当成"端口消失", native 就偷偷连回灯带
    /// (实测 bug: 端口填 COM4, 主页开启却连到 COM5)。**手动指定后不再回落**, 握手失败就是失败。</summary>
    public static List<PortItem> BuildPortItems(string configured, IEnumerable<string> present)
    {
        var list = new List<PortItem> { new(null, Loc.T("Adv.Port.Auto")) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in present)
            if (!string.IsNullOrWhiteSpace(p) && seen.Add(p))
                list.Add(new PortItem(p, p));
        if (!string.IsNullOrWhiteSpace(configured) && seen.Add(configured))
            list.Add(new PortItem(configured, configured + " " + Loc.T("Adv.Port.NotPresent")));
        return list;
    }

    /// <summary>下拉选项 → 配置值 (纯函数): 自动探测 = 空串。</summary>
    public static string PortValueOf(PortItem? item) => item?.Port ?? "";

    public SettingsPageAdvanced()
    {
        InitializeComponent();
        BaudCombo.ItemsSource = BuildBaudLabels();   // 静态内容 → 构造期就绑; 选中项由 ReloadFromConfig 定
        ThemeCombo.ItemsSource = BuildThemeLabels(); // 同上 (M5.2)
        Controls.WheelScroll.Attach(this);   // 滚轮滚动由外层滚动器承担
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ReloadFromConfig();

    /// <summary>探针用: 无头宿主里 Page 的 Loaded 不触发, 手工走一遍控件装配,
    /// 让探针能验"下拉是否真的被填上、选中项是否与配置一致"。轮 33 就是漏了下拉填充
    /// (探针当时只断言了常量数组, 没断言控件) 才让空下拉漏到用户手里。</summary>
    public void BuildForProbe() => ReloadFromConfig();

    /// <summary>把配置读进控件。Loaded 时走一遍; 「恢复默认配置」之后也要再走一遍。
    /// 全程 _loading=true → 各控件的事件处理器一律不回写 (否则会把刚恢复的默认值又覆盖回去)。</summary>
    private void ReloadFromConfig()
    {
        _loading = true;
        var dev = ConfigStore.Device;
        MinBrightnessSlider.Value = Math.Clamp(dev.MinBrightness, 1, 25);
        WaitDispCombo.SelectedIndex = Array.IndexOf(PolicyChoices, dev.WaitDispPolicy) is >= 0 and int i ? i : 0;
        ColorfixToggle.IsChecked = dev.Colorfix;
        SfloorSlider.Value = Math.Clamp(dev.Sfloor, 0, 255);
        AutoStartToggle.IsChecked = ConfigStore.App.AutoStart;
        StartMinimizedToggle.IsChecked = ConfigStore.App.StartMinimized;
        AutoStartCoreToggle.IsChecked = ConfigStore.Device.AutoStartCore;
        BannerGlowToggle.IsChecked = ConfigStore.App.BannerGlow;   // M5.1 §4
        ThemeCombo.SelectedIndex = Array.IndexOf(ThemeChoices, ThemeService.Parse(ConfigStore.App.Theme));  // M5.2 (Parse 永不失败)

        // 连接 (§8.3)
        ReloadPorts();
        int bi = Array.IndexOf(BaudChoices, dev.BaudRate);
        BaudCombo.SelectedIndex = bi >= 0 ? bi : 0;                 // 非候选值 (手改过 json) 落回默认

        string appVer = CurrentVersion;
        uint core = AmbientCore.Version();
        VersionText.Text = $"AmbientLight {appVer} · AmbientCore v{(core >> 16) & 0xFF}.{(core >> 8) & 0xFF}.{core & 0xFF}";
        _loading = false;
    }

    private static string CurrentVersion => typeof(SettingsPageAdvanced).Assembly
        .GetName().Version?.ToString(3) ?? "?";

    /// <summary>重建端口下拉 (本机所有 COM 口 + 配置里那个可能不在位的)。只改选中项, 不写配置。</summary>
    private void ReloadPorts()
    {
        string cur = ConfigStore.Device.ComPort;
        var items = BuildPortItems(cur, SerialPorts.All());
        PortCombo.ItemsSource = items;
        int idx = items.FindIndex(it => string.Equals(PortValueOf(it), cur, StringComparison.OrdinalIgnoreCase));
        PortCombo.SelectedIndex = idx >= 0 ? idx : 0;      // 兜底: 至少落在"自动探测"上
    }

    private void OnRefreshPorts(object sender, RoutedEventArgs e)
    {
        _loading = true;                                   // 重列期间别把选中变化写回配置
        ReloadPorts();
        _loading = false;
    }

    private void OnPortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PortCombo.SelectedItem is not PortItem item) return;
        string value = PortValueOf(item);
        if (string.Equals(value, ConfigStore.Device.ComPort, StringComparison.OrdinalIgnoreCase)) return;
        ConfigStore.Device.ComPort = value;
        App.Instance.ApplyConfigDebounced();               // native: com/baud 变了 → 关端口重连
    }

    private void OnBaudChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BaudCombo.SelectedIndex < 0) return;
        int v = BaudChoices[BaudCombo.SelectedIndex];
        if (v == ConfigStore.Device.BaudRate) return;
        ConfigStore.Device.BaudRate = v;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnMinBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinBrightnessValue != null) MinBrightnessValue.Text = $"{(int)e.NewValue}";
        if (_loading) return;
        ConfigStore.Device.MinBrightness = (int)e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnWaitDispChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || WaitDispCombo.SelectedIndex < 0) return;
        ConfigStore.Device.WaitDispPolicy = PolicyChoices[WaitDispCombo.SelectedIndex];
        App.Instance.ApplyConfigDebounced();
    }

    private void OnColorfixChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.Device.Colorfix = ColorfixToggle.IsChecked == true;
        App.Instance.ApplyConfigDebounced();
    }

    private void OnSfloorChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SfloorValue != null) SfloorValue.Text = $"{(int)e.NewValue}";
        if (_loading) return;
        ConfigStore.Device.Sfloor = (int)e.NewValue;
        App.Instance.ApplyConfigDebounced();
    }

    // ---- 开机自启 / 启动收托盘 ------------------------------------------
    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool on = AutoStartToggle.IsChecked == true;
        string? err = on ? AutoStart.Enable() : AutoStart.Disable();
        if (err != null)
        {
            _loading = true;                        // 失败回滚开关显示
            AutoStartToggle.IsChecked = !on;
            _loading = false;
            _ = Controls.AppDialogs.ShowAsync(Loc.T("App.AutoStart.FailedTitle"),
                Loc.F("App.AutoStart.Fail", err), closeText: Loc.T("Common.GotIt"));
            return;
        }
        ConfigStore.App.AutoStart = on;
        ConfigStore.SaveApp();
    }

    private void OnStartMinimizedChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.App.StartMinimized = StartMinimizedToggle.IsChecked == true;
        ConfigStore.SaveApp();
    }

    private void OnAutoStartCoreChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.Device.AutoStartCore = AutoStartCoreToggle.IsChecked == true;
        ConfigStore.SaveDevice();
    }

    /// <summary>banner 底边辉光开关 (M5.1 §4): 只写 app.json, 主页每次轮询读它, 无需热更核心。</summary>
    private void OnBannerGlowChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ConfigStore.App.BannerGlow = BannerGlowToggle.IsChecked == true;
        ConfigStore.SaveApp();
    }

    /// <summary>主题 (M5.2): 写 app.json + **立刻应用**(不重启)。
    /// Apply 会整个换掉主题资源字典 → 各页面靠 DynamicResource 自动跟着变;
    /// 主页那几处 code-behind 一次性赋值的刷子由它自己订阅 ApplicationThemeManager.Changed 重建。</summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        int i = ThemeCombo.SelectedIndex;
        if (i < 0 || i >= ThemeChoices.Length) return;
        var choice = ThemeChoices[i];
        string key = ThemeService.ToKey(choice);
        if (key == ConfigStore.App.Theme) return;      // 没变就别白跑一次 Apply (它会重刷 backdrop)
        ConfigStore.App.Theme = key;
        ConfigStore.SaveApp();
        ThemeService.Apply(choice);
    }

    // ---- 配置目录 --------------------------------------------------------
    private void OnOpenConfigDir(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigStore.ConfigDir}\""));
        }
        catch { /* 忽略 */ }
    }

    // ---- 版本更新 (轮 21) -------------------------------------------------
    // 现状: 软件还没发布到 GitHub, 这里先把 UI 与对话框通道跑通。
    // 接入点 (发布后): GET https://api.github.com/repos/<owner>/<repo>/releases/latest
    //   → 取 JSON 的 tag_name, 与 CurrentVersion 做版本比较;
    //   有新版 → 同一对话框里给出新版号 + Release 页面链接; 无新版 → "已是最新版本"。
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        await Controls.AppDialogs.ShowAsync(Loc.T("Adv.Update.Title"),
            Loc.F("Adv.Update.Body", CurrentVersion), closeText: Loc.T("Common.GotIt"));
    }

    // ---- 恢复默认配置 (轮 21): 藏在「配置目录」折叠内。破坏性操作, 先确认再动手 ----
    private async void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        bool ok = await Controls.AppDialogs.ShowAsync(Loc.T("Adv.Restore.Title"),
            Loc.T("Adv.Restore.Body"),
            primaryText: Loc.T("Adv.Restore.Confirm"), primaryDanger: true,
            closeText: Loc.T("Common.Cancel"));
        if (!ok) return;

        ConfigStore.RestoreDefaults();
        // 开机自启的快捷方式也要跟着回到默认 (与 App.OnStartup 的自愈规则一致, 否则会残留)
        if (!ConfigStore.App.AutoStart && AutoStart.IsActive()) AutoStart.Disable();
        App.Instance.ApplyConfig();     // 默认值立即下发给当前会话 (灯带随之变化)
        ReloadFromConfig();             // 本页控件回到默认值。其余页面未做导航缓存, 切过去时会重读
    }

    // 统一对话框已提到 Controls/AppDialogs (轮 34 补: 校准窗、启动失败提示等也用它, 一处实现)
}

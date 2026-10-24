// HomePage.xaml.cs — 主页: banner(状态/开关/底边辉光) / 亮度 / 模式 / 拾色器 / 固件特效 /
//                   布局预览 / 指标条 / 设备
// 契约 = docs/PROJECT.md §7.6 (主页结构与观感约束):
//   §2 固件特效 = 图标磁贴网格 + 折叠头「当前 XX」胶囊;
//   §3 纯色拾色器 = H + S 两根滑块 (V 就是上方「亮度」行, 不再另设第三根);
//   §4 banner 底边辉光 = 数值驱动的模糊光带 (亮灯占比/音量/亮度), 窗口不可见时不更新;
//   §5 运行指标 = 指标集随模式换 (MetricKeysForMode 纯函数), 双层 (去 CPU/去着色);
//   §6 设备信息 = 结构化行 (类型图标 + 语义胶囊 + 第二行小字) / 「当前」用 3px 强调条;
//   §7 几何环收进折叠「布局预览」, 中心只放静态几何参数。
// 另: 显示器行文案 = `编号 分辨率 适配器`; 预设色块 = 自绘圆形 + 覆写悬停; 磁贴内容居中。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AmbientLight.Controls;
using AmbientLight.Interop;
using AmbientLight.Localization;
using AmbientLight.Services;
using Wpf.Ui.Controls;

namespace AmbientLight.Views;

public partial class HomePage : Page
{
    private bool _loaded;                       // 初值阶段忽略一切配置下发
    private int _lastPolledState = -1;
    private int _lastHeroMode = -1;             // banner 大字/说明只在模式变化时改写
    private long _lastBannerLook = long.MinValue;   // (state|mode) 组合, 变则刷新 banner 视觉
    private string _lastBlockLabel = "\0";          // 上次渲染时的"开启失败"标签 (变了要重画 banner)
    private (string Label, string Detail)? _block;  // 开启失败原因 (胶囊短标签 + InfoBar 详情); null = 无
    private bool _streakOnA = true;             // 当前前台光带是 A 还是 B (交叉淡化用)
    private string? _lastStreakKey;             // 光带色系; 未变则不重复交叉淡化
    private bool _toggling;                     // 快速连点总开关时的重入守卫
    private long _lastRingSig = -1;             // (mode|leds|sides|dir|state|rotate) 变化才重配预览
    private string _lastCenter = "\0";          // 布局预览中心区的静态几何文本
    private string _metricsSig = "\0";          // 指标集签名, 变则重建指标条
    private bool _devRowsPending;               // 设备行是在"会话身份还没回填"时刷的, 需要补刷一次
    private bool _devicesRefreshing;            // 设备枚举在飞 (枚举会独占打开端口, 不能并发)
    private bool _devicesRefreshQueued;         // 刷新期间又来了新事件 → 结束后补跑一次
    private bool _updatingMode;                 // 程序化设 IsChecked 时不回环下发
    private bool _hexEditing;                   // HexBox / H,S 滑块的程序化写入期间避免回环
    private readonly Dictionary<int, System.Windows.Controls.Control> _modeBtns = new();
    private List<MetricCell> _metricItems = [];

    // banner 文字/胶囊用**自有画刷**: 只有自己的 SolidColorBrush 才能做 ColorAnimation
    // (主题资源刷子是共享且可能已冻结的)
    private readonly SolidColorBrush _titleBrush = new();
    private readonly SolidColorBrush _subBrush = new();
    private readonly SolidColorBrush _portBrush = new();
    private readonly SolidColorBrush _fpsBrush = new();
    private readonly SolidColorBrush _pillBgBrush = new();
    private readonly SolidColorBrush _pillTextBrush = new();
    private readonly SolidColorBrush _dotBrush = new();

    // ---- §4 底边辉光 ----
    // 形式 = 一条"中心亮、向两边衰减"的模糊光带, 亮度由**一个数值**决定
    // (追光 = 亮着的灯占比 / 拾音 = 音量 / 纯色 = 亮度)。形状与颜色只跟模式有关 → 画刷是静态的,
    // 每次只改 Opacity。这样:
    //   ① 视觉上是 WPF 动画插值出来的, 不受轮询频率限制
    //      (逐段重建 64 段画刷 4 次/秒曾是卡顿来源);
    //   ② 暗处 alpha=0 → 辉光是"叠加上去的", 不会把 banner 压成一条黑带。
    // 自带 10Hz 定时器: 每次只重算一个标量, 比让整个状态轮询提速便宜得多
    // (而且核心写 ledRgb / audioLevel 的频率本来就远高于 4Hz, 提出来的采样是"真"的)。
    private const int GlowTickMs = 100;
    private const int GlowEaseMs = 130;         // 常规跟踪 (跟随数值变化, 要跟得紧)
    private const int GlowBreathPeriodMs = 5000;   // 固件特效的呼吸周期
    private const double GlowBreathMin = 0.3, GlowBreathAmp = 0.8;
    /// <summary>未知模式 (表外号) 的辉光: 恒定中性微光, **不呼吸** —— 呼吸在既有语义里绑定
    /// "固件在渲染", 未知时不该假装。取呼吸曲线的时间平均 (Min + Amp/2), 这样从固件特效切过去
    /// 不会突然跳一下。</summary>
    private const double GlowSteadyLevel = GlowBreathMin + GlowBreathAmp * 0.5;
    private static readonly double[] GlowBellAlphas =
        [0, 0.05, 0.16, 0.34, 0.58, 1.0, 0.58, 0.34, 0.16, 0.05, 0];
    private DispatcherTimer? _glowTimer;
    private LinearGradientBrush? _glowBrush;    // 常驻画刷: 颜色做动画, 不重建 (否则会闪 + 卡)
    private string _glowBrushKey = "\0";        // 当前画刷对应的模式色系
    private bool _glowApplied;                  // 首次不做动画直接就位
    private DateTime _glowHoldUntil;            // 换模式期间压住常规跟踪, 让 260ms 过渡走完
    private double _glowBreath;                 // 固件特效呼吸相位
    private ACStatus _lastStatus;               // 最近一次状态轮询快照 (无会话时辉光用它兜底)

    /// <summary>亮度参数对这个模式有意义吗 —— **纯函数** (探针打真值表)。
    /// 语义由 `ModeCatalog` 表里的 `Bright` 列给: `Multiply` 直接乘 / `Cap` 作为上限 /
    /// `None` 没有落点 (固件特效、以及我们不认识的模式) → UI 禁用滑块。
    /// (用户 2026-10-08 提的: 免得用户以为那能调)</summary>
    public static bool BrightnessEnabled(int mode) => ModeCatalog.Of(mode).Bright != BrightKind.None;

    public HomePage()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);      // 滚轮滚动由外层滚动器承担
        CollectPresetButtons();
        EnsureGlowBrush();                 // 常驻画刷 (颜色做动画, 不重建)

        HeroTitle.Foreground = _titleBrush;
        HeroSub.Foreground = _subBrush;
        PortText.Foreground = _portBrush;
        BannerFpsText.Foreground = _fpsBrush;
        StatePill.Background = _pillBgBrush;
        StatePillText.Foreground = _pillTextBrush;
        StateDot.Fill = _dotBrush;
        ApplyBannerLook(AmbientCore.StateStopped, 0, animate: false);   // 初始 = 未点亮的卡片灰

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Instance.Poller.Polled += OnPolled;
        App.Instance.Poller.DevicesChanged += OnDevicesChanged;
        // 会话状态变化也要刷设备行 —— 托盘菜单开关、灯带物理断开都不经过主页的按钮,
        // 主页自己收不到信号。状态是**边沿触发**的 (只在真的变了时来一次), 不会造成刷新风暴。
        App.Instance.Poller.StateChanged += OnCoreStateChanged;
        // M5.2: 主题 / **强调色** 变化后, 页内几处 **code-behind 一次性赋值** 的主题刷
        // (banner 文字/胶囊、设备行语义色对、自定义色槽的选中圈) 不会自己更新 ——
        // 容器靠 DynamicResource 是自动的, 但"当时取到的那个刷子实例"不是。
        // 用 ThemeService.AppearanceChanged 而不是 ApplicationThemeManager.Changed:
        // 后者只在**换主题**时触发, 强调色变化不会触发它。
        ThemeService.AppearanceChanged += OnAppearanceChanged;
        // M5.3 语言: 与主题同一套路 —— XAML 侧自动, code-behind 一次性/按签名短路的文案靠这里重建
        LanguageService.LanguageChanged += OnLanguageChanged;

        CollectModeButtons();
        var dev = ConfigStore.Device;
        BrightnessSlider.Value = dev.Brightness * 100.0;    // 先设初值 (事件未挂)
        BrightnessValueText.Text = $"{(int)(dev.Brightness * 100)}%";
        BrightnessSlider.ValueChanged += OnBrightnessChanged;
        LoadSolidFromConfig();                              // 拾色器初值 (订阅在其后)
        HueSlider.ValueChanged += OnHsvChanged;
        SatSlider.ValueChanged += OnHsvChanged;
        _loaded = true;

        // 重建后"首帧就位": 立刻用最近一次轮询快照刷一遍 —— 否则 banner 停在构造期的"已停止"外观,
        // 要等下一次 250ms tick 才刷 (切回主页时可见一帧"已停止", 2026-10-14 用户报)。
        // 喂入幂等, 下一次 tick 照常覆盖; 页面仍每次重建 (资源回收策略不变)。
        if (App.Instance?.Poller is { HasPolled: true } poller0)
            OnPolled(poller0.LastSelected);
        StartGlowTimer();                   // 辉光自带 10Hz 刷新 (主状态轮询仍是 4Hz)
        _ = RefreshDevicesAsync();
        _ = SettleDeviceRowsAsync();        // 应用刚启动时 StartAllAsync 可能还在飞, 落地后要再刷一次
        // 指标卡首帧就位 (默认收起; 已自动连上时立刻展开), 免得先闪一下占位行
        AnimatedVisibility.SetExpanded(MetricsCard, App.Instance.CoreRunning);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Instance.Poller.Polled -= OnPolled;
        App.Instance.Poller.DevicesChanged -= OnDevicesChanged;
        App.Instance.Poller.StateChanged -= OnCoreStateChanged;
        ThemeService.AppearanceChanged -= OnAppearanceChanged;
        LanguageService.LanguageChanged -= OnLanguageChanged;
        StopGlowTimer();
        _lastPolledState = -1;
    }

    /// <summary>M5.3 换语言后的"重取 + 重建" (与主题那套同源)。
    /// XAML 侧的 `{DynamicResource}` 已由字典替换自动刷新, 这里补的是 **code-behind 一次性赋值**
    /// 和**按签名短路**的那几处:
    ///   · `_lastHeroMode` —— banner 大字 (模式名) / 副标题 (模式说明) 只在模式变化时改写;
    ///   · `_lastCenter` / `_lastRingSig` —— 布局预览的中心几何文本与折叠头摘要;
    ///   · 其余 (banner 文字色/指标条标签/设备行/显示器行/自定义色槽) 复用主题那次的处理。
    /// 注: 亮度说明 / 开关文案 / 端口行 / 提示条 / 诊断行是**每次轮询都重取**的, 不用管。</summary>
    private void OnLanguageChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            if (!_loaded) return;
            _lastHeroMode = -1;
            _lastCenter = "\0";
            _lastRingSig = -1;
            RefreshModeButtons();         // 固件特效折叠头的「当前 XX」胶囊
            OnAppearanceChanged();
        });

    /// <summary>M5.2 主题 / 强调色变化后的"重取 + 重建"(见 OnLoaded 里的说明)。
    /// 复位那两个签名, 否则 ApplyBannerLook / CrossFadeStreak 会因为"看起来没变"而短路不刷色。
    /// ⚠️ 光带/辉光的键本身已含主题 (见 ColorKey), 所以它们本来就会自己重解 ——
    /// 这里复位主要是为了 banner 文字色与指标条这些"不带主题的签名"。</summary>
    private void OnAppearanceChanged()
        => Dispatcher.BeginInvoke(() =>
        {
            if (!_loaded) return;
            _lastBannerLook = long.MinValue;
            _lastStreakKey = null;
            _metricsSig = "\0";                   // 指标条的字色也是 code-behind 赋的, 重建一次
            ApplyBannerLook(_lastPolledState < 0 ? AmbientCore.StateStopped : _lastPolledState,
                            ConfigStore.Device.ModeIndex, animate: false);
            // (模式区不用重建: 磁贴图标/说明文字随 XAML 走 DynamicResource, 换主题自动跟)
            RefreshCustomSlots();                 // 自定义槽的描边/选中圈 (强调色) 重取
            RefreshMonitors();
            _ = RefreshDevicesAsync();            // 设备行的语义胶囊随深/浅换套
        });

    /// <summary>盯着"选中会话的身份何时回填"。会话刚起时 `devType` 还是 -1、`ComPort` 还空着,
    /// 那会儿刷出来的设备行只能是"读取中…"; 等它回填了就要再刷一遍, 行才会变成
    /// "屏幕灯带 · id 1 · 运行中"。这样就不必猜"启动任务还在不在飞"。</summary>
    private void WatchDeviceIdentity(ACStatus st)
    {
        if (App.Instance.SlotOf(ConfigStore.SelectedKey) < 0) return;
        bool settled = st.devType >= 0 && st.ComPort.Length > 0;
        if (!settled)
        {
            _devRowsPending = true;
        }
        else if (_devRowsPending)
        {
            _devRowsPending = false;
            _ = RefreshDevicesAsync();
        }
    }

    /// <summary>等"应用启动时的那次总开启"落地后再刷一遍设备行 (与 WatchDeviceIdentity 互补:
    /// 那个盯选中设备的身份回填, 这个兜住"启动任务在飞"这一情形)。
    /// 不加这段的话首次打开会看到 `未知 · id -1 + 端口被占用`(实测现象): 那一刻端口已经被核心
    /// 占着(探测必然握手失败), 但 `_slots` 里还没条目 —— 看起来就像"被别的程序占了"。
    /// **只 await 在飞的任务, 不新建** —— 否则用户主动关掉灯带后一切回主页就又给开起来了。</summary>
    private async Task SettleDeviceRowsAsync()
    {
        var t = App.Instance.StartTaskInFlight;
        if (t == null) return;
        try { await t; } catch { /* 失败原因由 banner/设备行自己表达 */ }
        await Task.Delay(300);              // 等会话的 ComPort / devType 回填
        _ = RefreshDevicesAsync();
    }

    /// <summary>窗口真的在屏幕上 (且没最小化) 时才跑 4Hz 视觉更新 —— 硬规则: 窗口不可见/收进托盘
    /// 时完全不更新 (不看时开销为 0)</summary>
    private bool IsUiVisible()
    {
        var w = Window.GetWindow(this);
        return w is { IsVisible: true } && w.WindowState != WindowState.Minimized;
    }

    // ---- 模式选择 (M5.1 §2): 三张主模式卡 + 固件特效图标磁贴 ----
    // 控件本体 = XAML 静态声明 (静态内容不动态生成); 这里只做两件事:
    //   ① 收集控件 (按 Tag=模式号) 供选中态同步; ② 刷新选中态与相关容器的显隐/文案。
    private void CollectModeButtons()
    {
        if (_modeBtns.Count > 0) return;
        foreach (var rb in PrimaryModeGrid.Children.OfType<System.Windows.Controls.RadioButton>())
            if (int.TryParse(rb.Tag as string, out int i)) _modeBtns[i] = rb;
        foreach (var rb in FxPanel.Children.OfType<System.Windows.Controls.RadioButton>())
            if (int.TryParse(rb.Tag as string, out int i)) _modeBtns[i] = rb;
        RefreshModeButtons();
    }

    /// <summary>模式卡 / 磁贴被点选 (XAML Checked 的统一入口; Tag = 模式号)。
    /// 程序化改选 (_updatingMode) 期间不回环下发。</summary>
    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingMode) return;
        if (sender is System.Windows.Controls.RadioButton { Tag: string tag }
            && int.TryParse(tag, out int idx))
            SetMode(idx);
    }

    // 固件磁贴的图标/名称已随控件一起写进 XAML (HomePage.xaml 的 FxPanel);
    // 主模式卡的图标底渐变也在 XAML (ModeTile*Brush 资源) —— 这两张映射表因此删除。

    private void RefreshModeButtons()
    {
        int cur = ConfigStore.Device.ModeIndex;
        _updatingMode = true;
        foreach (var (index, ctl) in _modeBtns)
            if (ctl is System.Windows.Controls.RadioButton rb) rb.IsChecked = index == cur;
        _updatingMode = false;
        var modeDef = ModeCatalog.Of(cur);
        bool solid = modeDef.Ui == UiSection.SolidPicker;
        // 纯色拾色器每次展示时回到**折叠**态 (只露 Header: HEX 输入 + 色块预览);
        // 预设色板/H/S 滑块由用户点标题条自行展开
        if (solid) SolidCard.IsExpanded = false;
        bool fxTile = modeDef.Ui == UiSection.FirmwareFx;
        FxExpander.IsExpanded = fxTile;                       // 正用固件特效时保持展开
        AnimatedVisibility.SetExpanded(SolidCard, solid);     // 纯色拾色器仅纯色灯光模式显示
        // §2 折叠头右侧的「当前 XX」胶囊: 收起状态也能一眼看到正在用哪款特效
        FxCurrentPill.Visibility = fxTile ? Visibility.Visible : Visibility.Collapsed;
        if (fxTile) FxCurrentText.Text = Loc.F("Home.Fx.Current", ModeCatalog.Name(cur));
    }

    private void SetMode(int mode)
    {
        if (_updatingMode || !_loaded || ConfigStore.Device.ModeIndex == mode) return;
        ConfigStore.Device.ModeIndex = mode;
        RefreshModeButtons();
        App.Instance.ApplyConfig();             // 模式切换立即生效
    }

    /// <summary>探针入口: 无窗口的无头宿主里 `Loaded` 不触发, 而 OnLoaded 会碰 App.Instance
    /// (探针只 new App() 不 Run, 没有实例)。这里只做**不依赖 App/核心**的界面装配,
    /// 让探针能拿到真实的磁贴结构与指标条空态。</summary>
    public void BuildForProbe()
    {
        CollectModeButtons();
        LoadSolidFromConfig();      // 拾色器初值 (顺带建立 S 轨道渐变 —— 无头宿主里 OnLoaded 不触发)
        _metricsSig = "\0";
        RebuildMetrics(MetricKeysForMode(0, false));   // 未运行 → 一行灰字空态
    }

    /// <summary>探针用: 按给定指标集渲染一次 (无头宿主没有运行中的会话, 只能这样验双层结构)</summary>
    public void RenderMetricsForProbe(string[] keys) => RebuildMetrics(keys);

    /// <summary>探针用: 造一行显示器 (无头宿主枚举不到真显示器) —— 用**真模板**渲染,
    /// 验"编号 + 分辨率 + 适配器真的进了模板"与"不再有形状方块 / 不再有悬浮提示"。</summary>
    public UIElement BuildMonitorRowForProbe(int index, string adapter, int w, int h)
    {
        ACMonitorInfo m = default;
        unsafe
        {
            // m 是局部变量 → 已经固定, 直接索引它的定长缓冲区即可 (再套 fixed 会 CS0213)
            var bytes = System.Text.Encoding.UTF8.GetBytes(adapter);
            int n = Math.Min(bytes.Length, 31);
            for (int i = 0; i < n; i++) m.adapter[i] = bytes[i];
        }
        m.width = w;
        m.height = h;
        m.primary = 1;
        // 用 ContentPresenter 渲染 (与 ItemsControl 同款路径): 模板应用时数据已在, 绑定一次到位。
        // ⚠️ 不用 DataTemplate.LoadContent + 事后赋 DataContext —— 实测那样绑定不刷新 (文本全空)。
        return new ContentPresenter
        {
            Content = MakeMonitorRow(index, m, capturing: true),
            ContentTemplate = (DataTemplate)FindResource("MonitorRowTemplate"),
        };
    }

    // ---- 状态轮询 ----------------------------------------------------
    private unsafe void OnPolled(ACStatus st)
    {
        _lastStatus = st;                      // 辉光计时器在没有活动会话时用它兜底
        WatchDeviceIdentity(st);
        if (st.state != _lastPolledState)
        {
            _lastPolledState = st.state;
            RefreshMonitors();            // 拓扑切换 (Win+P/热插拔) 后信息面板跟随
        }

        int mode = ConfigStore.Device.ModeIndex;

        // v1.6: MCU 特效下禁用亮度滑块 + 改说明 (只在该变时动, 免得 4Hz 轮询里反复赋值)
        // MCU 模式下禁用亮度滑块 + 改说明。禁用**视觉**由本地滑块皮肤提供
        // (Styles/SliderStyle.xaml 的 IsEnabled=False 触发器 —— 上游 4.3.0 的模板没有这一档,
        //  以前只能用整行 Opacity 硬压, 现已撤掉 ✓)
        // 亮度滑块能不能点 + 那一行说明, 都由**亮度语义**决定 (BrightKind)。
        // "不可调"有两种原因 (固件渲染 / 我们还不认识这个模式) → 说明文案要分开写, 别让未知模式顶着
        // "固件特效由灯带渲染"的解释。
        bool brightOk = BrightnessEnabled(mode);
        if (BrightnessRow.IsEnabled != brightOk)
        {
            BrightnessRow.IsEnabled = brightOk;
        }
        var modeDef = ModeCatalog.Of(mode);
        BrightnessRow.Description = modeDef.Bright switch
        {
            BrightKind.Cap => Loc.T("Home.Brightness.Audio"),
            BrightKind.None => Loc.T(modeDef.Frame == FrameKind.Unknown
                                     ? "Home.Brightness.UnknownHint" : "Home.Brightness.McuHint"),
            _ => Loc.T("Home.Brightness.Desc"),
        };

        if (mode != _lastHeroMode)
        {
            _lastHeroMode = mode;
            HeroTitle.Text = ModeCatalog.Name(mode);
            HeroSub.Text = ModeCatalog.Desc(mode);
        }

        // banner 视觉 = f(状态, 模式, 开启失败原因): 任一变化就淡变过去 (首次直接就位)
        long look = ((long)st.state << 32) | (uint)mode;
        string blockLabel = _block?.Label ?? "";
        if (look != _lastBannerLook || blockLabel != _lastBlockLabel)
        {
            bool animate = _lastBannerLook != long.MinValue;
            _lastBannerLook = look;
            _lastBlockLabel = blockLabel;
            ApplyBannerLook(st.state, mode, animate);
        }

        // 按钮 = "有没有会话", 与 OnToggle 的判定同一个口径 → 标签和动作永远一致。
        // 连接中断时会话仍在 (核心每 2s 自动重连), 所以按钮是"关闭": 按下即打断重连, 回到未连接态
        //
        // 关闭后**收尾没完不许点开启**。native 的 StopEx 要 join 会话线程,
        // 而线程可能正卡在握手一个错误端口上 (实测 1~5s) —— 固定延时放开会让"端口还没释放"就能点开启。
        // 口径 = 由轮询统一裁决:
        //   禁用 = 正在处理这次点击 || 启动任务在飞 || 队列里还有没跑完的生命周期调用 (收尾中)
        // 标签也顺便说实话: 收尾期间显示「关闭中…」(否则按钮写着"开启"却是灰的, 像卡死)。
        ToggleBtn.Content = Loc.T(App.Instance.CoreRunning ? "Home.Toggle.Close"
            : App.Instance.LifecycleBusy ? "Home.Toggle.Closing" : "Home.Toggle.Open");
        ToggleBtn.IsEnabled = ToggleEnabled(_toggling, App.Instance.Starting, App.Instance.LifecycleBusy);

        // 端口行与设备行第二行同构 (分段 join, 空段跳过); 供电口径见 PowerText
        PortText.Text = st.state == AmbientCore.StateStopped && !App.Instance.CoreRunning
            ? Loc.T("Home.Port.None")
            : string.Join(" · ", new[]
              {
                  st.ComPort,
                  DeviceTypeCatalog.Name(st.devType),
                  PowerText(st.state, st.ledStatusByte),
                  Loc.F("Dev.LedCount", ConfigStore.Device.LedCount),
              }.Where(s => s.Length > 0));

        BannerFpsText.Text = $"{st.fps:F1} FPS";
        UpdateMetrics(st);
        UpdateGlow(st);
        UpdateRing(st);

        // 未知模式 (表外号) 先说清楚 —— 它**可行动** (切走就好), 而且此时亮度被禁用、指标只剩 wire/leds,
        // 不解释一句用户会以为软件坏了。放在提示条最前面。
        bool modeUnknown = !ModeCatalog.IsKnown(mode);

        var warns = new List<string>();
        if (modeUnknown)
            warns.Add(Loc.F("Home.Warn.UnknownMode", mode));
        if (st.monitorFallback == 1)
            warns.Add(Loc.T("Home.Warn.MonitorFallback"));
        if (st.allBlackFrames > 30)
            warns.Add(Loc.F("Home.Warn.AllBlack", st.allBlackFrames));

        // 提示条优先显示"开启失败原因": 它只在**没有会话**时出现, 与画面告警天然互斥
        bool blocked = _block.HasValue;
        bool showWarn = blocked || warns.Count > 0;
        if (showWarn)
        {
            WarnBar.Title = Loc.T(blocked ? "Home.Warn.Blocked"
                                : modeUnknown ? "Home.Warn.Mode.Title" : "Home.Warn.Title");
            WarnBar.Message = blocked ? _block!.Value.Detail : string.Join("\n", warns);
        }
        WarnBar.IsOpen = showWarn;

        // 诊断只在"确实有问题"的态显示: 已停止/启动中 时核心的 LastDiag 往往是上一次的残留
        // ("device lost: no usable port" 之类), 快速连点开关时会闪出红色报错
        string diag = DiagText.Translate(AmbientCore.LastDiag());
        bool showDiag = !string.IsNullOrEmpty(diag) && st.state is AmbientCore.StateError
                                                                   or AmbientCore.StateDevLost
                                                                   or AmbientCore.StateWaitDisp;
        if (showDiag) DiagBar.Message = diag;
        DiagBar.Severity = st.state == AmbientCore.StateError ? InfoBarSeverity.Error
                                                              : InfoBarSeverity.Warning;
        DiagBar.IsOpen = showDiag;
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>主题刷色 (关闭态用)。5.2 换主题后下次状态变化会重新取到新值</summary>
    private Color ThemeColor(string key) => ThemeColor(key, Colors.White);

    private Color ThemeColor(string key, Color fallback)
        => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    private Brush ThemeBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>总开关按钮此刻能不能点 —— **纯函数**, 探针打真值表。
    /// 关闭后**收尾没完不许点开启**: native 的 `StopEx` 要 join 会话线程,
    /// 而线程可能正卡在握手一个错误端口上 (实测 1~5s) —— 固定延时放开会让"端口还没释放"就能点开启。口径:
    ///   禁用 = 正在处理这次点击 (`toggling`) || 启动任务在飞 (`starting`)
    ///          || 队列里还有没跑完的生命周期调用 (`lifecycleBusy` = 收尾中)
    /// 收尾期间按钮文案显示「关闭中…」, 而不是灰着的"开启" (那看着像卡死)。</summary>
    public static bool ToggleEnabled(bool toggling, bool starting, bool lifecycleBusy)
        => !toggling && !starting && !lifecycleBusy;

    // ---- 总开关 (状态卡与多设备列表同步: 开关后延时刷新, 等会话端口/状态回填) ----
    // 暂停期间整个流程持锁 + 按钮禁用 (禁用现在由轮询统一裁决, 见 UpdateBanner/OnPolled 里那条 ——
    // 收尾没完 (App.LifecycleBusy) 或启动任务在飞时按钮保持灰):
    //   快速连点会连发 Start/Stop, 核心侧留下 "device lost: no usable port" 之类的残留诊断
    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        _toggling = true;                       // 轮询据此立刻禁用按钮
        try
        {
            if (App.Instance.CoreRunning)
            {
                App.Instance.StopCore();
                _block = null;              // 用户主动关闭 → 顺手清掉上一次的失败提示
            }
            else
            {
                // 开启失败**不进入"开启"态**。App 侧对"设备被占用/未应答/不在位"不建
                // 兜底会话 (否则 banner 会亮起一个"未连接"的假开启态, 按钮却写着"开启", 再点变成关闭)。
                // 这里把失败原因取回来交给胶囊 (警示色) + InfoBar; 按钮保持"开启", 再点即重试。
                // 不在这里手动画 banner: 让 250ms 轮询发现 _block 变化后统一渲染, 免得动画跑两遍。
                await App.Instance.StartAllAsync();
                _block = App.Instance.CoreRunning ? null : App.Instance.LastStartProblem;
            }
            await Task.Delay(600);              // 等会话端口/状态回填再刷设备列表
            _ = RefreshDevicesAsync();
            // 关闭场景不用在这里死等 native 收尾: 按钮的禁用/放开由轮询裁决
            // (条件里有 App.LifecycleBusy) → 收尾期间保持「关闭中…」的灰态, 收尾完自动放开。
        }
        finally
        {
            _toggling = false;                  // 之后的禁用/放开交给轮询裁决
        }
    }

    // ---- 亮度 (节流下发: 首次立即生效, 拖动中每 40ms 一次, 收尾补发最终值) ----
    // 轨道点击跳转/跟手拖动由 SliderJump 附加行为接管 (与设置页滑条一致)
    // 拖动中**只热更、不写盘**; 停手 400ms 再落盘一次 (拖动中每次热更都同步
    // File.WriteAllText = 25 次/秒落在 UI 线程上, 拖亮度时的卡顿源之一)。
    private const double BrightnessThrottleMs = 40;
    private const double BrightnessSaveDelayMs = 400;
    private DateTime _lastBrightnessApply = DateTime.MinValue;
    private DispatcherTimer? _brightnessFlushTimer;
    private DispatcherTimer? _brightnessSaveTimer;

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;                   // 构造/初值阶段不触发热更与落盘
        if (BrightnessValueText != null)
            BrightnessValueText.Text = $"{(int)e.NewValue}%";
        var due = _lastBrightnessApply.AddMilliseconds(BrightnessThrottleMs);
        if (DateTime.UtcNow >= due)
        {
            ApplyBrightnessNow();
        }
        else
        {
            if (_brightnessFlushTimer == null)
            {
                _brightnessFlushTimer = new DispatcherTimer();
                _brightnessFlushTimer.Tick += (_, _) => ApplyBrightnessNow();
            }
            _brightnessFlushTimer.Interval = due - DateTime.UtcNow;
            _brightnessFlushTimer.Stop();
            _brightnessFlushTimer.Start();
        }
    }

    private void ApplyBrightnessNow()
    {
        _brightnessFlushTimer?.Stop();
        _lastBrightnessApply = DateTime.UtcNow;
        ConfigStore.Device.Brightness = BrightnessSlider.Value / 100.0;
        App.Instance.ApplyConfigHot();          // 只热更
        ScheduleBrightnessSave();               // 停手 400ms 再落盘
    }

    /// <summary>亮度落盘防抖: 每次改动都重置, 停手后才写一次 device json。</summary>
    private void ScheduleBrightnessSave()
    {
        if (_brightnessSaveTimer == null)
        {
            _brightnessSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(BrightnessSaveDelayMs),
            };
            _brightnessSaveTimer.Tick += (_, _) =>
            {
                _brightnessSaveTimer!.Stop();
                ConfigStore.SaveDevice();
            };
        }
        _brightnessSaveTimer.Stop();
        _brightnessSaveTimer.Start();
    }

}

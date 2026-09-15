// HomePage.xaml.cs — 主页: banner(状态/开关/底边辉光) / 亮度 / 模式 / 拾色器 / 固件特效 /
//                   布局预览 / 指标条 / 设备
// 轮 27 (用户反馈第一轮修复): 辉光改"亮着的灯占比 + 中心到两边 + alpha 叠加"并自带 10Hz 刷新;
//   预设改圆形并覆写悬停; 磁贴内容居中; 指标改双层(去 CPU/去着色); 设备行首次打开竞态;
//   显示器行改 `编号 分辨率 适配器`.
// 轮 26 (M5.1 主页构件重设计, 契约 = docs/M5.1_HOME_REDESIGN_PLAN.md):
//   §2 固件特效 = 图标磁贴网格 + 折叠头「当前 XX」胶囊;
//   §3 纯色拾色器 = H + S 两根滑块 (V 就是上方「亮度」行, 不再另设第三根);
//   §4 banner 底边辉光 = 数值驱动的模糊光带, 窗口不可见时不更新;
//   §5 运行指标 = 指标集随模式换 (MetricKeysForMode 纯函数);
//   §6 设备信息 = 结构化行 (类型图标 + 语义胶囊 + 第二行小字) / 「当前」用 3px 强调条;
//   §7 几何环收进折叠「布局预览」, 中心只放静态几何参数。
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AmbientLight.Controls;
using AmbientLight.Interop;
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
    private bool _updatingMode;                 // 程序化设 IsChecked 时不回环下发
    private bool _hexEditing;                   // HexBox / H,S 滑块的程序化写入期间避免回环
    private readonly Dictionary<int, System.Windows.Controls.Control> _modeBtns = new();
    private readonly List<(string Key, System.Windows.Controls.TextBlock Value)> _metricValues = new();

    // banner 文字/胶囊用**自有画刷**: 只有自己的 SolidColorBrush 才能做 ColorAnimation
    // (主题资源刷子是共享且可能已冻结的)
    private readonly SolidColorBrush _titleBrush = new();
    private readonly SolidColorBrush _subBrush = new();
    private readonly SolidColorBrush _portBrush = new();
    private readonly SolidColorBrush _fpsBrush = new();
    private readonly SolidColorBrush _pillBgBrush = new();
    private readonly SolidColorBrush _pillTextBrush = new();
    private readonly SolidColorBrush _dotBrush = new();

    // ---- §4 底边辉光 (轮 27 重做) ----
    // 形式 = 一条"中心亮、向两边衰减"的模糊光带, 亮度由**一个数值**决定
    // (追光 = 亮着的灯占比 / 拾音 = 音量 / 纯色 = 亮度)。形状与颜色只跟模式有关 → 画刷是静态的,
    // 每次只改 Opacity。这样:
    //   ① 视觉上是 WPF 动画插值出来的, 不受轮询频率限制
    //      (轮 26 用 ledRgb 逐段重建 64 段画刷 4 次/秒 —— 那正是用户报的卡顿来源);
    //   ② 暗处 alpha=0 → 辉光是"叠加上去的", 不会把 banner 压成一条黑带。
    // 自带 10Hz 定时器: 每次只重算一个标量, 比让整个状态轮询提速便宜得多
    // (而且核心写 ledRgb / audioLevel 的频率本来就远高于 4Hz, 提出来的采样是"真"的)。
    private const int GlowTickMs = 100;
    private const int GlowEaseMs = 130;         // 常规跟踪 (跟随数值变化, 要跟得紧)
    private const int GlowBreathPeriodMs = 5000;   // 固件特效的呼吸周期
    private const double GlowBreathMin = 0.3, GlowBreathAmp = 0.8;
    private static readonly double[] GlowBellAlphas =
        [0, 0.05, 0.16, 0.34, 0.58, 1.0, 0.58, 0.34, 0.16, 0.05, 0];
    private DispatcherTimer? _glowTimer;
    private LinearGradientBrush? _glowBrush;    // 常驻画刷: 颜色做动画, 不重建 (否则会闪 + 卡)
    private string _glowBrushKey = "\0";        // 当前画刷对应的模式色系
    private bool _glowApplied;                  // 首次不做动画直接就位
    private DateTime _glowHoldUntil;            // 换模式期间压住常规跟踪, 让 260ms 过渡走完
    private double _glowBreath;                 // 固件特效呼吸相位
    private ACStatus _lastStatus;               // 最近一次状态轮询快照 (无会话时辉光用它兜底)

    public HomePage()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);      // 滚轮滚动由外层滚动器承担
        BuildPresets();
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

        BuildModeGrid();
        var dev = ConfigStore.Device;
        BrightnessSlider.Value = dev.Brightness * 100.0;    // 先设初值 (事件未挂)
        BrightnessValueText.Text = $"{(int)(dev.Brightness * 100)}%";
        BrightnessSlider.ValueChanged += OnBrightnessChanged;
        LoadSolidFromConfig();                              // 拾色器初值 (订阅在其后)
        HueSlider.ValueChanged += OnHsvChanged;
        SatSlider.ValueChanged += OnHsvChanged;
        _loaded = true;

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
        StopGlowTimer();
        _lastPolledState = -1;
    }

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
    private void BuildModeGrid()
    {
        PrimaryModeGrid.Children.Clear();
        FxPanel.Items.Clear();
        _modeBtns.Clear();
        var cardTemplate = (ControlTemplate)FindResource("ModeCardTemplate");
        var tileTemplate = (ControlTemplate)FindResource("FxTileTemplate");
        int primaryIndex = 0;
        foreach (var (index, name, kind) in AmbientCore.Modes)
        {
            if (kind == "MCU")
            {
                // §2 固件特效 = 图标磁贴 (24px 图标 + 名称, 选中 = 强调描边/淡底/对勾)。
                // 图标只表达"是哪一款", 不暗示配色 —— 仓库里没有任何固件配色资料
                // (AmbientCore_API.md 只定义 3 字节命令, 效果由灯带固件自己渲染)。
                var ico = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = FxIcon(index),
                    FontSize = 20,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                // 直接赋值而不是 SetResourceReference(Control.ForegroundProperty): FontIcon 的
                // Foreground 未必就是 Control 那个 DP, 传错 DP 会在运行期抛"DP 不适用于该类型"。
                ico.Foreground = ThemeBrush("TextFillColorPrimaryBrush");                var tileContent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        ico,
                        new System.Windows.Controls.TextBlock
                        {
                            Text = name, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                };
                var tile = new System.Windows.Controls.RadioButton
                {
                    Content = tileContent,
                    GroupName = "FxTiles",
                    Template = tileTemplate,
                    Cursor = Cursors.Hand,
                    Width = 152,
                    Height = 56,
                    Margin = new Thickness(0, 0, 8, 8),
                    ToolTip = $"{name} · 由灯带固件渲染, 主机只下发命令",
                };
                tile.Checked += (_, _) => SetMode(index);
                FxPanel.Items.Add(tile);
                _modeBtns[index] = tile;
                continue;
            }

            // 主模式卡 (Gallery 风): 44px 圆角色块图标 + 标题 + 一句功能说明;
            // 等宽三列, 列位 = 序号×2 (中间列是 8px 间隙)
            // 色块 = 与 banner 光带同色系 (蓝/绿/紫), 便于"看色识模式"
            var (icon, tileColor) = index switch
            {
                0 => (SymbolRegular.WeatherSunny24, Color.FromRgb(0x4C, 0x8D, 0xFF)),
                1 => (SymbolRegular.SoundWaveCircle24, Color.FromRgb(0x2D, 0xC0, 0x8A)),
                _ => (SymbolRegular.Color24, Color.FromRgb(0xA7, 0x8B, 0xFA)),
            };
            var tileIcon = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(8),
                Background = new System.Windows.Media.SolidColorBrush(tileColor),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Child = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = icon,
                    FontSize = 22,
                    // 色块是亮色 → 图标取近黑: 深浅主题下对比都成立
                    Foreground = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x1F)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var descEl = new System.Windows.Controls.TextBlock
            {
                Text = ModeDesc(index),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            };
            descEl.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                        "TextFillColorSecondaryBrush");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(tileIcon);
            content.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new System.Windows.Controls.TextBlock
                    {
                        Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold,
                    },
                    descEl,
                },
            });

            var rb = new System.Windows.Controls.RadioButton
            {
                Content = content,
                GroupName = "PrimaryModes",
                Template = cardTemplate,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Stretch,     // 等宽三列撑满
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 96,
            };
            Grid.SetColumn(rb, primaryIndex * 2);
            primaryIndex++;
            rb.Checked += (_, _) => SetMode(index);
            PrimaryModeGrid.Children.Add(rb);
            _modeBtns[index] = rb;
        }
        RefreshModeButtons();
    }

    /// <summary>固件特效图标 (§2; 全部已核对 WPF-UI 4.3.0 的 SymbolRegular)。
    /// ⚠️ 不要凭记忆加名字 —— 这里写错会编译失败, 那是好事 (XAML 里写错只会运行时闪退)。</summary>
    private static SymbolRegular FxIcon(int mode) => mode switch
    {
        2 => SymbolRegular.ColorFill24,           // 彩虹跳跃
        3 => SymbolRegular.Water24,               // 海浪潮汐
        4 => SymbolRegular.Flash24,               // 电闪雷鸣
        5 => SymbolRegular.Heart24,               // 呼吸心跳
        6 => SymbolRegular.Fire24,                // 火焰燃烧
        7 => SymbolRegular.WeatherSnowflake24,    // 南极光彩
        8 => SymbolRegular.Molecule24,            // 粒子碰撞
        9 => SymbolRegular.Gift24,                // 圣诞节日
        10 => SymbolRegular.Wand24,               // 变幻莫测
        12 => SymbolRegular.Beach24,              // 浓情夏日
        13 => SymbolRegular.WeatherSnow24,        // 北极冰山
        _ => SymbolRegular.Sparkle24,
    };

    private void RefreshModeButtons()
    {
        int cur = ConfigStore.Device.ModeIndex;
        _updatingMode = true;
        foreach (var (index, _, _) in AmbientCore.Modes)
        {
            if (!_modeBtns.TryGetValue(index, out var ctl)) continue;
            if (ctl is System.Windows.Controls.RadioButton rb) rb.IsChecked = index == cur;
        }
        _updatingMode = false;
        bool solid = cur == 11;
        // 纯色拾色器每次展示时回到**折叠**态 (只露 Header: HEX 输入 + 色块预览);
        // 预设色板/H/S 滑块由用户点标题条自行展开 (反馈修复轮 13)
        if (solid) SolidCard.IsExpanded = false;
        FxExpander.IsExpanded = AmbientCore.IsMcuMode(cur);   // 正用固件特效时保持展开
        AnimatedVisibility.SetExpanded(SolidCard, solid);     // 纯色拾色器仅纯色灯光模式显示
        // §2 折叠头右侧的「当前 XX」胶囊: 收起状态也能一眼看到正在用哪款特效
        bool mcu = AmbientCore.IsMcuMode(cur);
        FxCurrentPill.Visibility = mcu ? Visibility.Visible : Visibility.Collapsed;
        if (mcu) FxCurrentText.Text = $"当前 {AmbientCore.ModeName(cur)}";
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
        BuildModeGrid();
        LoadSolidFromConfig();      // 拾色器初值 (顺带建立 S 轨道渐变 —— 无头宿主里 OnLoaded 不触发)
        _metricsSig = "\0";
        RebuildMetrics(MetricKeysForMode(0, false));   // 未运行 → 一行灰字空态
    }

    /// <summary>探针用: 按给定指标集渲染一次 (无头宿主没有运行中的会话, 只能这样验双层结构)</summary>
    public void RenderMetricsForProbe(string[] keys) => RebuildMetrics(keys);

    /// <summary>探针用: 造一行显示器 (无头宿主枚举不到真显示器) —— 验"编号图标 + 分辨率 + 适配器"
    /// 的格式, 以及"不再有形状方块 / 不再有悬浮提示"。</summary>
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
        return BuildMonitorRow(index, m, capturing: true);
    }

    // ---- §3 纯色灯光拾色器: H + S 即完整 HSV (V = 主页「亮度」行; ConfigStore.Brightness 是
    //      颜色链之后真正的线性调光乘数)。拖动时以 (H,S) 为真值源, 只在初值/HEX/预设时反推 ——
    //      否则 8bit 往返会让色相旋钮自己抖 (HEX 每通道只有 8bit, 反推 H 会跳)。 ----
    private static readonly (string Hex, string Name)[] Presets =
    {
        ("#FFE1D6", "暖白"), ("#FFF6E0", "米白"), ("#FFFFFF", "纯白"), ("#C9E4FF", "冷白"),
        ("#FF3B30", "红"),   ("#FF9500", "橙"),   ("#FFCC00", "黄"),   ("#34C759", "绿"),
        ("#5AC8FA", "青"),   ("#007AFF", "蓝"),   ("#AF52DE", "紫"),   ("#FF2D55", "粉"),
    };

    private const int CustomSlotCount = 5;            // 自定义色槽数量 (轮 29)
    private const double CustomColorDelayMs = 1000;    // 颜色停手多久才算"定了"并记入自定义槽
    private readonly List<System.Windows.Controls.Button> _customBtns = [];
    private DispatcherTimer? _customColorTimer;

    private void BuildPresets()
    {
        var tpl = (ControlTemplate)FindResource("PresetSwatchTemplate");
        foreach (var (hex, name) in Presets)
        {
            var btn = new System.Windows.Controls.Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 6),
                // 自绘圆点模板: 默认 Button 模板的悬停会把整个色块铺成灰色 (轮 27 用户反馈)
                Template = tpl,
                BorderThickness = new Thickness(2),
                // 未选中给一圈深灰描边 (而不是透明) —— 否则圆环位置看着像"空出来的缝"
                BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush"),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                ToolTip = $"{name} {hex}",
                Tag = hex,
                Cursor = Cursors.Hand,
            };
            btn.Click += OnPresetClick;
            PresetStrip.Items.Add(btn);
        }

        // 预设与自定义之间一条竖线 (轮 29 用户要求: "预设与自定义用个竖向分割线")
        var divider = new System.Windows.Shapes.Rectangle
        {
            Width = 1,
            Height = 30,
            Margin = new Thickness(6, 0, 10, 6),
            Fill = ThemeBrush("ControlStrokeColorDefaultBrush"),
        };
        PresetStrip.Items.Add(divider);

        // 自定义色槽: 自动记住调出来的颜色 (最新在前, 最多 CustomSlotCount 个); 右键清空单个槽
        for (int i = 0; i < CustomSlotCount; i++)
        {
            var btn = new System.Windows.Controls.Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 6, 6),
                Template = tpl,
                BorderThickness = new Thickness(2),
                BorderBrush = ThemeBrush("ControlStrokeColorDefaultBrush"),
                Background = Brushes.Transparent,
                Tag = null,
                Cursor = Cursors.Hand,
            };
            btn.Click += OnPresetClick;                 // 同一套逻辑: Tag 为空时什么也不做
            btn.MouseRightButtonUp += OnCustomSlotClear;
            _customBtns.Add(btn);
            PresetStrip.Items.Add(btn);
        }
        RefreshCustomSlots();
    }

    /// <summary>把记忆里的自定义色刷到 5 个槽上 (空槽画成一个空心圆: 模板里 BorderBrush 有、Fill 没有)。
    /// 槽与颜色列表按顺序对应, 不做左右补位 —— 清除中间一个会整体前移, 简单可预期。</summary>
    private void RefreshCustomSlots()
    {
        var colors = ConfigStore.App.CustomColors;
        for (int i = 0; i < _customBtns.Count; i++)
        {
            var btn = _customBtns[i];
            bool has = i < colors.Count && !string.IsNullOrWhiteSpace(colors[i]);
            string? hex = has ? colors[i] : null;
            btn.Tag = hex;
            btn.Background = has
                ? new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex!))
                : Brushes.Transparent;
            btn.BorderBrush = ThemeBrush(has ? "ControlStrokeColorDefaultBrush" : "TextFillColorTertiaryBrush");
            btn.ToolTip = has ? $"{hex} (右键清除)" : "自定义色槽 · 调出颜色后自动记住";
        }
        UpdatePresetSelection();
    }

    /// <summary>记住一个自定义色 (非预设、去重、最新在前、最多 5 个)。
    /// 由"颜色停手 600ms"的防抖计时器触发 —— 拖 H/S 滑条时会连续产生几十个近似色,
    /// 不防抖的话 5 个槽一秒就被刷满了。</summary>
    private void RememberCustomColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return;
        if (Presets.Any(p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase))) return;
        var list = ConfigStore.App.CustomColors;
        list.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        while (list.Count > CustomSlotCount) list.RemoveAt(list.Count - 1);
        ConfigStore.SaveApp();
        RefreshCustomSlots();
    }

    private void OnCustomSlotClear(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string hex }) return;
        ConfigStore.App.CustomColors.RemoveAll(c => string.Equals(c, hex, StringComparison.OrdinalIgnoreCase));
        ConfigStore.SaveApp();
        RefreshCustomSlots();
        e.Handled = true;
    }

    private void LoadSolidFromConfig()
    {
        var (r, g, b) = ParseHex(ConfigStore.Device.SolidColor, (255, 225, 214));
        SetHsvFromRgb(r, g, b);
        _hexEditing = true;
        HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
        _hexEditing = false;
        RefreshSolidSwatches();
    }

    /// <summary>把 RGB 反推成 H/S 写进滑块 (程序化写入 → 加 _hexEditing 抑制回环)。
    /// 颜色链侧看到的 V 恒为 1, 实际亮度由「亮度」行决定。</summary>
    private void SetHsvFromRgb(int r, int g, int b)
    {
        var (h, s, _) = RgbToHsv(r, g, b);
        _hexEditing = true;
        HueSlider.Value = h;
        SatSlider.Value = s * 100.0;
        _hexEditing = false;
        HValueText.Text = $"{(int)Math.Round(h)}°";
        SValueText.Text = $"{(int)Math.Round(s * 100)}%";
        RefreshHsvTracks();
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string hex }) return;
        var (r, g, b) = ParseHex(hex, (255, 225, 214));
        SetHsvFromRgb(r, g, b);
        ApplySolid(r, g, b);
    }

    private void OnHsvChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (HValueText == null || SValueText == null || SatTrackBar == null) return;  // 解析期防护
        HValueText.Text = $"{(int)Math.Round(HueSlider.Value)}°";
        SValueText.Text = $"{(int)Math.Round(SatSlider.Value)}%";
        RefreshHsvTracks();
        if (_hexEditing || !_loaded) return;
        var (r, g, b) = HsvToRgb(HueSlider.Value, SatSlider.Value / 100.0, 1.0);
        ApplySolid(r, g, b);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_hexEditing || !_loaded) return;
        var (r, g, b) = ParseHex(HexBox.Text, (-1, -1, -1));
        if (r < 0) return;                       // 输入未完成时忽略
        SetHsvFromRgb(r, g, b);
        ApplySolid(r, g, b);
    }

    /// <summary>S 轨道 = 白 → 当前 H 的全饱和色 (HSV 里 S=0 且 V=1 就是白); H 变化时重画。
    /// 色相轨道是静态彩虹 (XAML 资源 HueRainbowBrush)。</summary>
    private void RefreshHsvTracks()
    {
        double h = HueSlider?.Value ?? 0;
        var (fr, fg, fb) = HsvToRgb(h, 1.0, 1.0);
        SatTrackBar.Background = new LinearGradientBrush(
            Colors.White, Color.FromRgb((byte)fr, (byte)fg, (byte)fb), 0);
        SatSwatch.Fill = Frozen(Color.FromRgb((byte)fr, (byte)fg, (byte)fb));
    }

    private void ApplySolid(int r, int g, int b)
    {
        ConfigStore.Device.SolidColor = $"#{r:X2}{g:X2}{b:X2}";
        _hexEditing = true;
        HexBox.Text = ConfigStore.Device.SolidColor;
        _hexEditing = false;
        RefreshSolidSwatches();
        App.Instance.ApplyConfigDebounced();
        ScheduleCustomColorRemember();
    }

    /// <summary>颜色停手 600ms 后记入自定义槽 (拖动中不记 —— 否则一秒钟刷满 5 个槽)。</summary>
    private void ScheduleCustomColorRemember()
    {
        if (_customColorTimer == null)
        {
            _customColorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(CustomColorDelayMs),
            };
            _customColorTimer.Tick += (_, _) =>
            {
                _customColorTimer!.Stop();
                RememberCustomColor(ConfigStore.Device.SolidColor);
            };
        }
        _customColorTimer.Stop();
        _customColorTimer.Start();
    }

    /// <summary>Header 的色块 (生效色的对比行已按轮 29 用户要求去掉)。</summary>
    private void RefreshSolidSwatches()
    {
        var (r, g, b) = ParseHex(ConfigStore.Device.SolidColor, (255, 225, 214));
        SolidPreview.Background = Frozen(Color.FromRgb((byte)r, (byte)g, (byte)b));
        UpdatePresetSelection();
    }

    private void UpdatePresetSelection()
    {
        string cur = ConfigStore.Device.SolidColor;
        foreach (var b in PresetStrip.Items.OfType<System.Windows.Controls.Button>())
        {
            bool sel = string.Equals((string?)b.Tag, cur, StringComparison.OrdinalIgnoreCase);
            b.BorderBrush = sel ? ThemeBrush("AccentFillColorDefaultBrush")
                                : ThemeBrush("ControlStrokeColorDefaultBrush");
        }
    }

    private static (int r, int g, int b) ParseHex(string? hex, (int r, int g, int b) fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);   // #AARRGGBB → RGB
        if (s.Length != 6) return fallback;
        try
        {
            return (int.Parse(s[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(s.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(s[4..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { return fallback; }
    }

    /// <summary>RGB(0-255) → HSV (h 0-360, s/v 0-1)</summary>
    public static (double h, double s, double v) RgbToHsv(int r, int g, int b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double mx = Math.Max(rd, Math.Max(gd, bd)), mn = Math.Min(rd, Math.Min(gd, bd));
        double d = mx - mn, h = 0;
        if (d > 0)
        {
            if (mx == rd) h = 60 * (((gd - bd) / d) % 6);
            else if (mx == gd) h = 60 * ((bd - rd) / d + 2);
            else h = 60 * ((rd - gd) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, mx <= 0 ? 0 : d / mx, mx);
    }

    /// <summary>HSV → RGB(0-255)</summary>
    public static (int r, int g, int b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255),
                (int)Math.Round((b + m) * 255));
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
        if (mode != _lastHeroMode)
        {
            _lastHeroMode = mode;
            HeroTitle.Text = AmbientCore.ModeName(mode);
            HeroSub.Text = ModeDesc(mode);
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
        // 连接中断时会话仍在 (核心每 2s 自动重连), 所以按钮是"关闭": 按下即打断重连, 回到未连接态 (轮 19)
        ToggleBtn.Content = App.Instance.CoreRunning ? "关闭" : "开启";

        PortText.Text = st.state == AmbientCore.StateStopped && !App.Instance.CoreRunning
            ? "未连接设备"
            : $"端口 {st.ComPort} · {DevTypeName(st.devType)} · 供电 {((st.ledStatusByte & 1) == 1 ? "外接" : "USB")} · {ConfigStore.Device.LedCount} 灯";

        BannerFpsText.Text = $"{st.fps:F1} FPS";
        UpdateMetrics(st);
        UpdateGlow(st);
        UpdateRing(st, st.margins[0], st.margins[1], st.margins[2], st.margins[3]);

        var warns = new List<string>();
        if (st.monitorFallback == 1)
            warns.Add($"配置的显示器不可用, 已回退主屏 (margin {{底,右,顶,左}} = {st.margins[0]},{st.margins[1]},{st.margins[2]},{st.margins[3]})");
        if (st.allBlackFrames > 30)
            warns.Add($"连续 {st.allBlackFrames} 帧全黑 (独占全屏或 MPO?), 画面可能不准");

        // 提示条优先显示"开启失败原因"(轮 18 ③): 它只在**没有会话**时出现, 与画面告警天然互斥
        bool blocked = _block.HasValue;
        bool showWarn = blocked || warns.Count > 0;
        if (showWarn)
        {
            WarnBar.Title = blocked ? "无法开启灯带" : "画面提示";
            WarnBar.Message = blocked ? _block!.Value.Detail : string.Join("\n", warns);
        }
        WarnBar.IsOpen = showWarn;

        // 诊断只在"确实有问题"的态显示: 已停止/启动中 时核心的 LastDiag 往往是上一次的残留
        // ("device lost: no usable port" 之类), 快速连点开关时会闪出红色报错 (轮 16 用户反馈)
        string diag = AmbientCore.LastDiag();
        bool showDiag = !string.IsNullOrEmpty(diag) && st.state is AmbientCore.StateError
                                                                   or AmbientCore.StateDevLost
                                                                   or AmbientCore.StateWaitDisp;
        if (showDiag) DiagBar.Message = diag;
        DiagBar.Severity = st.state == AmbientCore.StateError ? InfoBarSeverity.Error
                                                              : InfoBarSeverity.Warning;
        DiagBar.IsOpen = showDiag;
    }

    // ---- §4 banner 底边辉光 ------------------------------------------
    // 数据零新增, 渲染 = 1 个 Border + 1 个 BlurEffect (GPU); 只重绘 banner 底边约 600×50 的区域。
    // 净开销比轮 26 更低: 不再每帧重建画刷, 也不再 4Hz 重绘 60 个 Ellipse (几何环已收进折叠)。
    private void StartGlowTimer()
    {
        if (_glowTimer == null)
        {
            _glowTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(GlowTickMs),
            };
            _glowTimer.Tick += OnGlowTick;
        }
        _glowTimer.Start();
    }

    private void StopGlowTimer() => _glowTimer?.Stop();

    private void OnGlowTick(object? sender, EventArgs e)
    {
        // 先做便宜判断: 开关关掉 / 窗口不可见时连 P/Invoke 都不发 (硬规则: 不看就不更新)
        if (!ConfigStore.App.BannerGlow) { UpdateGlow(_lastStatus); return; }
        if (!IsUiVisible()) return;
        int slot = App.Instance.SlotOf(ConfigStore.SelectedKey);
        UpdateGlow(slot >= 0 ? AmbientCore.GetStatusEx(slot) : _lastStatus);
    }

    private void UpdateGlow(ACStatus st)
    {
        // 实验开关 (高级页「界面」组): 关掉 = 退回纯渐变 banner, 不影响其它任何功能
        if (!ConfigStore.App.BannerGlow)
        {
            if (GlowBand.Visibility != Visibility.Collapsed) GlowBand.Visibility = Visibility.Collapsed;
            GlowBand.BeginAnimation(UIElement.OpacityProperty, null);
            GlowBand.Opacity = 0;
            return;
        }
        if (GlowBand.Visibility != Visibility.Visible) GlowBand.Visibility = Visibility.Visible;
        if (!IsUiVisible()) return;             // 硬规则: 窗口不可见 → 完全不更新

        var dev = ConfigStore.Device;
        int mode = dev.ModeIndex;

        EnsureGlowBrush();
        // 颜色/形状只跟模式有关 (追光蓝 / 拾音绿 / 纯色紫 / 固件白灰)。
        // 换模式时**只动颜色**并用与 banner 渐变同一个时长 (BannerFadeMs) 过渡 ——
        // 上一版是直接换画刷 (瞬变), 所以看起来比渐变"快了一点" (轮 28 用户反馈)。
        string key = ModeColorKey(mode);
        bool modeChanged = key != _glowBrushKey;
        if (modeChanged)
        {
            _glowBrushKey = key;
            AnimateGlowColor(ModeGlowColor(key), _glowApplied);
        }

        double level = mode switch
        {
            0 => TraceLitLevel(st, dev.LedCount),      // 追光: 亮着的灯占比
            1 => st.audioLevel / 255.0,                // 拾音: 音量
            11 => Math.Clamp(dev.Brightness, 0, 1),    // 纯色: 亮度
            _ => BreathLevel(),                        // 固件特效: 呼吸 (不受亮度控制)
        };
        AnimateGlow(level, modeChanged);
        _glowApplied = true;
    }

    /// <summary>固件特效的辉光: 中性微光 + **呼吸亮暗** (轮 28 用户要求)。周期 2.6s 的余弦,
    /// 起落平滑; 与亮度滑块无关 —— 核心此时只发命令, "亮度"语义不适用。</summary>
    private double BreathLevel()
    {
        _glowBreath += (double)GlowTickMs / GlowBreathPeriodMs;
        if (_glowBreath >= 1.0) _glowBreath -= 1.0;
        return GlowBreathMin + GlowBreathAmp * (0.5 - 0.5 * Math.Cos(2 * Math.PI * _glowBreath));
    }

    /// <summary>追光的辉光数值 = 灯带"亮着的灯占比"/亮度水平, 归一到 [0,1]。
    /// **两种算法二选一** (轮 28 用户要求把另一种留在代码里, 方便在 VS 里注释切换对比最终方案):
    ///   A (现行) 相对亮度加权 `0.2126R + 0.7152G + 0.0722B` —— 符合人眼感受,
    ///            纯蓝画面不会被高估 (蓝色的 max 通道很高, 但看起来并不亮);
    ///   B (备选) HSV 的 V = `max(R,G,B)` —— 灯珠"非全亮即全灭"时它正好等于点亮比例, 但对蓝偏高。
    /// 用 `ledRgb`(最终下发值) 而不是"调光前的值"是**等价**的: 亮度自 PROJECT_PLAN §8.1 起就是颜色链
    /// **最后一次**线性缩放 → mean(v·k) = mean(v)·k, 而亮度滑块本来就要控制辉光透明度。</summary>
    private static double TraceLitLevel(ACStatus st, int ledCount)
    {
        int n = Math.Clamp(ledCount, 1, AmbientCore.MaxLeds);
        var rgb = st.CopyLedRgb();
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            int r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
            // ---- 算法 A (现行) ----
            //sum += 0.2126 * r + 0.7152 * g + 0.0722 * b;
            // ---- 算法 B (备选: 想换就把 A 注释掉、把这行放开) ----
            sum += Math.Max(r, Math.Max(g, b));
        }
        return sum / (n * 255.0);
    }

    /// <summary>辉光基色 = 与 banner 光带同一套色系 (轮 27 用户定稿: 不再逐灯上色)</summary>
    private static Color ModeGlowColor(string key) => key switch
    {
        "Blue" => Rgb(0x4C, 0x8D, 0xFF),
        "Green" => Rgb(0x2D, 0xC0, 0x8A),
        "Purple" => Rgb(0xA7, 0x8B, 0xFA),
        _ => Rgb(0xE4, 0xEB, 0xF2),
    };

    /// <summary>"中心亮 → 两边衰减"的光带: 颜色固定, 亮度全压在 **alpha** 上 ——
    /// 暗处 alpha = 0, 所以辉光是叠加上去的, 不会把 banner 压成黑带。
    /// 画刷**只建一次**, 之后只对 stop 的颜色做动画 (换模式时颜色平滑过渡)。</summary>
    private void EnsureGlowBrush()
    {
        if (_glowBrush != null) return;
        var c = ModeGlowColor("Blue");
        _glowBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < GlowBellAlphas.Length; i++)
        {
            byte a = (byte)Math.Round(GlowBellAlphas[i] * 255);
            _glowBrush.GradientStops.Add(new GradientStop(
                Color.FromArgb(a, c.R, c.G, c.B), (double)i / (GlowBellAlphas.Length - 1)));
        }
        GlowBand.Background = _glowBrush;
    }

    /// <summary>换模式时的辉光换色: 逐 stop 做 ColorAnimation (只动 RGB, alpha 不动 ——
    /// 亮度必须始终留在 alpha 上), 时长与 banner 光带交叉淡化一致 → 两者同时变化。</summary>
    private void AnimateGlowColor(Color to, bool animate)
    {
        if (_glowBrush == null) return;
        foreach (var stop in _glowBrush.GradientStops)
        {
            Color from = stop.Color;                                  // 当前生效值 (可能正被动画驱动)
            var target = Color.FromArgb(from.A, to.R, to.G, to.B);
            stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.Color = target;                                      // 基准值先写成终值
            if (!animate || from == target) continue;
            var anim = new ColorAnimation(from, target, TimeSpan.FromMilliseconds(BannerFadeMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            anim.Completed += (_, _) => stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.BeginAnimation(GradientStop.ColorProperty, anim);
        }
    }

    /// <summary>辉光亮度 = 整层不透明度。**不给 From** —— WPF 以"当前生效值"为起点,
    /// 所以两次动画之间不会跳 (这正是它比"每次赋一个新值"平滑的原因)。
    /// 换模式那一次用与渐变同样的 260ms, 并且在此后的一小段时间里压住常规跟踪,
    /// 否则 10Hz 的下一拍会用 130ms 把它"追短", 看起来还是比渐变快。</summary>
    private void AnimateGlow(double level, bool modeChanged = false)
    {
        level = Math.Clamp(level, 0, 1);
        if (modeChanged)
        {
            _glowHoldUntil = DateTime.UtcNow.AddMilliseconds(BannerFadeMs);
        }
        else if (DateTime.UtcNow < _glowHoldUntil)
        {
            return;                             // 换模式的过渡还没走完, 别打断它
        }
        int ms = modeChanged ? BannerFadeMs : GlowEaseMs;
        var anim = new DoubleAnimation(level, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        GlowBand.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // ---- §5 运行指标 (轮 27 用户定稿: 双层 —— 标签在上 / 数值在下) ----
    /// <summary>指标集随模式换。**纯函数** —— 探针用它打真值表: 这类"写错不报错、只会静默显示错指标"
    /// 的规则最该被回归网住。running=false → 空集 (显示一行灰字)。
    /// **不含 CPU**: `ACStatus.cpuPercent` 从 M1 起就标着"预留", native 侧从来没赋过值 (恒 0.0%);
    /// 为了一个没人会照着行动的数字去核心加采样线程, 不值得 → 拿掉。</summary>
    public static string[] MetricKeysForMode(int mode, bool running)
    {
        if (!running) return [];
        if (mode == 0) return ["proc", "wire", "fps"];
        if (mode == 1) return ["audio", "wire"];
        if (mode == 11) return ["wire", "leds", "bright"];
        if (AmbientCore.IsMcuMode(mode)) return ["cmd", "wire", "leds"];
        return ["wire"];
    }

    /// <summary>指标标签 (5.3 i18n 文案源)</summary>
    public static string MetricLabel(string key) => key switch
    {
        "proc" => "处理",
        "wire" => "串口写入",
        // 原叫"帧率"/「变化/s」: 它的物理含义是屏幕变化速率, 叫帧率会让人以为掉到 30 就是坏了
        "fps" => "画面变化",
        "audio" => "音量",
        "leds" => "灯珠",
        "bright" => "亮度",
        "cmd" => "命令",
        _ => key,
    };

    private void UpdateMetrics(ACStatus st)
    {
        var keys = MetricKeysForMode(ConfigStore.Device.ModeIndex, App.Instance.CoreRunning);
        // 灯带没开 → 整块高度动画收掉 (与拾音页「输入/输出设备」同一套机制), 不留一行占位
        AnimatedVisibility.SetExpanded(MetricsCard, keys.Length > 0);
        string sig = string.Join(",", keys);
        if (sig != _metricsSig)
        {
            _metricsSig = sig;
            RebuildMetrics(keys);
        }
        if (keys.Length == 0) return;

        var dev = ConfigStore.Device;
        foreach (var (key, el) in _metricValues)
        {
            // 不适用 / 取不到一律显示 "—" (0 会被读成"卡住了", 空着又像没填)
            el.Text = key switch
            {
                "proc" => st.state == AmbientCore.StateRunning ? $"{st.procMs:F2} ms" : "—",
                "wire" => st.state == AmbientCore.StateRunning ? $"{st.wireMs:F2} ms" : "—",
                "fps" => st.state == AmbientCore.StateRunning ? $"{st.fps:F1}/s" : "—",
                "audio" => $"{st.audioLevel}/255",
                "leds" => $"{dev.LedCount}",
                "bright" => $"{(int)Math.Round(dev.Brightness * 100)}%",
                "cmd" => st.fps > 0 ? $"{st.fps:F1}/s" : "—",
                _ => "—",
            };
        }
        // 不做健康着色 (轮 27 用户定稿): 处理/串口耗时没有公认的绝对标准, 涂绿涂红只会误导
    }

    /// <summary>双层等宽分栏: 标签在上 (11px 次要色) / 数值在下 (16px), 都居中。
    /// 初值一律 "—" 由 UpdateMetrics 填 —— 这样"取不到"与"还没填"长得一样, 不会一闪一闪。</summary>
    private void RebuildMetrics(string[] keys)
    {
        MetricsGrid.Children.Clear();
        MetricsGrid.ColumnDefinitions.Clear();
        _metricValues.Clear();
        if (keys.Length == 0)
        {
            // 未启动 → 一行灰字 (不是四个 0.0: 0 会被读成"卡住了")
            MetricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var gray = new System.Windows.Controls.TextBlock
            {
                Text = "未开启 · 指标在灯带运行后显示",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            gray.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                      "TextFillColorTertiaryBrush");
            MetricsGrid.Children.Add(gray);
            return;
        }
        for (int i = 0; i < keys.Length; i++)
        {
            MetricsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new System.Windows.Controls.TextBlock
            {
                Text = MetricLabel(keys[i]),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                       "TextFillColorTertiaryBrush");
            var val = new System.Windows.Controls.TextBlock
            {
                Text = "—",
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            val.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                     "TextFillColorPrimaryBrush");
            var cell = new StackPanel();
            cell.Children.Add(label);
            cell.Children.Add(val);
            Grid.SetColumn(cell, i);
            MetricsGrid.Children.Add(cell);
            _metricValues.Add((keys[i], val));
        }
    }

    // ---- banner 视觉 (轮 16): 点亮 = 模式色渐变 + 白字, 未点亮 = 普通卡片灰 + 主题字色;
    //      换模式用双光带交叉淡化, 点亮/熄灭用整层淡入淡出。
    //      轮 19 (用户定稿口径): **渐变 = "确实驱动上了灯带"** —— 只有 Running/WaitDisp 点亮;
    //      Starting (还在探测/握手) 与 DEV_LOST/ERROR 都不点亮, 胶囊与按钮另行表达"会话还在" ----
    private const int BannerFadeMs = 260;

    /// <summary>渐变是否点亮 = 核心确实在驱动灯带。
    /// - `Starting` 不算: 还在探测/握手, 灯带没被驱动。若算上, 握手失败时会"淡入又淡出"闪一下
    ///   (LightsLayer 淡变 260ms 比握手还长); 不算则任何路径都不会闪。
    /// - `DevLost` / `Error` 不算: 灯带拔出/端口被占/不可恢复错误, 灯带是灭的或不受控的,
    ///   亮着渐变会让人误以为已经驱动成功 (轮 19 用户反馈)。</summary>
    private static bool IsLit(int state) => state is AmbientCore.StateRunning
                                                       or AmbientCore.StateWaitDisp;

    private void ApplyBannerLook(int state, int mode, bool animate)
    {
        bool lit = IsLit(state);
        var (label, dot, pillText) = PillLook(state);
        StatePillText.Text = label;
        SetColor(_dotBrush, dot, animate);
        SetColor(_pillTextBrush, pillText, animate);

        if (lit)
        {
            SetColor(_titleBrush, Colors.White, animate);
            SetColor(_subBrush, Rgb(0xCF, 0xE3, 0xF2), animate);
            SetColor(_portBrush, Rgb(0xA9, 0xC0, 0xCE), animate);
            SetColor(_fpsBrush, Rgb(0x9F, 0xB4, 0xC2), animate);
            // 胶囊底保持中性半透明白: 状态色只落在圆点/文字上, 免得与模式色打架
            SetColor(_pillBgBrush, Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), animate);
            CrossFadeStreak(mode, animate);
        }
        else
        {
            // 未点亮取主题刷色 → 5.2 浅色主题下自动跟随 (下次状态变化时重取)
            SetColor(_titleBrush, ThemeColor("TextFillColorPrimaryBrush"), animate);
            SetColor(_subBrush, ThemeColor("TextFillColorSecondaryBrush"), animate);
            SetColor(_portBrush, ThemeColor("TextFillColorTertiaryBrush"), animate);
            SetColor(_fpsBrush, ThemeColor("TextFillColorTertiaryBrush"), animate);
            SetColor(_pillBgBrush, ThemeColor("ControlFillColorSecondaryBrush"), animate);
        }
        FadeTo(LightsLayer, lit ? 1 : 0, animate);
    }

    /// <summary>胶囊的文案与配色。未点亮时还分三种情况, 都要"看得出问题但不算开启":
    /// ① 开启失败 (无会话) → 警示色 + 失败原因 (轮 18 ③);
    /// ② 有会话但没在驱动灯带 (DEV_LOST=正在重连 / ERROR) → 用该状态自己的颜色;
    /// ③ 真的停了 → 主题次色 + "已停止"。</summary>
    private (string Label, Color Dot, Color Text) PillLook(int state)
    {
        if (!IsLit(state))
        {
            if (_block is { } b) return (b.Label, CautionColor(), CautionColor());
            if (state == AmbientCore.StateStopped)
                return ("已停止",
                        ThemeColor("TextFillColorTertiaryBrush", Rgb(0x9A, 0x9A, 0x9A)),
                        ThemeColor("TextFillColorSecondaryBrush", Rgb(0xC9, 0xC9, 0xC9)));
        }
        var (dot, text) = StateColors(state);
        return (StateLabel(state), dot, text);
    }

    private Color CautionColor()
        => ThemeColor("SystemFillColorCautionBrush", Rgb(0xF0, 0xA0, 0x2E));

    private void CrossFadeStreak(int mode, bool animate)
    {
        string key = ModeColorKey(mode);
        if (key == _lastStreakKey) return;      // 色系没变 (如关→开) 不用换, 交给整层淡变
        _lastStreakKey = key;
        var incoming = _streakOnA ? StreakB : StreakA;
        var outgoing = _streakOnA ? StreakA : StreakB;
        incoming.Fill = (Brush)FindResource($"BannerStreak{key}Brush");
        if (animate)
        {
            FadeTo(incoming, 1, true);
            FadeTo(outgoing, 0, true);
        }
        else
        {
            incoming.Opacity = 1;
            outgoing.Opacity = 0;
        }
        _streakOnA = !_streakOnA;
    }

    /// <summary>颜色过渡。基准值先写成终值 —— 动画结束后撤销动画即可回落于终值
    /// (与 AnimatedVisibility 同一个 HoldEnd 坑)</summary>
    private static void SetColor(SolidColorBrush brush, Color to, bool animate)
    {
        Color from = brush.Color;               // 当前生效值 (可能正被上一次动画驱动)
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = to;
        if (!animate || from == to) return;
        var anim = new ColorAnimation(from, to, TimeSpan.FromMilliseconds(BannerFadeMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private static void FadeTo(UIElement el, double to, bool animate)
    {
        double from = el.Opacity;
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = to;
        if (!animate || Math.Abs(from - to) < 0.001) return;
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(BannerFadeMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) => el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, anim);
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

    /// <summary>状态 → 胶囊文案。DEV_LOST 用"正在尝试重连"而不是"设备未连接":
    /// 核心此时每 2s 重试且会话还在, 说"关闭即打断重连"才讲得通 (轮 19)</summary>
    private static string StateLabel(int state) => state switch
    {
        AmbientCore.StateRunning => "运行中",
        AmbientCore.StateStarting => "启动中",
        AmbientCore.StateWaitDisp => "等待画面",
        AmbientCore.StateDevLost => "正在尝试重连",
        AmbientCore.StateError => "错误",
        _ => "已停止",
    };

    /// <summary>状态 → 胶囊圆点/文字色 (仅在点亮时用)</summary>
    private static (Color Dot, Color Text) StateColors(int state) => state switch
    {
        AmbientCore.StateRunning => (Rgb(0x2D, 0xC0, 0x8A), Rgb(0x8C, 0xE8, 0xC2)),
        AmbientCore.StateStarting => (Rgb(0x4C, 0x8D, 0xFF), Rgb(0xA9, 0xD8, 0xFF)),
        AmbientCore.StateWaitDisp => (Rgb(0xF0, 0xA0, 0x2E), Rgb(0xF6, 0xCE, 0x92)),
        AmbientCore.StateDevLost => (Rgb(0xF0, 0xA0, 0x2E), Rgb(0xF6, 0xCE, 0x92)),
        AmbientCore.StateError => (Rgb(0xE2, 0x4B, 0x4A), Rgb(0xF2, 0xA9, 0xA9)),
        _ => (Rgb(0x9A, 0x9A, 0x9A), Rgb(0xC9, 0xC9, 0xC9)),
    };

    /// <summary>模式 → 光带色系 (追光=蓝 / 拾音=绿 / 纯色=紫 / 固件特效=中性白灰)。
    /// MCU 模式不参与取色也不律动, 故用白灰而不是琥珀 (琥珀留给"等待/警告"的状态色)</summary>
    private static string ModeColorKey(int mode) => mode switch
    {
        0 => "Blue",
        1 => "Green",
        11 => "Purple",
        _ => "Mono",
    };

    /// <summary>模式一句话说明 (banner 副标题 / 模式卡副标题; 5.3 i18n 文案源)</summary>
    private static string ModeDesc(int mode) => mode switch
    {
        0 => "跟随屏幕边缘取色, 实时同步画面",
        1 => "跟随系统声音或麦克风律动",
        11 => "整条灯带单色, 无需画面",
        _ => "固件端特效, 主机只下发命令",
    };

    // ---- §7 布局预览 (折叠) -----------------------------------------
    private void UpdateRing(ACStatus st, int m0, int m1, int m2, int m3)
    {
        var dev = ConfigStore.Device;
        long sig = dev.ModeIndex | ((long)dev.LedCount << 5) | ((long)dev.Sides << 15)
                | ((long)dev.Direction << 18) | ((long)st.state << 20)
                | (((long)dev.LedRotate + 256) << 24);
        if (sig != _lastRingSig)
        {
            _lastRingSig = sig;
            var (mw, mh) = GetMonitorSize(dev.MonitorIndex);   // 预览按真实长宽比布局
            Ring.Configure(dev.LedCount, dev.Sides, dev.Direction, dev.LedRotate, mw, mh);
            LayoutSummary.Text = $"{dev.LedCount} 灯 · {dev.Sides} 边";
        }
        // 中心区只放**静态几何参数**: 展开这个折叠的人目的是核对灯序/边数/偏移, 要的就是几何事实。
        // "黑边裁切" = 核心按黑边自适应在各边剪掉的像素数 (底/右/顶/左, 厂商初始 5);
        // 关掉黑边自适应时它不参与布局, 所以直接写"关闭"而不是给一个会被误读的数字。
        string inset = ConfigStore.Device.BlackBars
            ? $"黑边裁切 {m0}/{m1}/{m2}/{m3}"
            : "黑边裁切 关闭";
        string center = $"{dev.LedCount} 灯 · {dev.Sides} 边 · {inset} · " +
                        $"起点偏移 {dev.LedRotate:+0;-0;0}";
        if (center != _lastCenter)
        {
            _lastCenter = center;
            Ring.SetHint(center);
        }

        // 折叠时不重绘 (几何环收进折叠后, 原来 4Hz 重绘 60 个 Ellipse 的开销消失); 窗口不可见同理
        if (!LayoutCard.IsExpanded || !IsUiVisible()) return;
        if (st.state == AmbientCore.StateRunning && AmbientCore.IsHostFrameMode(dev.ModeIndex))
        {
            Ring.Update(st.CopyLedRgb(), dev.LedCount);
        }
        else if (st.state is AmbientCore.StateRunning or AmbientCore.StateStopped)
        {
            // 拾音/固件特效没有逐灯颜色, 停着也没有 → 清空, 不能留着上一次的画面 (会误导)
            Ring.Clear();
        }
        // WAIT_DISP/DEV_LOST: 保留最后画面 (串口策略帧/重连语义与之一致)
    }

    /// <summary>选中显示器的实际物理分辨率 (预览长宽比用); 枚举失败按 16:9</summary>
    private static (int w, int h) GetMonitorSize(int index)
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            if (mons.Length > 0)
            {
                int i = Math.Clamp(index, 0, mons.Length - 1);
                if (mons[i].width > 0 && mons[i].height > 0)
                    return (mons[i].width, mons[i].height);
            }
        }
        catch { }
        return (16, 9);
    }

    // ---- 模式/设备图标与名称 ------------------------------------------
    /// <summary>显示器编号徽标 (轮 28): 自绘小圆圈 + 数字。
    /// UI 库里确实有 `NumberCircle1..9`, 但实测 **2 号渲染不出数字**
    /// (枚举里搜得到这些名字, 字形却对不上 —— 又是"二进制串里搜得到 ≠ 能用"那条坑),
    /// 与其赌字体某个序号, 不如自己画: 任意序号都对, 还跟着主题走。
    /// 轮 29: 改成**实心强调色 + 反白数字**, 不描边。</summary>
    private static UIElement MonitorBadge(int index)
    {
        var text = new System.Windows.Controls.TextBlock
        {
            Text = (index + 1).ToString(),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                  "TextOnAccentFillColorPrimaryBrush");
        var badge = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),      // = 半边长 → 正圆
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
        return badge;
    }

    private static SymbolRegular DevIcon(int t) => t switch
    {
        AmbientCore.DevScreen => SymbolRegular.Desktop24,
        AmbientCore.DevFan => SymbolRegular.PuzzleCube24,
        AmbientCore.DevDesk => SymbolRegular.RectangleLandscape24,
        AmbientCore.DevSound => SymbolRegular.RectanglePortrait24,
        _ => SymbolRegular.UsbPlug24,
    };

    private static string DevTypeName(int t) => t switch
    {
        AmbientCore.DevScreen => "屏幕灯带",
        AmbientCore.DevFan => "ARGB 风扇",
        AmbientCore.DevDesk => "桌面灯条",
        AmbientCore.DevSound => "立柱氛围灯",
        _ => "未知",
    };

    private void OnDevicesChanged() => _ = RefreshDevicesAsync();

    // ---- 总开关 (状态卡与多设备列表同步: 开关后延时刷新, 等会话端口/状态回填) ----
    // 暂停期间整个流程持锁 + 按钮禁用: 快速连点会连发 Start/Stop, 核心侧留下
    // "device lost: no usable port" 之类的残留诊断 (轮 16 用户反馈)
    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_toggling) return;
        _toggling = true;
        ToggleBtn.IsEnabled = false;
        try
        {
            if (App.Instance.CoreRunning)
            {
                App.Instance.StopCore();
                _block = null;              // 用户主动关闭 → 顺手清掉上一次的失败提示
            }
            else
            {
                // 轮 18 ③: 开启失败**不进入"开启"态**。App 侧对"设备被占用/未应答/不在位"已不再建
                // 兜底会话 (否则 banner 会亮起一个"未连接"的假开启态, 按钮却写着"开启", 再点变成关闭)。
                // 这里把失败原因取回来交给胶囊 (警示色) + InfoBar; 按钮保持"开启", 再点即重试。
                // 不在这里手动画 banner: 让 250ms 轮询发现 _block 变化后统一渲染, 免得动画跑两遍。
                await App.Instance.StartAllAsync();
                _block = App.Instance.CoreRunning ? null : App.Instance.LastStartProblem;
            }
            await Task.Delay(600);              // 等会话端口/状态回填再刷设备列表
            _ = RefreshDevicesAsync();
        }
        finally
        {
            _toggling = false;
            ToggleBtn.IsEnabled = true;
        }
    }

    // ---- 亮度 (节流下发: 首次立即生效, 拖动中每 40ms 一次, 收尾补发最终值) ----
    // 轨道点击跳转/跟手拖动由 SliderJump 附加行为接管 (与设置页滑条一致)
    // 轮 28: 拖动中**只热更、不写盘**; 停手 400ms 再落盘一次。原来是每次热更都同步
    // File.WriteAllText —— 拖动中 25 次/秒落在 UI 线程上, 那是拖亮度时的卡顿源之一。
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

    // ---- 设备 / 显示器 -------------------------------------------------
    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    private void OnRefreshMonitorsClick(object sender, RoutedEventArgs e) => RefreshMonitors();

    /// <summary>语义胶囊 (与 banner 同一色板): 绿=运行中 / 琥珀=端口被占用·未应答 / 灰=未在位 /
    /// 强调=当前 / 蓝=连接中</summary>
    private static readonly SolidColorBrush PillGreenBg = Frozen(Color.FromArgb(0x2E, 0x2D, 0xC0, 0x8A));
    private static readonly SolidColorBrush PillGreenFg = Frozen(Color.FromRgb(0x8C, 0xE8, 0xC2));
    private static readonly SolidColorBrush PillAmberBg = Frozen(Color.FromArgb(0x2E, 0xF0, 0xA0, 0x2E));
    private static readonly SolidColorBrush PillAmberFg = Frozen(Color.FromRgb(0xF6, 0xCE, 0x92));
    private static readonly SolidColorBrush PillBlueBg = Frozen(Color.FromArgb(0x2E, 0x4C, 0x8D, 0xFF));
    private static readonly SolidColorBrush PillBlueFg = Frozen(Color.FromRgb(0xA9, 0xD8, 0xFF));

    private static Border Pill(string text, Brush? bg, Brush? fg,
                               string? bgKey = null, string? fgKey = null, double left = 0)
    {
        var tb = new System.Windows.Controls.TextBlock { Text = text, FontSize = 10.5 };
        if (fg != null) tb.Foreground = fg;
        else if (fgKey != null) tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, fgKey);
        var b = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(left, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = tb,
        };
        if (bg != null) b.Background = bg;
        else if (bgKey != null) b.SetResourceReference(Border.BackgroundProperty, bgKey);
        return b;
    }

    private void RefreshMonitors()
    {
        if (MonitorsPanel == null) return;
        MonitorsPanel.Children.Clear();
        try
        {
            var mons = AmbientCore.GetMonitors();
            if (mons.Length == 0)
            {
                MonitorsPanel.Children.Add(GrayText("未枚举到显示器"));
                return;
            }
            int cur = ConfigStore.Device.MonitorIndex;
            for (int i = 0; i < mons.Length; i++)
                MonitorsPanel.Children.Add(BuildMonitorRow(i, mons[i], i == cur));
            // 选择器不在这里重复做 (已在「采集」页) —— 这里只做"正在采集的是哪台"的识别
            var hint = GrayText("切换采集目标在「采集」页");
            hint.Margin = new Thickness(0, 4, 0, 0);
            MonitorsPanel.Children.Add(hint);
        }
        catch (Exception ex)
        {
            MonitorsPanel.Children.Add(GrayText($"枚举失败: {ex.Message}"));
        }
    }

    private static System.Windows.Controls.TextBlock GrayText(string text)
    {
        var t = new System.Windows.Controls.TextBlock { Text = text, FontSize = 12 };
        t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                               "TextFillColorTertiaryBrush");
        return t;
    }

    /// <summary>显示器行 (轮 27 用户定稿): `编号  分辨率  适配器`。
    /// - 编号 = UI 库里现成的数字圆形图标 (NumberCircle1..9), 不再显示 `[0] \\.\DISPLAY5`;
    /// - 适配器用**次要色** (它只是"哪块卡带出来的", 不是主信息); 不再挂悬浮提示;
    /// - 去掉按长宽比画的小方块 (形状预览)。
    /// 注: 适配器名受 native 侧 32 字节缓冲限制, 太长的会被截断 (如 "…Laptop GPU" → "…Laptop")。</summary>
    private UIElement BuildMonitorRow(int index, ACMonitorInfo m, bool capturing)
    {
        var numIcon = MonitorBadge(index);

        var res = new System.Windows.Controls.TextBlock
        {
            Text = m.width > 0 && m.height > 0 ? $"{m.width}×{m.height}" : "分辨率未知",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        res.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                 "TextFillColorPrimaryBrush");
        var adapter = new System.Windows.Controls.TextBlock
        {
            Text = m.Adapter,
            FontSize = 11.5,
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        adapter.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                     "TextFillColorTertiaryBrush");

        var texts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
        };
        texts.Children.Add(res);
        texts.Children.Add(adapter);

        var marks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (m.primary != 0)
            marks.Children.Add(Pill("主屏", null, null, "ControlFillColorDefaultBrush", "TextFillColorSecondaryBrush"));
        if (capturing)
            marks.Children.Add(Pill("正在采集", PillBlueBg, PillBlueFg, left: 6));

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(numIcon, 0);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(marks, 2);
        grid.Children.Add(numIcon);
        grid.Children.Add(texts);
        grid.Children.Add(marks);
        return grid;
    }

    private async Task RefreshDevicesAsync()
    {
        if (DevicesPanel == null) return;
        DevicesPanel.Children.Clear();
        DevicesPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "探测中… (打开串口握手, ~0.2s/台)",
            Opacity = 0.6,
            FontSize = 12,
        });
        try
        {
            var devices = await Task.Run(AmbientCore.GetDevices).ConfigureAwait(true);
            DevicesPanel.Children.Clear();
            if (devices.Length == 0)
            {
                DevicesPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "未发现灯带 (VID_2E3C & PID_5740); 检查 USB 连接, 并确认 Ansnake 未占用端口",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                });
                return;
            }
            foreach (var d in devices)
            {
                // 本核心会话占用的端口必然探测为 [端口被占用][未应答] —
                // 类型/编号/运行态改从该会话的 ACStatus 取
                ACStatus? sess = null;
                int sessSlot = -1;
                foreach (var slot in App.Instance.Slots.Keys)
                {
                    var st = AmbientCore.GetStatusEx(slot);
                    if (st.state != AmbientCore.StateStopped &&
                        string.Equals(st.ComPort, d.ComPort, StringComparison.OrdinalIgnoreCase))
                    {
                        sess = st;
                        sessSlot = slot;
                        break;
                    }
                }
                if (sess == null && d.inUse == 1)
                {
                    // 刚 Start 的会话 ComPort 可能尚未回填: 恰有一个非停止会话时按归属处理
                    int only = -1;
                    foreach (var slot in App.Instance.Slots.Keys)
                    {
                        var st = AmbientCore.GetStatusEx(slot);
                        if (st.state == AmbientCore.StateStopped) continue;
                        if (only >= 0) { only = -2; break; }
                        only = slot;
                    }
                    if (only >= 0) { sess = AmbientCore.GetStatusEx(only); sessSlot = only; }
                }
                DevicesPanel.Children.Add(BuildDeviceRow(d, sess, sessSlot));
            }
        }
        catch (Exception ex)
        {
            DevicesPanel.Children.Clear();
            DevicesPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"探测失败: {ex.Message}",
                FontSize = 12,
            });
        }
    }

    // 每台设备一行 (§6): 类型图标 + 名称 + 语义胶囊 + 右侧动作; COM/供电/灯数下沉第二行小字。
    // 「当前」设备用左侧 3px 强调条与强调色标签标出 (与模式卡选中态同语言)。
    // sess = 该端口被本核心会话占用时的会话状态 (探测拿不到握手, 类型取自会话);
    // sessSlot = 那个会话的 slot —— 会话刚起时 devType 还没回填 (=-1), 组不出正确的设备 key,
    // 必须用 slot 反查 (否则"连接中"那一行点开启会去 StartDevice("-1_-1"))。
    private UIElement BuildDeviceRow(ACDeviceInfo d, ACStatus? sess, int sessSlot)
    {
        bool managedHere = sess != null;
        int devType = sess?.devType ?? d.devType;
        int devId = sess?.devId ?? d.devId;
        bool usable = managedHere || (d.devType >= 0 && d.inUse == 0);
        int slot = managedHere ? sessSlot : -1;
        bool connecting = managedHere && devType < 0;
        // 端口被占着但会话还没登记 → 极可能是"我们自己正在起"(应用启动时的那次总开启):
        // StartEx 要先握手才返回, 而握手要先独占端口 → 这中间 `_slots` 里没有条目。
        // 不区分就会在首次打开时显示"未知 · id -1 + 端口被占用"(轮 27 用户实测现象)。
        bool likelyOurs = !managedHere && d.inUse == 1
                          && (App.Instance.CoreRunning || App.Instance.Starting);
        string key = managedHere
            ? (App.Instance.KeyOf(sessSlot) ?? $"{devType}_{devId}")
            : $"{devType}_{devId}";
        bool selected = usable && ConfigStore.SelectedKey == key;

        // 语义胶囊: 绿=运行中 / 琥珀=端口被占用·未应答 / 灰=就绪·未在位 / 蓝=连接中
        Border pill =
            connecting || likelyOurs ? Pill("连接中…", PillBlueBg, PillBlueFg, left: 8)
            : slot >= 0 ? Pill("运行中", PillGreenBg, PillGreenFg, left: 8)
            : !managedHere && d.inUse == 1 ? Pill("端口被占用", PillAmberBg, PillAmberFg, left: 8)
            : !managedHere && d.devType < 0 ? Pill("未应答", PillAmberBg, PillAmberFg, left: 8)
            : Pill("就绪", null, null, "ControlFillColorDefaultBrush", "TextFillColorSecondaryBrush", left: 8);

        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = DevIcon(devType),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
        var name = new System.Windows.Controls.TextBlock
        {
            Text = connecting ? $"{DevTypeName(devType)} · 读取中"
                              : likelyOurs ? "读取中…"
                              : $"{DevTypeName(devType)} · id {devId}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        line1.Children.Add(icon);
        line1.Children.Add(name);
        if (selected)
        {
            // ⚠️ 强调色**底**上的字要用 TextOnAccentFillColorPrimaryBrush。
            // 原来用 AccentTextFillColorPrimaryBrush = 强调色写强调色 → 用户看到的"不明橙色胶囊"
            var cur = Pill("当前", null, null, "AccentFillColorDefaultBrush",
                           "TextOnAccentFillColorPrimaryBrush", left: 8);
            line1.Children.Add(cur);
        }
        line1.Children.Add(pill);

        var line2 = new System.Windows.Controls.TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(24, 2, 0, 0),
        };
        line2.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                                   "TextFillColorTertiaryBrush");
        string power = managedHere && sess is { } ss
            ? (((ss.ledStatusByte & 1) == 1) ? "供电 外接" : "供电 USB")
            : "";
        // 只用查得到的配置, 不 GetOrCreate —— 光是"看一眼设备列表"不该给没见过的设备建配置文件
        string leds = managedHere && ConfigStore.Devices.TryGetValue(key, out var ds)
            ? $"{ds.LedCount} 灯" : "";
        line2.Text = string.Join(" · ", new[] { d.ComPort, power, leds }.Where(s => s.Length > 0));

        var infoCol = new StackPanel
        {
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        infoCol.Children.Add(line1);
        if (line2.Text.Length > 0) infoCol.Children.Add(line2);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toggle = new Wpf.Ui.Controls.Button
        {
            Content = slot >= 0 ? "关闭" : "开启",
            Appearance = slot >= 0 ? ControlAppearance.Secondary : ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = usable,
            MinWidth = 64,
        };
        toggle.Click += async (_, _) =>
        {
            if (App.Instance.SlotOf(key) >= 0) App.Instance.StopDevice(key);
            else App.Instance.StartDevice(key);
            await Task.Delay(600);              // 会话端口/状态回填后再刷, 避免闪现"端口被占用"
            _ = RefreshDevicesAsync();
        };
        actions.Children.Add(toggle);
        if (!selected)
        {
            var select = new Wpf.Ui.Controls.Button
            {
                Content = "设为当前",
                IsEnabled = usable,
                Appearance = ControlAppearance.Secondary,
                MinWidth = 76,
            };
            select.Click += (_, _) =>
            {
                ConfigStore.Select(key);
                _ = RefreshDevicesAsync();
            };
            actions.Children.Add(select);
        }

        var outer = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (selected) bar.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");

        Grid.SetColumn(bar, 0);
        Grid.SetColumn(infoCol, 1);
        Grid.SetColumn(actions, 2);
        outer.Children.Add(bar);
        outer.Children.Add(infoCol);
        outer.Children.Add(actions);
        return outer;
    }
}

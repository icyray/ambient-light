// HomePage.xaml.cs — 仪表盘: banner(状态/开关) / 亮度 / 模式 / 拾色器 / 预览 / 指标 / 设备
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
    private bool _updatingMode;                 // 程序化设 IsChecked 时不回环下发
    private bool _hexEditing;                   // HexBox 输入过程中避免反写
    private readonly Dictionary<int, System.Windows.Controls.Control> _modeBtns = new();

    // banner 文字/胶囊用**自有画刷**: 只有自己的 SolidColorBrush 才能做 ColorAnimation
    // (主题资源刷子是共享且可能已冻结的)
    private readonly SolidColorBrush _titleBrush = new();
    private readonly SolidColorBrush _subBrush = new();
    private readonly SolidColorBrush _portBrush = new();
    private readonly SolidColorBrush _fpsBrush = new();
    private readonly SolidColorBrush _pillBgBrush = new();
    private readonly SolidColorBrush _pillTextBrush = new();
    private readonly SolidColorBrush _dotBrush = new();

    public HomePage()
    {
        InitializeComponent();
        Controls.WheelScroll.Attach(this);      // 滚轮滚动由外层滚动器承担
        BuildPresets();

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
        SRSlider.ValueChanged += OnSolidRgbChanged;
        SGSlider.ValueChanged += OnSolidRgbChanged;
        SBSlider.ValueChanged += OnSolidRgbChanged;
        _loaded = true;

        _ = RefreshDevicesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Instance.Poller.Polled -= OnPolled;
        App.Instance.Poller.DevicesChanged -= OnDevicesChanged;
        _lastPolledState = -1;
    }

    // ---- 模式选择 (M5.1 主次分层): 三张主模式卡 (RadioButton 模板) + 固件特效折叠 ----
    private void BuildModeGrid()
    {
        PrimaryModeGrid.Children.Clear();
        FxPanel.Children.Clear();
        _modeBtns.Clear();
        var cardTemplate = (ControlTemplate)FindResource("ModeCardTemplate");
        int primaryIndex = 0;
        foreach (var (index, name, kind) in AmbientCore.Modes)
        {
            if (kind == "MCU")
            {
                // 固件特效 = 折叠区内小芯片按钮
                var chip = new Wpf.Ui.Controls.Button
                {
                    Content = name,
                    MinWidth = 92,
                    Margin = new Thickness(0, 0, 6, 6),
                };
                chip.Click += (_, _) => SetMode(index);
                FxPanel.Children.Add(chip);
                _modeBtns[index] = chip;
                continue;
            }

            // 主模式卡 (Gallery 风): 44px 圆角色块图标 + 标题 + 一句功能说明;
            // 等宽三列, 列位 = 序号×2 (中间列是 8px 间隙)
            // 色块 = 与 banner 光带同色系 (蓝/绿/紫), 便于"看色识模式"
            var (icon, tileColor) = index switch
            {
                0 => (SymbolRegular.WeatherSunny24, System.Windows.Media.Color.FromRgb(0x4C, 0x8D, 0xFF)),
                1 => (SymbolRegular.SoundWaveCircle24, System.Windows.Media.Color.FromRgb(0x2D, 0xC0, 0x8A)),
                _ => (SymbolRegular.Color24, System.Windows.Media.Color.FromRgb(0xA7, 0x8B, 0xFA)),
            };
            var tile = new Border
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
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x14, 0x1A, 0x1F)),
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
            content.Children.Add(tile);
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

    private void RefreshModeButtons()
    {
        int cur = ConfigStore.Device.ModeIndex;
        _updatingMode = true;
        foreach (var (index, _, _) in AmbientCore.Modes)
        {
            if (!_modeBtns.TryGetValue(index, out var ctl)) continue;
            switch (ctl)
            {
                case System.Windows.Controls.RadioButton rb:
                    rb.IsChecked = index == cur;
                    break;
                case Wpf.Ui.Controls.Button chip:
                    chip.Appearance = index == cur ? ControlAppearance.Primary : ControlAppearance.Secondary;
                    break;
            }
        }
        _updatingMode = false;
        bool solid = cur == 11;
        // 纯色拾色器每次展示时回到**折叠**态 (只露 Header: HEX 输入 + 色块预览);
        // 预设色板/RGB 滑块由用户点标题条自行展开 (反馈修复轮 13)
        if (solid) SolidCard.IsExpanded = false;
        FxExpander.IsExpanded = AmbientCore.IsMcuMode(cur);   // 正用固件特效时保持展开
        AnimatedVisibility.SetExpanded(SolidCard, solid);     // 纯色拾色器仅纯色灯光模式显示
    }

    private void SetMode(int mode)
    {
        if (_updatingMode || !_loaded || ConfigStore.Device.ModeIndex == mode) return;
        ConfigStore.Device.ModeIndex = mode;
        RefreshModeButtons();
        App.Instance.ApplyConfig();             // 模式切换立即生效
        UpdateRingHint(ConfigStore.Device.ModeIndex);
    }

    // ---- 纯色灯光拾色器 (M5.1 用户反馈⑪: 从颜色页迁入, 仅纯色灯光模式显示) ----
    private static readonly (string Hex, string Name)[] Presets =
    {
        ("#FFE1D6", "暖白"), ("#FFF6E0", "米白"), ("#FFFFFF", "纯白"), ("#C9E4FF", "冷白"),
        ("#FF3B30", "红"),   ("#FF9500", "橙"),   ("#FFCC00", "黄"),   ("#34C759", "绿"),
        ("#5AC8FA", "青"),   ("#007AFF", "蓝"),   ("#AF52DE", "紫"),   ("#FF2D55", "粉"),
    };

    private void BuildPresets()
    {
        foreach (var (hex, name) in Presets)
        {
            var btn = new System.Windows.Controls.Button
            {
                Width = 34,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 6),
                Background = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x44)),
                ToolTip = name,
                Tag = hex,
            };
            btn.Click += OnPresetClick;
            PresetStrip.Items.Add(btn);
        }
    }

    private void LoadSolidFromConfig()
    {
        var (r, g, b) = ParseHex(ConfigStore.Device.SolidColor, (255, 225, 214));
        _hexEditing = true;
        SRSlider.Value = r;
        SGSlider.Value = g;
        SBSlider.Value = b;
        RefreshSolidValueTexts();
        RefreshSolidPreview(r, g, b);
        HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
        _hexEditing = false;
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string hex }) return;
        var (r, g, b) = ParseHex(hex, (255, 225, 214));
        _hexEditing = true;
        SRSlider.Value = r;
        SGSlider.Value = g;
        SBSlider.Value = b;
        _hexEditing = false;
        ApplySolid(r, g, b);
    }

    private void OnSolidRgbChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SRValue == null || SGValue == null || SBValue == null) return;   // 解析期防护
        RefreshSolidValueTexts();
        if (_hexEditing || !_loaded) return;
        ApplySolid((int)SRSlider.Value, (int)SGSlider.Value, (int)SBSlider.Value);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_hexEditing || !_loaded) return;
        var (r, g, b) = ParseHex(HexBox.Text, (-1, -1, -1));
        if (r < 0) return;                       // 输入未完成时忽略
        _hexEditing = true;
        SRSlider.Value = r;
        SGSlider.Value = g;
        SBSlider.Value = b;
        _hexEditing = false;
        ApplySolid(r, g, b);
    }

    private void RefreshSolidValueTexts()
    {
        SRValue.Text = $"{(int)SRSlider.Value}";
        SGValue.Text = $"{(int)SGSlider.Value}";
        SBValue.Text = $"{(int)SBSlider.Value}";
    }

    private void ApplySolid(int r, int g, int b)
    {
        RefreshSolidPreview(r, g, b);
        ConfigStore.Device.SolidColor = $"#{r:X2}{g:X2}{b:X2}";
        App.Instance.ApplyConfigDebounced();
        _hexEditing = true;
        HexBox.Text = $"#{r:X2}{g:X2}{b:X2}";
        _hexEditing = false;
    }

    private void RefreshSolidPreview(int r, int g, int b)
        => SolidPreview.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b));

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

    // ---- 状态轮询 ----------------------------------------------------
    private unsafe void OnPolled(ACStatus st)
    {
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

        FpsText.Text = st.fps.ToString("F1");
        BannerFpsText.Text = $"{st.fps:F1} fps";
        FramesText.Text = st.frameCounter.ToString("N0");
        ProcText.Text = $"{st.procMs:F2} ms";
        WireText.Text = $"{st.wireMs:F2} ms";

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

        UpdateRing(st);
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

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>主题刷色 (关闭态用)。5.2 换主题后下次状态变化会重新取到新值</summary>
    private Color ThemeColor(string key) => ThemeColor(key, Colors.White);

    private Color ThemeColor(string key, Color fallback)
        => TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

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

    private void UpdateRing(ACStatus st)
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
            UpdateRingHint(dev.ModeIndex);
            ModeTagText.Text = $"{AmbientCore.ModeName(dev.ModeIndex)} · {dev.LedCount} 灯";
        }
        if (st.state == AmbientCore.StateRunning && AmbientCore.IsHostFrameMode(dev.ModeIndex))
        {
            Ring.Update(st.CopyLedRgb(), dev.LedCount);
        }
        else if (st.state == AmbientCore.StateRunning && dev.ModeIndex == 1)
        {
            // 拾音: Adz 帧只有音量字节, 无逐灯颜色 → 预览区显示实时音量
            Ring.Clear();
            Ring.SetHint($"拾音律动中 · 音量 {st.audioLevel}/255" +
                         (st.audioOk == 2 ? " · 音源重建中…" : "") +
                         (st.audioOk == 0 ? " · 音源未就绪" : ""));
        }
        else if (st.state == AmbientCore.StateStopped)
        {
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

    private void UpdateRingHint(int mode)
    {
        if (AmbientCore.IsMcuMode(mode))
            Ring.SetHint("MCU 特效由灯带固件渲染, 主机无帧数据可预览");
        else if (mode == 1)
            Ring.SetHint(ConfigStore.Device.AudioSource == 1
                ? "固件自拾音: 灯带自己渲染律动, 主机无帧数据"
                : "主机拾音: 灵敏度等参数在「拾音」页调节");
        else if (mode == 11)
            Ring.SetHint("纯色模式: 颜色在上方「纯色灯光」卡调节");
        else
            Ring.SetHint("实时显示每颗 LED 最近下发的颜色");
    }

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
    private const double BrightnessThrottleMs = 40;
    private DateTime _lastBrightnessApply = DateTime.MinValue;
    private DispatcherTimer? _brightnessFlushTimer;

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
        App.Instance.ApplyConfig();
    }

    // ---- 设备 / 显示器 -------------------------------------------------
    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _ = RefreshDevicesAsync();

    private void OnRefreshMonitorsClick(object sender, RoutedEventArgs e) => RefreshMonitors();

    private void RefreshMonitors()
    {
        try
        {
            var mons = AmbientCore.GetMonitors();
            MonitorsText.Text = mons.Length == 0
                ? "未枚举到显示器 "
                : string.Join("\n", mons.Select((m, i) =>
                    $"[{i}] {m.Name}  {m.width}x{m.height}{(m.primary != 0 ? "  (主屏)" : "")}  —  {m.Adapter}"));
        }
        catch (Exception ex)
        {
            MonitorsText.Text = $"枚举失败: {ex.Message}";
        }
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
                foreach (var slot in App.Instance.Slots.Keys)
                {
                    var st = AmbientCore.GetStatusEx(slot);
                    if (st.state != AmbientCore.StateStopped &&
                        string.Equals(st.ComPort, d.ComPort, StringComparison.OrdinalIgnoreCase))
                    {
                        sess = st;
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
                    if (only >= 0) sess = AmbientCore.GetStatusEx(only);
                }
                DevicesPanel.Children.Add(BuildDeviceRow(d, sess));
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

    // 每台设备一行: 信息 + 开启/关闭 + 设为当前 (多设备会话管理入口)。
    // sess = 该端口被本核心会话占用时的会话状态 (探测拿不到握手, 类型取自会话)
    private UIElement BuildDeviceRow(ACDeviceInfo d, ACStatus? sess)
    {
        bool managedHere = sess != null;
        int devType = sess?.devType ?? d.devType;
        int devId = sess?.devId ?? d.devId;
        bool usable = managedHere || (d.devType >= 0 && d.inUse == 0);
        string key = $"{devType}_{devId}";
        int slot = managedHere ? App.Instance.SlotOf(key) : -1;
        bool selected = usable && ConfigStore.SelectedKey == key;

        var info = new System.Windows.Controls.TextBlock
        {
            Text = $"{d.ComPort}  " +
                   (managedHere && devType < 0
                       ? "连接中…"
                       : $"{DevTypeName(devType)}  id={devId}") +
                   (slot >= 0 ? "  · 运行中" : "") +
                   (!managedHere && d.inUse == 1 ? "  [端口被占用]" : "") +
                   (!managedHere && d.devType < 0 ? "  [未应答]" : ""),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
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
        var select = new Wpf.Ui.Controls.Button
        {
            Content = selected ? "当前" : "设为当前",
            IsEnabled = usable && !selected,
            Appearance = ControlAppearance.Secondary,
            MinWidth = 76,
        };
        select.Click += (_, _) =>
        {
            ConfigStore.Select(key);
            _ = RefreshDevicesAsync();
        };

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(info, 0);
        Grid.SetColumn(toggle, 1);
        Grid.SetColumn(select, 2);
        grid.Children.Add(info);
        grid.Children.Add(toggle);
        grid.Children.Add(select);
        return grid;
    }
}

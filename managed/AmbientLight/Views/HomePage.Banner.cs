// HomePage.Banner.cs — banner 视觉 (点亮/胶囊/文字色) + 底边辉光 (数值驱动的模糊光带)。
// (HomePage partial 之一: 核心状态在 HomePage.xaml.cs, 本文件只放这一块职责)
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
        // 比较键里含主题 → 换主题时它也变了, 于是辉光/识别线的颜色会自动跟着重解
        string key = ColorKey(mode, ThemeService.IsDark);
        bool modeChanged = key != _glowBrushKey;
        if (modeChanged)
        {
            _glowBrushKey = key;
            AnimateGlowColor(ModeGlowColor(mode, ThemeService.IsDark), _glowApplied);
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

    /// <summary>辉光基色 = 与 banner 光带同一套色系 (轮 27 用户定稿: 不再逐灯上色)。
    /// **分主题** (D1-B 起): 浅色主题的辉光是"叠在浅底上的一层着色", 需要跟深色主题用不同的基色 ——
    /// 初始值照抄深色版 (浅色观感 = 已验收的 B 方案), 想调就改浅色那一列。
    /// 与 `StreakBrushKey` 一样, 这里是**唯一**的解析处 (调用点不许再分主题写 if)。</summary>
    public static Color ModeGlowColor(int mode, bool dark) => (ModeColorKey(mode), dark) switch
    {
        ("Blue", true) => Rgb(0x56, 0xB0, 0xF0),
        ("Green", true) => Rgb(0x46, 0xD4, 0xC5),
        ("Purple", true) => Rgb(0xC3, 0x8F, 0xF1),
        ("Mono", true) => Rgb(0xE4, 0xEB, 0xF2),
        // 浅色主题: 照抄深色版的色相 (叠在浅底上会变成"一道着色带"), 你可以单独调这一列
        ("Blue", false) => Rgb(0xA3, 0xDD, 0xFF),
        ("Green", false) => Rgb(0xA2, 0xFF, 0xFA),
        ("Purple", false) => Rgb(0xFC, 0xBC, 0xFF),
        // ⚠️ Mono 浅色版**刻意不照抄**: #E4EBF2 是近白灰, 叠在浅底上等于看不见 → 给一档中灰
        _ => Rgb(0xB3, 0xC2, 0xD1),
    };

    /// <summary>光带画刷键 —— **纯函数** (探针打真值表 + 断言资源真能解析)。
    /// 约定: 浅色版 = 深色版 key + `Light` 后缀 (`BannerStreakBlueBrush` / `BannerStreakBlueBrushLight`)。
    /// 与 `BannerBaseKey` 同一套约定, 三种"随主题走的 banner 资源"因此长得一样好记。</summary>
    public static string StreakBrushKey(int mode, bool dark)
        => $"BannerStreak{ModeColorKey(mode)}Brush" + (dark ? "" : "Light");

    /// <summary>色系 + 主题的合成键 (只用于"变了才重刷"的比较)。把主题编进来是刻意的:
    /// **换主题时它自己就变了** → 光带的交叉淡化与辉光的换色会自动重解,
    /// 不必在每个调用点写"如果主题变了就强制刷新一遍"。</summary>
    private static string ColorKey(int mode, bool dark) => ModeColorKey(mode) + (dark ? "" : "-light");

    /// <summary>"中心亮 → 两边衰减"的光带: 颜色固定, 亮度全压在 **alpha** 上 ——
    /// 暗处 alpha = 0, 所以辉光是叠加上去的, 不会把 banner 压成黑带。
    /// 画刷**只建一次**, 之后只对 stop 的颜色做动画 (换模式时颜色平滑过渡)。</summary>
    private void EnsureGlowBrush()
    {
        if (_glowBrush != null) return;
        // 初值用"当前模式 + 当前主题" (首次 UpdateGlow 会再平滑对齐一次)
        var c = ModeGlowColor(ConfigStore.Device.ModeIndex, ThemeService.IsDark);
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

    /// <summary>点亮态 banner 的底色画刷键 —— **纯函数**, 探针用它取对应渐变去算对比度。
    /// ★ D1-B 实验分支: 浅色主题下**点亮态也用浅底** (原方案 A 是"点亮态恒深色",
    /// 理由见 M5.2_THEME_PLAN §4.1 的"物理理由" —— 加亮型辉光在浅底上不再发光、变成一道着色带)。</summary>
    public static string BannerBaseKey(bool dark) => dark ? "BannerBaseBrush" : "BannerBaseLightBrush";

    /// <summary>点亮态 banner 的文字/胶囊底色 —— **纯函数**, 探针打真值表 + 算对比度。
    /// 深色主题: **原样** (M5.1 定稿的写死白/浅蓝, 实验分支不许动深色观感);
    /// 浅色主题 (D1-B): 改用**主题色** —— 取值与"未点亮态"完全一致,
    /// 因为浅底上写浅色字会看不见, 而主题色本来就是为"浅底/卡片底"选的那一套。</summary>
    public static (Color Title, Color Sub, Color Port, Color Fps, Color PillBg) LitBannerColors(
        bool dark, Color themePrimary, Color themeSecondary, Color themeControlFillSecondary)
        => dark
            ? (Colors.White, Rgb(0xCF, 0xE3, 0xF2), Rgb(0xA9, 0xC0, 0xCE), Rgb(0x9F, 0xB4, 0xC2),
               Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF))
            : (themePrimary, themeSecondary, themeSecondary, themeSecondary, themeControlFillSecondary);

    private void ApplyBannerLook(int state, int mode, bool animate)
    {
        bool lit = IsLit(state);
        var (label, dot, pillText) = PillLook(state);
        StatePillText.Text = label;
        SetColor(_dotBrush, dot, animate);
        SetColor(_pillTextBrush, pillText, animate);

        // ★ D1-B: 底色随主题 (深色主题仍是原来那条深蓝→深灰渐变, 观感零变化)
        bool dark = ThemeService.IsDark;
        if (FindResource(BannerBaseKey(dark)) is Brush baseBrush) BannerBase.Fill = baseBrush;

        if (lit)
        {
            var c = LitBannerColors(dark,
                ThemeColor("TextFillColorPrimaryBrush"),
                ThemeColor("TextFillColorSecondaryBrush"),
                ThemeColor("ControlFillColorSecondaryBrush"));
            SetColor(_titleBrush, c.Title, animate);
            SetColor(_subBrush, c.Sub, animate);
            SetColor(_portBrush, c.Port, animate);
            SetColor(_fpsBrush, c.Fps, animate);
            // 胶囊底: 深色主题保持中性半透明白 (状态色只落在圆点/文字上); 浅色主题用主题控件底
            SetColor(_pillBgBrush, c.PillBg, animate);
            CrossFadeStreak(mode, animate);
        }
        else
        {
            // 未点亮取主题刷色 → 5.2 浅色主题下自动跟随 (下次状态变化时重取)
            SetColor(_titleBrush, ThemeColor("TextFillColorPrimaryBrush"), animate);
            SetColor(_subBrush, ThemeColor("TextFillColorSecondaryBrush"), animate);
            SetColor(_portBrush, ThemeColor("TextFillColorSecondaryBrush"), animate);
            SetColor(_fpsBrush, ThemeColor("TextFillColorSecondaryBrush"), animate);
            SetColor(_pillBgBrush, ThemeColor("ControlFillColorSecondaryBrush"), animate);
        }
        FadeTo(LightsLayer, lit ? 1 : 0, animate);
    }

    /// <summary>胶囊的文案与配色。未点亮时还分三种情况, 都要"看得出问题但不算开启":
    /// ① 开启失败 (无会话) → 警示色 + 失败原因 (轮 18 ③);
    /// ② 有会话但没在驱动灯带 (DEV_LOST=正在重连 / ERROR) → 用该状态自己的颜色;
    /// ③ 真的停了 → 主题次色 + "已停止"。
    /// **D1-B 实验分支口径**: 点亮态 banner 也随主题 (浅色 = 浅底深字) → 胶囊色板一律按当前主题取,
    /// **不再**有"点亮就固定深色套"这条特例。深色主题下行为与原方案 A 完全一致。</summary>
    private (string Label, Color Dot, Color Text) PillLook(int state)
    {
        bool lit = IsLit(state);
        if (!lit)
        {
            if (_block is { } b) return (b.Label, CautionColor(), CautionColor());
            if (state == AmbientCore.StateStopped)
                return (Loc.T("Banner.State.Stopped"),
                        ThemeColor("TextFillColorTertiaryBrush", Rgb(0x9A, 0x9A, 0x9A)),
                        ThemeColor("TextFillColorSecondaryBrush", Rgb(0xC9, 0xC9, 0xC9)));
        }
        var (dot, text) = ThemePalette.StateOf(state, ThemeService.IsDark);
        return (StateLabel(state), dot, text);
    }

    private Color CautionColor()
        => ThemeColor("SystemFillColorCautionBrush", Rgb(0xF0, 0xA0, 0x2E));

    /// **public 是给探针用的**: 要断"换主题后 StreakA 真的换成浅色版那一条"
    /// (只验资源存在挡不住"资源建了但没人用")。
    public void CrossFadeStreak(int mode, bool animate)
    {
        // 键 = 色系 + 主题: 换主题时键变了 → 自动换成该主题那一套光带 (不用额外写"主题变了"的判断)
        string key = ColorKey(mode, ThemeService.IsDark);
        if (key == _lastStreakKey) return;      // 色系与主题都没变 (如关→开) 不用换, 交给整层淡变
        _lastStreakKey = key;
        var incoming = _streakOnA ? StreakB : StreakA;
        var outgoing = _streakOnA ? StreakA : StreakB;
        incoming.Fill = (Brush)FindResource(StreakBrushKey(mode, ThemeService.IsDark));
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

    /// <summary>状态 → 胶囊文案。DEV_LOST 用"正在尝试重连"而不是"设备未连接":
    /// 核心此时每 2s 重试且会话还在, 说"关闭即打断重连"才讲得通 (轮 19)</summary>
    private static string StateLabel(int state) => Loc.T(state switch
    {
        AmbientCore.StateRunning => "Banner.State.Running",
        AmbientCore.StateStarting => "Banner.State.Starting",
        AmbientCore.StateWaitDisp => "Banner.State.WaitDisp",
        AmbientCore.StateDevLost => "Banner.State.Reconnecting",
        AmbientCore.StateError => "Banner.State.Error",
        _ => "Banner.State.Stopped",
    });

    /// <summary>模式 → 光带色系 (追光=蓝 / 拾音=绿 / 纯色=紫 / 固件特效=中性白灰)。
    /// MCU 模式不参与取色也不律动, 故用白灰而不是琥珀 (琥珀留给"等待/警告"的状态色)</summary>
    private static string ModeColorKey(int mode) => mode switch
    {
        0 => "Blue",
        1 => "Green",
        11 => "Purple",
        _ => "Mono",
    };

}

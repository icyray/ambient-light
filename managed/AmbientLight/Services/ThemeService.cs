// ThemeService.cs — M5.2 浅色/深色主题 (设计契约: docs/M5.2_THEME_PLAN.md)
//
// 机制要点 (全部按 WPF-UI 4.3.0 tag 源码查证, 不是凭记忆):
//   1) ApplicationThemeManager.Apply(theme, backdrop, updateAccent) 内部是用
//      ResourceDictionaryManager("wpf.ui;").UpdateDictionary("theme", "…/Theme/Light.xaml")
//      **整个替换** App.xaml 里那个 <ui:ThemesDictionary>。
//      ⚠️ 找不到就 return(false) —— **静默失败、不抛异常**(探针有一条断言守着这个前置条件)。
//      ⚠️ 所以 App.xaml 里 ThemesDictionary 必须是 Application.Resources.MergedDictionaries 的
//         **直接成员**, 不能挪进嵌套字典; 且 `Theme="Dark"` 那行必须留着
//         (ThemesDictionary 无参构造默认是 Light, 去掉它启动会闪一下浅色)。
//   2) Apply 最后一步只刷 Application.MainWindow 的 backdrop + DWM 标题栏深浅
//      (校准窗不在其中, 它本来也没 backdrop)。
//   3) 高对比度(HC): 库自己会映射成 ApplicationTheme.HighContrast, 且会把 backdrop 降为 None;
//      本项目的决策 (M5.2 §0 D3) 是 **HC 回退深色** —— 4 套 HC 字典我们没走查过,
//      半吊子 HC 比直接给深色更糟。所以这里是自己算 ResolveForApply, 而不是调 ApplySystemTheme()。
//   4) 系统主题/强调色变化: 不用 SystemThemeWatcher, 而用 SystemEvents.UserPreferenceChanged。
//      原因: ① SystemThemeWatcher.Watch 首次挂钩会**强制** ApplySystemTheme (HC 就又跑回 HighContrast);
//            ② 它的 UnWatch 在**未 Loaded** 窗口上会抛异常 —— StartMinimized 时窗口从未 Show;
//            ③ 它只在有 HWND 的窗口上挂钩, 而 SystemEvents 不需要窗口, 最小化启动也照样跟随;
//            ④ 它内部要做的两件事 (Apply 主题 / UpdateBackground) Apply 本身都会做。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AmbientLight.Services;

/// <summary>主题选择 (config/app.json 的 `theme`)。</summary>
public enum ThemeChoice
{
    /// <summary>跟随系统 (默认)。</summary>
    System,
    Light,
    Dark,
}

/// <summary>M5.2 主题服务: 解析配置 → 应用主题 → 跟随系统变化。</summary>
public static class ThemeService
{
    private static Window? _main;
    private static bool _hooked;

    /// <summary>当前配置的选择 (未知值 → 跟随系统)。</summary>
    public static ThemeChoice Current => Parse(ConfigStore.App.Theme);

    /// <summary>当前**生效**的是不是深色主题 (真实来源 = 已应用的资源字典, 不是配置)。
    /// 高对比度按深色处理 (见 ResolveForApply): 我们的自定义色板不为 HC 适配。</summary>
    public static bool IsDark => ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Light;

    // ---- 纯函数 (探针打真值表) -------------------------------------------

    /// <summary>json 值 → 枚举。空/未知/大小写/前后空白一律落到**跟随系统**, 永不抛。</summary>
    public static ThemeChoice Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "light" => ThemeChoice.Light,
        "dark" => ThemeChoice.Dark,
        _ => ThemeChoice.System,
    };

    /// <summary>枚举 → json 值 (与 Parse 往返一致)。</summary>
    public static string ToKey(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Light => "light",
        ThemeChoice.Dark => "dark",
        _ => "system",
    };

    /// <summary>该选择是否需要"跟着系统走"(系统主题变化时重算)。</summary>
    public static bool ShouldWatchSystem(ThemeChoice choice) => choice == ThemeChoice.System;

    /// <summary>选择 + 系统实况 → 实际要应用的 WPF-UI 主题。**纯函数**, 探针打真值表。
    /// 固定浅/深与系统无关; 「跟随系统」时 HC 回退深色 (M5.2 §0 D3)。</summary>
    public static ApplicationTheme ResolveForApply(ThemeChoice choice, SystemTheme system, bool highContrast)
    {
        if (choice == ThemeChoice.Light) return ApplicationTheme.Light;
        if (choice == ThemeChoice.Dark) return ApplicationTheme.Dark;

        if (highContrast) return ApplicationTheme.Dark;              // D3
        return system switch
        {
            SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow => ApplicationTheme.Dark,
            // HC1/HC2/HCBlack/HCWhite 理论上已被上面的 highContrast 拦下, 这里再兜一次
            SystemTheme.HC1 or SystemTheme.HC2 or SystemTheme.HCBlack or SystemTheme.HCWhite
                => ApplicationTheme.Dark,
            _ => ApplicationTheme.Light,        // Light / Flow / Sunrise / Custom / Unknown
        };
    }

    // ---- 副作用 -----------------------------------------------------------

    /// <summary>「跟随系统」用当前的系统主题; 固定浅/深与系统无关。</summary>
    public static ApplicationTheme ResolveNow(ThemeChoice choice)
    {
        // 公开 API: 强制重读注册表 (缓存默认只在首次访问时填, 可能已经是旧的)
        SystemThemeManager.UpdateSystemThemeCache();
        return ResolveForApply(choice, SystemThemeManager.GetCachedSystemTheme(), SystemThemeManager.HighContrast);
    }

    /// <summary>启动期那一次 (在 ConfigStore.Load() 之后、new MainWindow() 之前调用)。
    /// 此时还没有窗口 → Apply 的最后一步(刷 backdrop)是空操作, 窗口创建时
    /// OnSourceInitialized 会按**已经正确**的主题执行 backdrop 序列 → 不会闪。</summary>
    public static void ApplyAtStartup() => Apply(Current);

    /// <summary>接线主窗口并开始跟随系统变化 (窗口创建后调用一次)。</summary>
    public static void Attach(Window main)
    {
        _main = main;
        if (_hooked) return;
        _hooked = true;
        // ① SystemEvents: 覆盖系统主题/个性化变化, **不需要 HWND** → 最小化启动也跟随
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // ② 窗口消息: **强调色**变化走的是 WM_DWMCOLORIZATIONCOLORCHANGED, SystemEvents 收不到它
        //    (WPF-UI 自己的 SystemThemeWatcher 也专门监听了这条消息, 就是为强调色)。
        HookWindowMessages(main);
    }

    /// <summary>退出时退订 (SystemEvents 是静态事件, 不退订会吊着它的隐藏窗口)。</summary>
    public static void Detach()
    {
        if (!_hooked) return;
        _hooked = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _hookSource?.RemoveHook(WindowProc);
        _hookSource = null;
        _main = null;
    }

    private static HwndSource? _hookSource;
    private static DateTime _lastRefreshUtc;

    private static void HookWindowMessages(Window main)
    {
        if (_hookSource is not null) return;
        if (PresentationSource.FromVisual(main) is HwndSource src)
        {
            _hookSource = src;
            src.AddHook(WindowProc);
            return;
        }
        // 窗口还没 Show (StartMinimized) 时没有 HWND → 等它创建出来再挂钩。
        // 挂上之后顺手补刷一次: 隐藏期间的系统主题/强调色变化只有 SystemEvents 那一半收得到
        // (强调色走 WM_DWMCOLORIZATIONCOLORCHANGED, 没 HWND 就收不到), 打开窗口时要能对上。
        main.SourceInitialized += (_, _) =>
        {
            if (_hookSource is not null || !_hooked) return;
            if (PresentationSource.FromVisual(main) is not HwndSource s) return;
            _hookSource = s;
            s.AddHook(WindowProc);
            RefreshFromSystem();
        };
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_SYSCOLORCHANGE = 0x0015;
        const int WM_THEMECHANGED = 0x031A;
        const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

        // WM_SETTINGCHANGE 要按 lParam 过滤: 这条消息被很多程序乱发, 不过滤会造成无谓的重刷
        // (重新膨胀主题字典 = 整字典重建 + 全窗口重绘, 不该被别人的设置变更带着跑)。
        bool immersiveColorSet = msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero
            && string.Equals(Marshal.PtrToStringUni(lParam), "ImmersiveColorSet", StringComparison.Ordinal);
        if (msg is WM_DWMCOLORIZATIONCOLORCHANGED or WM_THEMECHANGED or WM_SYSCOLORCHANGE || immersiveColorSet)
            RefreshFromSystem();
        return IntPtr.Zero;
    }

    /// <summary>系统主题 / 强调色 / 个性化变化 → 自动跟随。
    /// **两个来源** (SystemEvents 与窗口消息) 可能同时触发 → 200ms 内只算一次。</summary>
    private static void RefreshFromSystem()
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if ((DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds < 200) return;
                _lastRefreshUtc = DateTime.UtcNow;
                var theme = ResolveNow(Current);
                // 只在"解析出来的主题真的变了"时才整个 Apply (Apply 会 Remove+Apply backdrop);
                // 主题没变就是纯强调色变化 → 只刷强调色。
                if (theme != ApplicationThemeManager.GetAppTheme())
                    ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
                else
                    ApplyAccentOnly();
            }
            catch
            {
                // 个性化变化期间的偶发 COM/注册表异常不值得让界面崩 —— 静默忽略, 下次变化会再试
            }
        });
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        => RefreshFromSystem();

    /// <summary>外观 (主题或强调色) 变化后触发 —— UI 侧用它"重取 + 重建"那些 **code-behind 一次性赋值**
    /// 的刷子 (容器上的 DynamicResource 是自动的, 但"当时取到的那个刷子实例"不是)。
    /// 为什么要自己发这个事件: `ApplicationThemeManager.Changed` **只在换主题时**触发,
    /// **强调色变化不会触发**它 —— 而换刷子的需求两种变化都有。</summary>
    public static event Action? AppearanceChanged;

    /// <summary>应用主题 (用户改设置 / 启动 / 跟随系统变化都走这里)。
    /// 不再写任何"应用自己的刷子": 说明文字用 WPF-UI 的 `TextFillColorSecondaryBrush`
    /// (轮 34 补收回了自造的 AppTextHintBrush, 避免自己造轮子)。</summary>
    public static void Apply(ThemeChoice choice)
    {
        var theme = ResolveNow(choice);
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: true);
        AppearanceChanged?.Invoke();
    }

    /// <summary>只刷新强调色, 不动主题 —— 与 <see cref="ApplicationThemeManager.Apply"/> 内部那一句
    /// 完全相同 (同一个来源色、同一个变体算法), 这样"改系统强调色"与"切主题"刷出来的观感一致。
    /// 系统强调色本身走 WinRT UISettings.Accent (取不到才回退 DWM 色化色) —— 我们不提供应用内自选强调色。
    ///
    /// ⚠️ 两点都是查 4.3.0 源码 + 探针实测得来 (轮 34 补):
    /// ① **触发**: 强调色变化发的是 `WM_DWMCOLORIZATIONCOLORCHANGED`, **`SystemEvents` 收不到**这条消息
    ///    (WPF-UI 自己的 SystemThemeWatcher 也专门监听了它, 就是为强调色) → 跟不上的真正原因是
    ///    "没人叫醒我们", 见 <see cref="WindowProc"/>。现象就是"自绘控件变了, WPF-UI 的按钮/滑块
    ///    要手动切一下才变"。
    /// ② **中介刷子**: WPF-UI 的控件样式引用的是一组住在**主题字典**里的刷子, 例如
    ///    `AccentButtonBackground.Color = {DynamicResource AccentFillColorDefault}` (按钮 Appearance=Primary)、
    ///    `SliderThumbBackground.Color   = {DynamicResource SystemAccentColorPrimary}` (滑块) ——
    ///    这些用 **DynamicResource** 别名的**会**跟着顶层键走 (探针断言了);
    ///    但 `AccentButtonForeground` / `AccentControlElevationBorderBrush` / `SliderTrackFill` 那几个是
    ///    **StaticResource 钉死**的 → 只有重新膨胀字典才会重算, 所以下面那一步不能省。</summary>
    public static void ApplyAccentOnly()
    {
        var before = ApplicationAccentColorManager.SystemAccent;   // 当前生效值 (读自资源)
        ApplicationAccentColorManager.Apply(
            ApplicationAccentColorManager.GetColorizationColor(),
            ApplicationThemeManager.GetAppTheme(),
            false);
        // 强调色真没变 (重复消息 / 别的程序在发 WM_SETTINGCHANGE) → 什么都不用做。
        // 变了才重新膨胀字典: 那些**用 StaticResource 钉死**的中介刷子
        // (AccentButtonForeground ← TextOnAccentFillColorPrimary, AccentControlElevationBorderBrush ←
        //  ControlStrokeColorOnAccent*) 不会跟着顶层键走 —— 只有重新膨胀才会重算。
        if (ApplicationAccentColorManager.SystemAccent == before) return;
        ReinflateThemeDictionary();
        AppearanceChanged?.Invoke();
    }

    /// <summary>把 App.xaml 里那个主题字典**重新膨胀**成一份新实例 (同一个 Source)。
    /// 作用 = 让住在字典里、且用 `{StaticResource}` 钉死颜色的那几个中介刷子重算:
    /// `AccentButtonForeground` ← `TextOnAccentFillColorPrimary`、
    /// `AccentControlElevationBorderBrush` ← `ControlStrokeColorOnAccent*`、`SliderTrackFill`。
    /// （用 `{DynamicResource}` 别名的那几个 —— `AccentButtonBackground`、`SliderThumbBackground` ——
    /// 本来就会跟着顶层键走, 不需要这一步。）
    /// 不动 merged 列表结构, 也不碰窗口 backdrop。</summary>
    public static void ReinflateThemeDictionary()
    {
        var dicts = Application.Current?.Resources.MergedDictionaries;
        if (dicts is null) return;
        for (int i = 0; i < dicts.Count; i++)
        {
            var source = dicts[i].Source;
            string? uri = source?.ToString();
            if (uri is null) continue;
            if (!uri.Contains("wpf.ui;", StringComparison.OrdinalIgnoreCase)
                || !uri.Contains("theme", StringComparison.OrdinalIgnoreCase))
                continue;
            dicts[i] = new ResourceDictionary { Source = source };
            return;
        }
    }
}

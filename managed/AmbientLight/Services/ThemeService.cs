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
using System.Windows;
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
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>退出时退订 (SystemEvents 是静态事件, 不退订会吊着它的隐藏窗口)。</summary>
    public static void Detach()
    {
        if (!_hooked) return;
        _hooked = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _main = null;
    }

    /// <summary>应用主题 (用户改设置 / 启动 / 跟随系统变化都走这里)。</summary>
    public static void Apply(ThemeChoice choice)
    {
        ApplicationThemeManager.Apply(ResolveNow(choice), WindowBackdropType.Mica, updateAccent: true);
    }

    /// <summary>只刷新强调色, 不动主题 —— 与 <see cref="ApplicationThemeManager.Apply"/> 内部那一句
    /// 完全相同 (同一个来源色、同一个变体算法), 这样"改系统强调色"与"切主题"刷出来的观感一致。
    /// 系统强调色本身走 WinRT UISettings.Accent (取不到才回退 DWM 色化色) —— 我们不提供应用内自选强调色。</summary>
    public static void ApplyAccentOnly() => ApplicationAccentColorManager.Apply(
        ApplicationAccentColorManager.GetColorizationColor(),
        ApplicationThemeManager.GetAppTheme(),
        false);

    /// <summary>系统主题 / 强调色 / 个性化变化 → 自动跟随。
    /// 事件来自 SystemEvents 自己的隐藏窗口线程 → 必须切回 UI 线程。
    /// 只在"解析出来的主题真的变了"时才整个 Apply (Apply 会 Remove+Apply backdrop, 没必要就别动);
    /// 主题没变就是纯强调色变化 → 只刷强调色。</summary>
    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var theme = ResolveNow(Current);
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
}

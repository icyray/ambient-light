// LanguageService.cs — M5.3 i18n: 语言切换 (设计见 docs/M5.3_I18N_SELECTION.md §4)。
//
// ⚠️ **加一种语言 = 加枚举成员 + 表里加一行 + 放一个 Localization/Strings.<key>.xaml**
//    (枚举那一步不能省 —— 它给调用点编译期安全; 但探针有断言"枚举每个非 System 成员都有表行",
//     所以漏一处会当场报错。表里的 Key 就是字典文件名的那一段, 含区域/变体都行: `Strings.zh-TW.xaml` ✓)
//    代码里**不允许**再出现 "不是 zh 就是 en" 这类二值假设 —— 那种写法加第三种语言时
//    不报错、只是**静默**退化成中文/英文 (或加载错字典), 正是社区加语言时最容易踩的坑。
//
// ⚠️ **同一门语言的多个变体 (简体 / 繁体 这种) 也由这张表支撑**, 机制 = "完整标识优先, primary 兜底":
//    · config 值 "zh-TW" 先按**完整 Key** 比 (命中繁体行), 认不出才退回 primary 子标签 ("zh" → 简体行);
//    · 系统 LANGID 先按**完整值**比 (zh-TW = 0x0404 ≠ zh-CN = 0x0804), 认不出才比 primary (低 10 位)。
//    所以繁体行登记 `[0x0404, 0x0C04]`、简体行登记 `[0x0804, 0x04]` 就能各归各位;
//    而**只登记 primary 0x04 的那一行**是"其它中文变体的默认落点" —— 有意的兜底, 不是 bug。
//
// 机制 = **换字典 + 广播** (与 M5.2 主题 1:1 同构):
//   · XAML 侧 `{DynamicResource 键}` —— 把 App.Resources.MergedDictionaries 里那本
//     `Localization/Strings.*.xaml` **整个换成另一本**, WPF 会让所有 DynamicResource 重新求值
//     (与主题换字典同一原理, 所以是**原地替换文本、零跳变**; 探针有断言守着这条不变量)。
//     ⚠️ 这条失效传播**只沿 Application.Windows 走** —— 挂在窗口里的元素会跟着变, 裸元素不会 (实测)。
//   · C# 侧 `Loc.T/Loc.F` 是一次性求值 (下拉项、托盘菜单、磁贴胶囊…) —— 由**订阅者自己重建**:
//        App                    → 重写托盘菜单标题;
//        SettingsPageAdvanced   → 重建本页 4 个下拉的项 (语言选择器就在这页, 必须当场对);
//        HomePage               → 清掉按签名短路的缓存后复用主题那次的重建;
//        SettingsPageAudio/Capture → 重建"设备/显示器"下拉;
//        其余页面不订阅: 切过去时都是新实例, 构造期自然取到新语言。
//     (没采用"换语言就重导航当前页": 那要赌"同类型重复 Navigate 是否重建实例"这个 WPF 内部行为,
//      而且会给页面状态带来一次跳变。)
//   · 用户可见的效果: 切一下, 界面立刻全变, 不用重启 (厂商那套要重启 —— NeedRestart)。
//
// 语言名 (Endonym) **不进字典**: 语言自己的名字必须与当前界面语言无关 ——
// 中文界面里 "English" 要写 English, 英文界面里也要写 English (见 `All` 的 Endonym)。
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AmbientLight.Services;

/// <summary>语言选择 (config/app.json 的 `lang`)。<see cref="System"/> = 跟随系统显示语言。</summary>
public enum LangChoice
{
    System,
    Zh,
    En,
}

/// <summary>一种语言的全部定义 —— 加语言只动这张表, 别在别处写 `if (lang == En)`。</summary>
/// <param name="Choice">枚举值 (给调用点编译期安全)。</param>
/// <param name="Key">config 里的值, 同时也是字典文件名里的那一段 (`Strings.<Key>.xaml`)。
/// 支持带变体的写法 (如 `zh-TW`) —— 派生的文件名就是 `Strings.zh-TW.xaml`。</param>
/// <param name="Endonym">语言**自己**的名字 (下拉里显示, 不进字典)。</param>
/// <param name="LangIds">系统语言标识 —— 见下面的取值说明与官方出处。</param>
public sealed record LangDef(LangChoice Choice, string Key, string Endonym, ushort[] LangIds);

/// <summary>M5.3 语言服务: 解析配置 → 换字典 → 广播重取 → (跟随系统时) 跟随 Windows 显示语言。</summary>
public static class LanguageService
{
    /// <summary>★ **语言表 = 唯一事实源**。加一行 = 加一种语言 (枚举成员也要加, 探针守着这条)。
    ///
    /// `LangIds` 是 **Windows 语言标识 (LANGID)** —— `GetUserDefaultUILanguage()` / 注册表 / LCID 都是这个值:
    ///   · 低 10 位 = **primary language id** (哪门语言): 0x04 中文 / 0x09 英语 / 0x11 日语 / 0x12 韩语 /
    ///     0x07 德语 / 0x0C 法语 / 0x19 俄语 / 0x0A 西班牙语 / 0x10 意大利语 / 0x16 葡萄牙语 /
    ///     0x13 荷兰语 / 0x15 波兰语 / 0x1F 土耳其语 / 0x1E 泰语 / 0x21 印尼语 / 0x14 挪威语 …
    ///   · 高位 = **sublanguage / 区域** (哪种变体): 0x0804 zh-CN / 0x0404 zh-TW / 0x0C04 zh-HK /
    ///     0x0809 en-GB / 0x0409 en-US / 0x0C0A 西语(现代) …
    ///   完整对照表 (官方, 加语言时照这张表抄):
    ///     https://learn.microsoft.com/windows/win32/intl/language-identifier-constants-and-strings
    ///     https://learn.microsoft.com/windows-hardware/manufacture/desktop/available-language-packs-for-windows
    ///   两种写法都合法, 且**各有用途** (见文件头的"完整标识优先, primary 兜底"):
    ///     登记完整值 (如繁体行 `0x0404`) = 只要这一种变体; 登记 primary (如简体行 `0x04`) = 兜住所有变体。
    ///   取不到系统语言时的兜底走 <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>.LCID, 同为 LANGID。</summary>
    public static readonly LangDef[] All =
    [
        new(LangChoice.Zh, "zh", "简体中文", [0x0804, 0x04]),   // 0x0804 = zh-CN; 0x04 = 其余中文变体的默认落点
        new(LangChoice.En, "en", "English",  [0x0409, 0x09]),   // 0x0409 = en-US; 0x09 = 其余英语变体的默认落点
        // 将来加繁体: new(LangChoice.ZhHant, "zh-TW", "繁體中文", [0x0404, 0x0C04, 0x1404])  + 枚举成员 + 字典文件
    ];

    /// <summary>系统语言 / 未知语言回落到哪一本 —— 国际惯例: 认不出就英文。
    /// 想改成"回落到中文"只改这一行 (探针有真值表守着这条规则)。</summary>
    public static readonly LangDef Fallback = All.First(d => d.Choice == LangChoice.En);

    /// <summary>App.xaml 启动时合并的那一本 (改 App.xaml 的合并行就必须同步改这里)。
    /// 只用来避免"启动时白重建一次本来就对的字典"; 探针有一条断言核对两者一致。</summary>
    public static readonly LangChoice StartupDefault = LangChoice.Zh;

    /// <summary>所有文案字典都在这个目录下 —— 换字典时按"路径含它 + 后缀 .xaml"认出那本, 与语言无关。</summary>
    private const string DictFolder = "Localization";
    private const string PackPrefix = "pack://application:,,,/AmbientLight;component/Localization/";

    // ---- 表查询 (加语言不用动) -------------------------------------------

    /// <summary>枚举 → 定义; 未知/System 落到 <see cref="Fallback"/> (只用于"取字典文件"这类场合)。</summary>
    public static LangDef DefOf(LangChoice choice) => All.FirstOrDefault(d => d.Choice == choice) ?? Fallback;

    /// <summary>config 值 → 定义 (**完整键**精确匹配)。</summary>
    public static LangDef? ByKey(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>系统 LANGID → 定义。**完整值优先, primary 兜底** —— 这是同语多变体 (简/繁) 能各归各位的关键:
    /// 先看有没有哪行**完整**登记了它 (zh-TW 0x0404), 没有再比 primary (低 10 位, 0x0404 → 0x04)。
    /// 若只比 primary, 繁中就会被"primary 相同"的理由吞给先登记的那行 (静默, 且**依赖表顺序**)。
    /// <paramref name="table"/> 只给探针用 (拿合成表验机制, 真表里没有繁中行)。</summary>
    public static LangDef? ByLangId(ushort langId, LangDef[]? table = null)
    {
        var t = table ?? All;
        var full = t.FirstOrDefault(d => d.LangIds.Contains(langId));
        if (full is not null) return full;
        ushort primary = (ushort)(langId & 0x3FF);
        return t.FirstOrDefault(d => d.LangIds.Contains(primary));
    }

    /// <summary>目标语言对应的字典**文件名** (由 Key 派生 —— 加语言不必再登记第二遍)。</summary>
    public static string DictFileOf(LangChoice effective) => "Strings." + DefOf(effective).Key + ".xaml";

    /// <summary>目标语言字典的绝对 pack URI (不依赖调用点的相对基址)。</summary>
    public static string SourceOf(LangChoice effective) => PackPrefix + DictFileOf(effective);

    /// <summary>当前配置的选择 (未知值 → 跟随系统)。</summary>
    public static LangChoice Current => Parse(ConfigStore.App.Lang);

    // ---- 纯函数 (探针打真值表) -------------------------------------------

    /// <summary>json 值 → 枚举。空/未知 → **跟随系统**; 带区域/下划线的写法 (`zh-CN`、`zh_Hans`) 都认:
    /// **先按完整键比** (这样 `zh-TW` 命中繁体行, 而不是被 primary 抢给简体), 再退到 primary 子标签。
    /// 永不抛。<paramref name="table"/> 只给探针用 (合成表验机制)。</summary>
    public static LangChoice Parse(string? value, LangDef[]? table = null)
    {
        var t = table ?? All;
        string raw = (value ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        if (raw.Length == 0) return LangChoice.System;
        if (raw is "system" or "auto") return LangChoice.System;
        var exact = t.FirstOrDefault(d => string.Equals(d.Key, raw, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact.Choice;
        string head = raw.Split('-')[0];
        return t.FirstOrDefault(d => string.Equals(d.Key, head, StringComparison.OrdinalIgnoreCase))?.Choice
               ?? LangChoice.System;
    }

    /// <summary>枚举 → json 值 (与 Parse 往返一致)。</summary>
    public static string ToKey(LangChoice choice)
        => choice == LangChoice.System ? "system" : DefOf(choice).Key;

    /// <summary>该选择是否需要"跟着系统走"。</summary>
    public static bool ShouldWatchSystem(LangChoice choice) => choice == LangChoice.System;

    /// <summary>Win32 LANGID → 语言。**纯函数**, 探针按表生成真值表 (+ 合成表验"简繁分流")。
    /// 表里都对不上 (日语/德语/俄语…) → <see cref="Fallback"/>。</summary>
    public static LangChoice FromLangId(ushort langId, LangDef[]? table = null)
        => ByLangId(langId, table)?.Choice ?? Fallback.Choice;

    /// <summary>系统显示语言 (用户自选的语言, 不是区域格式)。取不到就退当前进程 UI 文化的 LCID。</summary>
    public static LangChoice SystemLanguage()
    {
        try
        {
            return FromLangId(GetUserDefaultUILanguage());
        }
        catch
        {
            return FromLangId((ushort)System.Globalization.CultureInfo.CurrentUICulture.LCID);
        }
    }

    /// <summary>选择 + 系统实况 → **实际加载哪一本**。**纯函数**, 探针打真值表。</summary>
    public static LangChoice ResolveForApply(LangChoice choice, LangChoice systemLang)
        => choice == LangChoice.System ? systemLang : choice;

    // ---- 副作用 -----------------------------------------------------------

    /// <summary>当前**生效**的语言 (真实来源 = 我们装上去的那本字典; 启动时还没换过 = App.xaml 那本)。</summary>
    public static LangChoice Applied => _applied;

    private static LangChoice _applied = StartupDefault;

    /// <summary>启动期那一次 (必须在 ConfigStore.Load() **之后**、new MainWindow() 之前)。
    /// 此时还没有窗口 → 没有可见元素要刷, 只是把字典摆对, 所以不会闪一下中文再变英文。</summary>
    public static void ApplyAtStartup() => Apply(Current);

    /// <summary>应用语言 (用户改设置 / 启动 / 跟随系统变化都走这里)。
    /// 返回 **false** = 目标字典装不上 (文件没编进程序集 / URI 写错) —— 调用方**别写配置**并回退下拉;
    /// 否则界面还停在旧语言、配置却已是新语言, 下次启动又变回去 (像个鬼故事)。
    /// (zh/en 两本字典键集完全一致, 所以换字典不必担心中间态缺键。)</summary>
    public static bool Apply(LangChoice choice)
    {
        var want = ResolveForApply(choice, SystemLanguage());
        if (!SwapDictionary(want)) return false;
        LanguageChanged?.Invoke();
        return true;
    }

    /// <summary>语言变化后触发 —— XAML 侧已由 DynamicResource 自动刷新; 订阅者只需重建
    /// **code-behind 一次性赋值**的文案 (见文件头)。</summary>
    public static event Action? LanguageChanged;

    /// <summary>把 App.xaml 里那本文案字典换成目标语言的那本。**原地替换合并项** →
    /// 所有 `{DynamicResource}` 重新求值 (这就是"零跳变"的来源)。
    /// 返回 false = 没找到那本字典 (App.xaml 的合并行被挪走/改名了) 或目标字典装不上 ——
    /// 两种情况都会让语言切换**静默失效**, 所以探针有断言专门盯它们。</summary>
    public static bool SwapDictionary(LangChoice effective)
    {
        var dicts = Application.Current?.Resources.MergedDictionaries;
        if (dicts is null) return false;
        for (int i = 0; i < dicts.Count; i++)
        {
            string? uri = dicts[i].Source?.ToString();
            if (uri is null) continue;
            // 认法 = "在 Localization 目录下 + 是 .xaml" (与语言无关, 加语言不用改这里)
            if (!uri.Contains(DictFolder, StringComparison.OrdinalIgnoreCase)
                || !uri.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                continue;
            if (_applied == effective) return true;      // 已经是目标语言, 不重复重建字典实例
            try
            {
                dicts[i] = new ResourceDictionary { Source = new Uri(SourceOf(effective), UriKind.Absolute) };
            }
            catch
            {
                return false;   // 字典装不上 → 保持原样, 别把界面切成半边
            }
            _applied = effective;
            return true;
        }
        return false;
    }

    // ---- 跟随系统显示语言 ------------------------------------------------

    private static Window? _main;
    private static bool _hooked;
    private static HwndSource? _hookSource;
    private static DateTime _lastRefreshUtc;

    /// <summary>接线主窗口并开始跟随系统语言 (窗口创建后调用一次)。</summary>
    public static void Attach(Window main)
    {
        _main = main;
        if (_hooked) return;
        _hooked = true;
        // Windows 改显示语言会广播 WM_SETTINGCHANGE 且 lParam = "intl" —— 只有"跟随系统"才需要听
        // (固定语言与系统无关)。窗口消息与主题那套同源, 但**不能复用 ThemeService 的钩子**:
        // 它按 lParam 白名单过滤 (只认 ImmersiveColorSet), "intl" 会被它丢掉。
        HookWindowMessages(main);
    }

    /// <summary>退出时退订 (与 ThemeService.Detach 同理: 钩子挂在 HWND 上, 不摘会吊着窗口)。</summary>
    public static void Detach()
    {
        if (!_hooked) return;
        _hooked = false;
        _hookSource?.RemoveHook(WindowProc);
        _hookSource = null;
        _main = null;
    }

    private static void HookWindowMessages(Window main)
    {
        if (_hookSource is not null) return;
        if (PresentationSource.FromVisual(main) is HwndSource src)
        {
            _hookSource = src;
            src.AddHook(WindowProc);
            return;
        }
        // 窗口还没 Show (StartMinimized) 时没有 HWND → 等它创建出来再挂钩 (与 ThemeService 同款)。
        main.SourceInitialized += (_, _) =>
        {
            if (_hookSource is not null || !_hooked) return;
            if (PresentationSource.FromVisual(main) is not HwndSource s) return;
            _hookSource = s;
            s.AddHook(WindowProc);
        };
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        // 语言/区域变化发的是 WM_SETTINGCHANGE + lParam = "intl" (与主题的 ImmersiveColorSet 并列)
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero
            && string.Equals(Marshal.PtrToStringUni(lParam), "intl", StringComparison.Ordinal))
            RefreshFromSystem();
        return IntPtr.Zero;
    }

    /// <summary>系统语言变化 → 只有"跟随系统"时才真的换字典。</summary>
    private static void RefreshFromSystem()
    {
        var app = Application.Current;
        if (app == null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if ((DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds < 200) return;   // 去重
                _lastRefreshUtc = DateTime.UtcNow;
                if (!ShouldWatchSystem(Current)) return;
                var want = ResolveForApply(LangChoice.System, SystemLanguage());
                if (want == _applied) return;                  // 语言没变 → 不白换一次字典
                SwapDictionary(want);
                LanguageChanged?.Invoke();
            }
            catch
            {
                // 设置变更期间的偶发异常不值得让界面崩 —— 静默忽略, 下次变化会再试
            }
        });
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();
}

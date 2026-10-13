// Views/ModeCatalog.cs — 模式表 (**唯一事实源**): 模式名/说明 + 颜色归属 + 亮度语义 + 主页 UI 分区 + 指标集。
//
// 加一款固件特效 = 表里加一行 + XAML 磁贴 + 两个文案键 (探针断言"磁贴 ⇄ 表"双向一致)。
// 判定/UI/指标全部从这张表派生 —— 以前这些事实散在 9 个消费点 (算术闭集 IsMcuMode + 各处 switch),
// 加模式时**没有一处在编译期报错**, 只会静默把新模式当成"主机发帧模式"(亮度滑块可拖但没用、
// 指标集错、胶囊不显示、磁贴不存在、副标题还自称"固件端自带特效")。
//
// ⚠️ **为什么不是一个 ModeKind 枚举**: 消费点问的是**6 个互相独立的问题** ——
//   ① 指标语义 ② 有没有逐灯帧 ③ 亮度语义 ④ 是不是固件磁贴那种 UI 分区 ⑤ 有没有颜色可拾
//   ⑥ 辉光驱动量与光带色系。
// 单枚举只有当"每种取值在这 6 个投影上都不冲突"时才够用, 而 `Solid`(11 纯色) 就是反例:
// 它与 HostFrame 只在 ①⑤⑥ 上不同, ②③ 完全一样 —— 也就是说"颜色归属"与"UI 面"本来是两根轴。
// 拆成 3 个显式投影后, 新场景 (如厂商将来出"固件渲染但主机可传参数"的特效) 只是**填字段**,
// 不必新增枚举值、更不必回头重审所有消费点。
using AmbientLight.Localization;

namespace AmbientLight.Views;

/// <summary>颜色从哪来 —— 决定"有没有逐灯帧"(布局预览能不能画)与辉光驱动量。</summary>
public enum FrameKind
{
    /// <summary>主机逐灯发帧 (追光 / 纯色 / 将来的自创特效)。</summary>
    HostFrame,
    /// <summary>主机只发一个标量, 固件把它映射成颜色 (拾音的音量包络)。</summary>
    Scalar,
    /// <summary>固件完全自己渲染, 主机只发"选哪一款"。</summary>
    Firmware,
    /// <summary>不知道 —— 表外的模式号 (此版本还不认识它)。显式存在, 免得"未知"漏进别的分支假装是已知。</summary>
    Unknown,
}

/// <summary>亮度滑块在这个模式下的语义 (决定滑块可不可点 + 那一行说明文字)。</summary>
public enum BrightKind
{
    /// <summary>线性乘数 (gamma 之后)。</summary>
    Multiply,
    /// <summary>作为最高亮度上限 (拾音: 乘在音量包络上)。</summary>
    Cap,
    /// <summary>没有落点 → 滑块禁用并说明原因 (固件特效 / 未知模式)。</summary>
    None,
}

/// <summary>主页哪一块 UI 归它 —— 固件特效磁贴折叠头 / 「当前 XX」胶囊 / 纯色拾色器都由此决定。</summary>
public enum UiSection
{
    /// <summary>三张主模式卡那一类, 没有额外面板。</summary>
    Standard,
    /// <summary>固件特效磁贴 + 折叠头「当前 XX」胶囊。</summary>
    FirmwareFx,
    /// <summary>三张主模式卡 + 纯色拾色器。</summary>
    SolidPicker,
}

/// <summary>一行模式定义。</summary>
/// <param name="Id">固件协议里的模式号。</param>
/// <param name="Frame">颜色从哪来。</param>
/// <param name="Bright">亮度语义。</param>
/// <param name="Ui">主页 UI 分区。</param>
/// <param name="MetricKeys">调试指标集 —— 它是**数据**, 直接写数组, 不再 switch (键见 Metric.* 文案)。</param>
/// <param name="GlowTint">光带/辉光色系 (Blue/Green/Purple/Mono); 空 = 按 <paramref name="Frame"/> 取默认。</param>
/// <param name="DescKey">副标题文案键; 空 = 用 `Mode.Desc.Mcu` (固件特效共用一条)。
/// ⚠️ 非固件行**必须**自己给 (探针断言) —— 否则新的主机模式会顶着"固件端自带特效"的副标题。</param>
public sealed record ModeDef(
    int Id,
    FrameKind Frame,
    BrightKind Bright,
    UiSection Ui,
    string[] MetricKeys,
    string GlowTint = "",
    string DescKey = "");

public static class ModeCatalog
{
    /// <summary>★ **模式表 = 唯一事实源**。`MetricKeys` 见 `HomePage.MetricKeysForMode`。</summary>
    public static readonly ModeDef[] Modes =
    [
        new(0, FrameKind.HostFrame, BrightKind.Multiply, UiSection.Standard, ["proc", "wire", "fps"], "Blue", "Mode.Desc.0"),
        new(1, FrameKind.Scalar, BrightKind.Cap, UiSection.Standard, ["audio", "wire"], "Green", "Mode.Desc.1"),
        new(2, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(3, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(4, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(5, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(6, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(7, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(8, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(9, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(10, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(11, FrameKind.HostFrame, BrightKind.Multiply, UiSection.SolidPicker, ["wire", "leds", "bright"], "Purple", "Mode.Desc.11"),
        new(12, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        new(13, FrameKind.Firmware, BrightKind.None, UiSection.FirmwareFx, ["cmd", "wire", "leds"]),
        // 加新款固件特效: 这里加一行 (Frame=Firmware / Bright=None / Ui=FirmwareFx, 指标照抄上面那行)
        // + HomePage.xaml 的 FxPanel 加一个磁贴 + Mode.Name.<n> 与 Home.Fx.Tip.<n> 两个文案键。
    ];

    /// <summary>表外的模式号的兜底行 (固件报了一个此版本还不认识的特效)。**显式降级, 不撒谎**:
    /// · 指标只留 `wire`/`leds` —— 这两项任何模式下都真实 (会话在写串口、灯珠数是配置事实);
    ///   `proc/fps/audio/bright/cmd` 都是"我们猜的", 一律不给;
    /// · 亮度禁用 (原样放开会让用户拖了没用) + 专用说明文案;
    /// · 逐灯帧不画、固件磁贴/拾色器都不出 —— 我们不知道它属于哪一类;
    /// · 辉光给恒定中性微光 (呼吸在既有语义里绑定"固件在渲染", 未知时不该假装);
    /// · 副标题用 `Mode.Desc.Unknown`, 不再自称"固件端自带特效"。</summary>
    public static readonly ModeDef Unknown =
        new(-1, FrameKind.Unknown, BrightKind.None, UiSection.Standard, ["wire", "leds"], "Mono", "Mode.Desc.Unknown");

    /// <summary>全部模式号 —— **由表派生**, 别再写第二份清单 (以前这里是一串手写数字)。</summary>
    public static readonly int[] All = Modes.Select(m => m.Id).ToArray();

    /// <summary>该模式号这一版认识吗 (不认识 = 表外)。UI 用它决定要不要给"未知模式"提示。</summary>
    public static bool IsKnown(int mode) => Modes.Any(m => m.Id == mode);

    /// <summary>查表; 表外的号 → <see cref="Unknown"/> 兜底行 (调用点不必再判空)。</summary>
    public static ModeDef Of(int mode) => Modes.FirstOrDefault(m => m.Id == mode) ?? Unknown;

    /// <summary>模式名; 未知模式号回落到「未知 ({n})」。</summary>
    public static string Name(int mode)
    {
        string key = "Mode.Name." + mode;
        string s = Loc.T(key);
        return s == key ? Loc.F("Mode.Unknown", mode) : s;
    }

    /// <summary>一句话说明 (banner 副标题 / 模式卡副标题)。固件特效共用 `Mode.Desc.Mcu`。</summary>
    public static string Desc(int mode)
    {
        string key = Of(mode).DescKey;
        return Loc.T(key.Length > 0 ? key : "Mode.Desc.Mcu");
    }

    /// <summary>光带/辉光色系 —— 行里没写就按"颜色从哪来"取默认 (追光蓝 / 标量绿 / 中性灰)。</summary>
    public static string GlowTintOf(int mode)
    {
        var d = Of(mode);
        if (d.GlowTint.Length > 0) return d.GlowTint;
        return d.Frame switch
        {
            FrameKind.HostFrame => "Blue",
            FrameKind.Scalar => "Green",
            _ => "Mono",   // 固件 / 未知: 中性白灰 (琥珀留给"等待/警告"的状态色)
        };
    }
}

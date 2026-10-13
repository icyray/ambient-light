// Views/ModeCatalog.cs — 模式的显示名 / 一句话说明 (取使用的唯一入口)。
// 文案本体在 Localization/Strings.zh.xaml (M5.3); 这里只管"模式号 → 文案键"的映射,
// 例如加新款固件特效时: All 补一个号 + Strings.zh.xaml 补 Mode.Name.<n> (探针会数磁贴对账)。
using AmbientLight.Localization;

namespace AmbientLight.Views;

public static class ModeCatalog
{
    /// <summary>全部模式号 (固件协议定义; 0 追光 / 1 拾音 / 2~10、12、13 固件特效 / 11 纯色)。</summary>
    public static readonly int[] All = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];

    /// <summary>模式名; 未知模式号回落到「未知 ({n})」。</summary>
    public static string Name(int mode)
    {
        string key = "Mode.Name." + mode;
        string s = Loc.T(key);
        return s == key ? Loc.F("Mode.Unknown", mode) : s;
    }

    /// <summary>一句话说明 (banner 副标题 / 模式卡副标题)。</summary>
    public static string Desc(int mode) => mode switch
    {
        0 => Loc.T("Mode.Desc.0"),
        1 => Loc.T("Mode.Desc.1"),
        11 => Loc.T("Mode.Desc.11"),
        _ => Loc.T("Mode.Desc.Mcu"),
    };
}

// ThemePalette.cs — M5.2 语义色板 (深/浅两套) + 对比度工具
//
// 为什么需要两套: 原来那组"半透明底 + 浅色字"是为**深底**选的 (banner 渐变 / 深色卡片),
// 同一套颜色还被用在了卡片底上 (设备行/显示器行的胶囊) —— 浅色主题下就是"浅色字写在白卡上"。
// 所以按语义拆成 dark / light 两套, 由调用点按"这块底是深是浅"传入。
//
// 口径 (M5.2 §0 D1): **banner 点亮态恒深色** → banner 上的胶囊永远用深色套;
// 其余落在卡片上的胶囊按当前主题走 (ThemeService.IsDark)。
//
// 高对比度(HC): 不做适配, 按深色处理 (M5.2 §0 D3 —— 半吊子 HC 比给深色更糟)。
using System.Windows.Media;
using AmbientLight.Interop;

namespace AmbientLight.Services;

/// <summary>语义色调 (状态/含义, 不是具体颜色)。</summary>
public enum Tone
{
    Gray,
    Green,
    Amber,
    Blue,
    Red,
}

/// <summary>一个语义胶囊的配色: 半透明底 + 前景(字/圆点)色。</summary>
public readonly record struct ToneColors(Color Background, Color Foreground);

/// <summary>M5.2 语义色板与色学工具。两个色板表都是**纯函数**, 探针打真值表 + 对比度约束。</summary>
public static class ThemePalette
{
    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>胶囊配色。`dark = true` 用深底那套 (浅色字), 否则用浅底那套 (深色字)。</summary>
    public static ToneColors Of(Tone tone, bool dark) => dark
        ? tone switch
        {
            Tone.Green => new ToneColors(Color.FromArgb(0x2E, 0x2D, 0xC0, 0x8A), Rgb(0x8C, 0xE8, 0xC2)),
            Tone.Amber => new ToneColors(Color.FromArgb(0x2E, 0xF0, 0xA0, 0x2E), Rgb(0xF6, 0xCE, 0x92)),
            Tone.Blue => new ToneColors(Color.FromArgb(0x2E, 0x4C, 0x8D, 0xFF), Rgb(0xA9, 0xD8, 0xFF)),
            Tone.Red => new ToneColors(Color.FromArgb(0x2E, 0xE2, 0x4B, 0x4A), Rgb(0xF5, 0xB0, 0xAF)),
            _ => new ToneColors(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), Rgb(0xC9, 0xC9, 0xC9)),
        }
        : tone switch
        {
            // 浅底套取 Win11 状态色系 (字色压到能压在白卡上: 对比度见探针断言)
            Tone.Green => new ToneColors(Color.FromArgb(0x26, 0x0F, 0x6B, 0x0F), Rgb(0x0F, 0x6B, 0x0F)),
            Tone.Amber => new ToneColors(Color.FromArgb(0x26, 0x8A, 0x53, 0x00), Rgb(0x8A, 0x53, 0x00)),
            Tone.Blue => new ToneColors(Color.FromArgb(0x26, 0x0F, 0x5C, 0xA0), Rgb(0x0F, 0x5C, 0xA0)),
            Tone.Red => new ToneColors(Color.FromArgb(0x26, 0xB0, 0x28, 0x1A), Rgb(0xB0, 0x28, 0x1A)),
            _ => new ToneColors(Color.FromArgb(0x26, 0x00, 0x00, 0x00), Rgb(0x50, 0x50, 0x50)),
        };

    /// <summary>核心状态 → 圆点/文字色 (banner 胶囊用)。与旧 `HomePage.StateColors` 同表,
    /// 只是分成两套。传 `dark` 的口径见文件头。</summary>
    public static (Color Dot, Color Text) StateOf(int state, bool dark)
    {
        if (dark)
            return state switch
            {
                AmbientCore.StateRunning => (Rgb(0x2D, 0xC0, 0x8A), Rgb(0x8C, 0xE8, 0xC2)),
                AmbientCore.StateStarting => (Rgb(0x4C, 0x8D, 0xFF), Rgb(0xA9, 0xD8, 0xFF)),
                AmbientCore.StateWaitDisp => (Rgb(0xF0, 0xA0, 0x2E), Rgb(0xF6, 0xCE, 0x92)),
                AmbientCore.StateDevLost => (Rgb(0xF0, 0xA0, 0x2E), Rgb(0xF6, 0xCE, 0x92)),
                AmbientCore.StateError => (Rgb(0xE2, 0x4B, 0x4A), Rgb(0xF2, 0xA9, 0xA9)),
                _ => (Rgb(0x9A, 0x9A, 0x9A), Rgb(0xC9, 0xC9, 0xC9)),
            };

        return state switch
        {
            AmbientCore.StateRunning => (Rgb(0x0F, 0x6B, 0x0F), Rgb(0x0F, 0x6B, 0x0F)),
            AmbientCore.StateStarting => (Rgb(0x0F, 0x5C, 0xA0), Rgb(0x0F, 0x5C, 0xA0)),
            AmbientCore.StateWaitDisp => (Rgb(0x8A, 0x53, 0x00), Rgb(0x8A, 0x53, 0x00)),
            AmbientCore.StateDevLost => (Rgb(0x8A, 0x53, 0x00), Rgb(0x8A, 0x53, 0x00)),
            AmbientCore.StateError => (Rgb(0xB0, 0x28, 0x1A), Rgb(0xB0, 0x28, 0x1A)),
            _ => (Rgb(0x50, 0x50, 0x50), Rgb(0x50, 0x50, 0x50)),
        };
    }

    // ---- 色学工具 (探针用; 也是上面色板取值的依据) ------------------------

    /// <summary>把带 alpha 的 `fg` 叠在 `bg` 上 (标准 source-over), 结果不透明。
    /// 胶囊底色是半透明的, 要断言"字 vs 实际看到的底"就必须先合成。</summary>
    public static Color Blend(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        byte C(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(C(fg.R, bg.R), C(fg.G, bg.G), C(fg.B, bg.B));
    }

    /// <summary>WCAG 相对亮度 (0 黑 … 1 白)。</summary>
    public static double RelativeLuminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 对比度 (1.0 … 21.0)。</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}

#include "colorchain.h"
#include <algorithm>
#include <cmath>

namespace color {

struct HSL { double H, S, L; };   // 均 [0,1]; System.Drawing GetHue/GetSaturation/GetBrightness 语义

static HSL ToHSL(double r, double g, double b)
{
    double mx = std::max(r, std::max(g, b));
    double mn = std::min(r, std::min(g, b));
    double c = mx - mn;
    HSL hsl{ 0, 0, (mx + mn) / 2.0 };
    if (c > 0) {
        hsl.S = (mx + mn <= 1.0) ? c / (mx + mn) : c / (2.0 - mx - mn);
        double h;
        if      (mx == r) h = (g - b) / c;
        else if (mx == g) h = (b - r) / c + 2.0;
        else              h = (r - g) / c + 4.0;
        h /= 6.0;
        if (h < 0) h += 1.0;
        hsl.H = h;
    }
    return hsl;
}

static void HSLtoRGB(const HSL& h, double out[3])
{
    if (h.L <= 0) { out[0] = out[1] = out[2] = 0; return; }
    if (h.S <= 0) { out[0] = out[1] = out[2] = h.L; return; }
    double q = (h.L <= 0.5) ? h.L * (1.0 + h.S) : h.L + h.S - h.L * h.S;
    double p = 2.0 * h.L - q;
    auto f = [&](double t) -> double {
        t = fmod(t, 1.0); if (t < 0) t += 1.0;
        if (6.0 * t < 1.0) return p + (q - p) * 6.0 * t;
        if (2.0 * t < 1.0) return q;
        if (3.0 * t < 2.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
        return p;
    };
    out[0] = f(h.H + 1.0 / 3.0);
    out[1] = f(h.H);
    out[2] = f(h.H - 1.0 / 3.0);
}

static double clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }

void AdjustLedColors(const Params& p, double r, double g, double b, uint8_t out[3])
{
    HSL hsl = ToHSL(r, g, b);
    if (p.colorfix) {
        // 低灰保真: 绝对色度 (8bit max-min) 噪声门限 —— HSL 的 S 是相对值,
        // 暗色(如 #0D1117) 差值 10/255 也有 S=0.28, 会被鲜艳度放大成纯蓝;
        // 绝对色度 < 门限视为采样噪声钳为灰, 门限上线性归一后再过鲜艳度
        double c8 = (std::max(std::max(r, g), b) - std::min(std::min(r, g), b)) * 255.0;
        double s = (c8 <= p.sfloor) ? 0.0 : (c8 - p.sfloor) / (255.0 - p.sfloor);
        if (p.vibrance != 1.0) s = clamp01(s * p.vibrance);
        hsl.S = s;
    } else if (p.vibrance != 1.0) {
        hsl.S = clamp01(hsl.S * p.vibrance);   // 厂商原链
    }
    if (p.hueMul     != 1.0) hsl.H = clamp01(hsl.H * p.hueMul);
    // 亮度 = 真调光 (M5.1 §8.1)。**不要**再写 hsl.L *= brightness:
    // HSL 的 L 是 max/min 通道的中点、S 是相对量, 把 L 砍半等于把颜色从"浅色调区"推进"饱和色区"
    // (暖白 #FFE1D6 @50% 会变成饱和红 #E51E00)。改为在 HSL 链之后对最终 8bit 值线性缩放 —— 色相/饱和度
    // 不变, 只是暗一档。拾音模式不走本函数 (brightness 本来就在音量包络上做线性乘数)。
    double c[3];
    HSLtoRGB(hsl, c);
    for (int i = 0; i < 3; i++) {
        double v = c[i] * 255.0;
        if (p.gamma != 1.0) v = 255.0 * pow(v / 255.0, 1.0 / p.gamma);
        if (i == 0) v *= p.rGain; else if (i == 1) v *= p.gGain; else v *= p.bGain;
        if (p.brightness != 1.0) v *= p.brightness;
        c[i] = std::min(255.0, std::max(0.0, v));
    }
    int r8 = (int)c[0], g8 = (int)c[1], b8 = (int)c[2];
    if (r8 < p.cut && g8 < p.cut && b8 < p.cut) { r8 = g8 = b8 = 0; }
    if (r8 < p.minb && g8 < p.minb && b8 < p.minb) { r8 = g8 = b8 = p.minb; }
    int ord[3];
    switch (p.output) {
        case 1:  ord[0]=g8; ord[1]=b8; ord[2]=r8; break;   // GBR
        case 2:  ord[0]=b8; ord[1]=r8; ord[2]=g8; break;   // BRG
        case 3:  ord[0]=r8; ord[1]=b8; ord[2]=g8; break;   // RBG
        case 4:  ord[0]=g8; ord[1]=r8; ord[2]=b8; break;   // GRB
        case 5:  ord[0]=b8; ord[1]=g8; ord[2]=r8; break;   // BGR
        default: ord[0]=r8; ord[1]=g8; ord[2]=b8; break;   // RGB
    }
    out[0] = (uint8_t)ord[0]; out[1] = (uint8_t)ord[1]; out[2] = (uint8_t)ord[2];
}

} // namespace color

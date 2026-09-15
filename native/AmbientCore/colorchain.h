// colorchain.h — 厂商颜色链 (HslColor.cs + AdjustLedColors 忠实移植, PoC 已验证)
#pragma once
#include <stdint.h>

namespace color {

struct Params {
    double brightness = 1.0;   // 线性调光乘数 (HSL 链之后, 最终 8bit 值上线性缩放; M5.1 §8.1)
    double vibrance   = 9.0;   // HSL S 乘数
    double hueMul     = 1.0;   // HSL H 乘数
    double gamma      = 0.65;
    double rGain = 1.0, gGain = 1.0, bGain = 1.0;
    int    cut   = 1;          // 全通道低于 cut 置黑
    int    minb  = 1;          // 全通道低于 minb 抬到 minb
    int    output = 0;         // 0=RGB 1=GBR 2=BRG 3=RBG 4=GRB 5=BGR
    int    colorfix = 0;       // 0=厂商原链 1=绝对色度噪声门限 (低灰保真)
    double sfloor = 24.0;      // colorfix 门限 (8bit max-min)
};

// 输入 r,g,b ∈ [0,1]; 输出按 output 色序排好的 3 字节
void AdjustLedColors(const Params& p, double r, double g, double b, uint8_t out[3]);

} // namespace color

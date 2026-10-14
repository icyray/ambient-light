// led_rate_probe.cpp — 灯带刷新率诊断 (UFO-test 风格)
//
// 一个光团沿灯带匀速绕圈, 以**受控的发送帧率**发 "Ada" 定长帧 (543B; 直发原始字节, 不过颜色链)。
// 用途: 找出灯带 (USB CDC + 固件) 能吃下的最大帧率 —— 超过后会出现可见抖动/闪烁,
//       据此决定 FpsCap 的 UI 上限/默认值 (2026-10-14 用户报「>60Hz 稳定抖」后引入)。
//
// 关键设计:
//   · 光团相位按**时间**推进 (灯/秒), 与帧率无关 → 各档位线速度恒定, 同一基线做视觉对比;
//   · 发送时刻 = QPC 计算 + Sleep/自旋混合等待 (抖动 <0.1ms 量级), timeBeginPeriod(1);
//   · 统计: 实际达成帧率 / 发送间隔 (均值·标准差·最大) / 推迟量 / **单帧写入耗时** ——
//     写入耗时是"USB/驱动侧是否限流"的直接指标, 与"固件渲染侧抖动"区分开。
//
// 用法 (需先退出 AmbientLight —— 它独占串口):
//   led_rate_probe.exe <fps> [seconds]            单档, 默认 10 秒
//   led_rate_probe.exe --scan 30,60,120,240 [--dwell 5]
//   选项: --leds 60  --speed 10 (灯/秒)  --bright 255  --sigma 1.5
//         --port COM5  --baud 2000000  --static (光团不动)  --no-mark
//         --dry (不开串口, 自检时序与相位)  --help
//
// 观察建议:
//   · 光团边缘/亮度出现"毛刺、闪烁、变暗、拖影糊化"的那一档, 就是灯带的上限附近;
//   · 每档开始会整带快闪 1 次蓝 (--no-mark 关闭), 用来对应控制台打印的档位;
//   · 想看得更从容可把 --speed 调小 (如 6-8), 光团走得更慢。
#include "serial.h"
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cmath>
#include <cstdlib>
#include <string>
#include <vector>
#include <algorithm>
#include <windows.h>

#pragma comment(lib, "winmm.lib")

// ---------------------------------------------------------------- 时钟
static LARGE_INTEGER g_qpf{};
static double NowS()
{
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)g_qpf.QuadPart;
}
/// 等到目标时刻 (QPC 秒): 粗段 Sleep, 尾段自旋 —— 目标抖动 <0.1ms 量级
static void WaitUntil(double tTarget)
{
    for (;;) {
        double remain = tTarget - NowS();
        if (remain <= 0) return;
        if (remain > 0.0020)      Sleep((DWORD)((remain - 0.0015) * 1000.0));
        else if (remain > 0.0005) Sleep(0);
        else                      YieldProcessor();
    }
}

// ---------------------------------------------------------------- 帧构建
// "Ada" + 180*3; leds 之后补零。光团 = 环形高斯 (灯带首尾相接), 峰在 center (浮点灯位)。
static void BuildOrbFrame(std::vector<uint8_t>& f, int leds, double center, double sigma, int bright)
{
    if (f.size() != 543) f.assign(543, 0);
    std::fill(f.begin() + 3, f.end(), 0);
    f[0] = 'A'; f[1] = 'd'; f[2] = 'a';
    for (int i = 0; i < leds && i < 180; i++) {
        double d = std::fabs((double)i - center);
        if (d > leds * 0.5) d = leds - d;                          // 环形距离
        double g = std::exp(-(d * d) / (2.0 * sigma * sigma));
        int v = (int)(g * (double)bright + 0.5);
        if (v > 255) v = 255;
        f[3 + i * 3 + 0] = (uint8_t)v;
        f[3 + i * 3 + 1] = (uint8_t)v;
        f[3 + i * 3 + 2] = (uint8_t)v;
    }
}
static void BuildSolidFrame(std::vector<uint8_t>& f, int leds, int r, int g, int b)
{
    if (f.size() != 543) f.assign(543, 0);
    f[0] = 'A'; f[1] = 'd'; f[2] = 'a';
    for (int i = 0; i < leds && i < 180; i++) {
        f[3 + i * 3 + 0] = (uint8_t)r; f[3 + i * 3 + 1] = (uint8_t)g; f[3 + i * 3 + 2] = (uint8_t)b;
    }
}

// ---------------------------------------------------------------- 统计
struct RunStats
{
    double fps = 0;            // 目标帧率
    uint64_t sent = 0;
    double achieved = 0;       // 实际达成 (按"最后一帧应有周期"归一)
    double iAvg = 0, iSd = 0, iMax = 0;   // 发送间隔 ms (实际时刻之间)
    double lateMax = 0, lateAvg = 0;      // 推迟量 ms (实际 - 目标)
    double writeAvg = 0, writeMax = 0;    // 单帧 WriteAll 耗时 ms
    uint64_t writeSlow = 0;               // >10ms 的写次数
    bool writeFail = false;
};

static RunStats RunRate(serial::SerialPort* sp, std::vector<uint8_t>& fr,
                        double fps, double durSec, double runT0, double speed,
                        int leds, double sigma, int bright, bool stat, bool dry)
{
    RunStats st;
    st.fps = fps;
    double t0 = NowS();
    double prevTc = 0, sumI = 0, sumI2 = 0, sumLate = 0, sumW = 0;
    for (uint64_t n = 0; ; n++) {
        double tTarget = t0 + (double)n / fps;
        if (tTarget - t0 >= durSec) break;
        // 相位按"目标时刻"算 (跨档连续, 与抖动无关); 构建放在等待前 → 发送时刻更准
        double center = stat ? leds * 0.5 : std::fmod((tTarget - runT0) * speed, (double)leds);
        BuildOrbFrame(fr, leds, center, sigma, bright);
        WaitUntil(tTarget);
        double tc = NowS();
        double w0 = NowS();
        bool ok = true;
        if (!dry)
            ok = sp->WriteAll(fr.data(), (uint32_t)fr.size(), 500, nullptr, nullptr);
        double w1 = NowS();
        if (!ok) { st.writeFail = true; break; }
        // 统计
        if (n > 0) {
            double iv = (tc - prevTc) * 1000.0;
            sumI += iv; sumI2 += iv * iv;
            if (iv > st.iMax) st.iMax = iv;
        }
        double late = (tc - tTarget) * 1000.0;
        sumLate += late;
        if (late > st.lateMax) st.lateMax = late;
        double wms = (w1 - w0) * 1000.0;
        sumW += wms;
        if (wms > st.writeMax) st.writeMax = wms;
        if (wms > 10.0) st.writeSlow++;
        prevTc = tc;
        st.sent++;
    }
    st.achieved = st.sent > 0 ? (double)st.sent / ((prevTc - t0) + 1.0 / fps) : 0;
    if (st.sent > 1) {
        double n1 = (double)(st.sent - 1);
        st.iAvg = sumI / n1;
        double var = sumI2 / n1 - st.iAvg * st.iAvg;
        st.iSd = var > 0 ? std::sqrt(var) : 0;
    }
    st.lateAvg = st.sent > 0 ? sumLate / (double)st.sent : 0;
    st.writeAvg = st.sent > 0 ? sumW / (double)st.sent : 0;
    return st;
}

static void PrintStats(const RunStats& st)
{
    printf("  %6.1f Hz | sent %6llu | achieved %7.2f Hz | int avg %7.3f sd %6.3f max %7.3f ms"
           " | late avg %6.3f max %6.3f ms | write avg %6.3f max %7.3f slow %llu%s\n",
           st.fps, (unsigned long long)st.sent, st.achieved,
           st.iAvg, st.iSd, st.iMax, st.lateAvg, st.lateMax,
           st.writeAvg, st.writeMax, (unsigned long long)st.writeSlow,
           st.writeFail ? "  << WRITE FAILED/STUCK" : "");
}

static void Usage()
{
    printf("led_rate_probe — 灯带刷新率诊断 (光团绕圈, 可控发送帧率)\n"
           "用法:\n"
           "  led_rate_probe.exe <fps> [seconds]            单档测试 (默认 10 秒)\n"
           "  led_rate_probe.exe --scan 30,60,120,240       逐档扫描 (每档 --dwell 秒)\n"
           "  (不带任何帧率参数时默认扫描 30,60,90,120,144,165,200,240,300 九档)\n"
           "选项:\n"
           "  --scan r1,r2,...  扫描多个帧率: 逗号分隔列表, 每档持续 --dwell 秒后自动切下一档\n"
           "  --dwell N         扫描模式每档持续秒数 (默认 5)\n"
           "  --leds N          灯数 (默认 60)\n"
           "  --speed N         光团速度, 灯/秒 (默认 10)\n"
           "  --bright N        光团亮度 0-255 (默认 255)\n"
           "  --sigma X         光团宽度 (默认 1.5 灯)\n"
           "  --port NAME       串口 (默认 COM5)\n"
           "  --baud N          波特率 (默认 2000000)\n"
           "  --static          光团静止 (测纯频率下的稳定性, 不扫动)\n"
           "  --no-mark         关闭每档开始时的蓝闪标记\n"
           "  --out FILE        报告写入文件 (UTF-8; 建议用它代替 shell 的 > 重定向 ——\n"
           "                    PowerShell 的 > 会让中文双重编码失真, cmd 的相对安全)\n"
           "  --dry             不开串口, 自检时序/相位/统计\n"
           "  --help            本说明\n"
           "注意: 先退出 AmbientLight (它独占串口); 每档开始的整带蓝闪 = 档位标记。\n");
}

int main(int argc, char** argv)
{
    QueryPerformanceFrequency(&g_qpf);
    std::vector<double> rates;
    double singleSecs = 10.0, dwell = 5.0;
    int leds = 60, bright = 255;
    double speed = 10.0, sigma = 1.5;
    std::string port = "COM5", outPath;
    uint32_t baud = 2000000;
    bool stat = false, dry = false, mark = true;

    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        if (a == "--help" || a == "-h") { SetConsoleOutputCP(CP_UTF8); Usage(); return 0; }
        else if (a == "--scan" && i + 1 < argc) {
            std::string s = argv[++i];
            size_t p = 0;
            while (p < s.size()) {
                size_t q = s.find(',', p);
                if (q == std::string::npos) q = s.size();
                double v = atof(s.substr(p, q - p).c_str());
                if (v > 0) rates.push_back(v);
                p = q + 1;
            }
        }
        else if (a == "--dwell" && i + 1 < argc) dwell = atof(argv[++i]);
        else if (a == "--leds" && i + 1 < argc) leds = atoi(argv[++i]);
        else if (a == "--speed" && i + 1 < argc) speed = atof(argv[++i]);
        else if (a == "--bright" && i + 1 < argc) bright = atoi(argv[++i]);
        else if (a == "--sigma" && i + 1 < argc) sigma = atof(argv[++i]);
        else if (a == "--port" && i + 1 < argc) port = argv[++i];
        else if (a == "--baud" && i + 1 < argc) baud = (uint32_t)atol(argv[++i]);
        else if (a == "--out" && i + 1 < argc) outPath = argv[++i];
        else if (a == "--static") stat = true;
        else if (a == "--no-mark") mark = false;
        else if (a == "--dry") dry = true;
        else {
            double v = atof(a.c_str());
            if (v > 0) {
                rates.push_back(v);
                if (i + 1 < argc && argv[i + 1][0] != '-') singleSecs = atof(argv[++i]);
            }
        }
    }
    // --out: 由本程序自己以 UTF-8 写文件, 绕开 shell 重定向的编码变换 —— PowerShell 的 ">"
    // 会先把子进程输出按 [Console]::OutputEncoding (中文系统 = GBK) 解码、再按 UTF-16 写盘,
    // 中文双重失真 (实测 rate_scan.txt 全是 U+93BB 一类的 mojibake)。指定后控制台无输出。
    if (!outPath.empty() && !freopen(outPath.c_str(), "wb", stdout)) {
        fprintf(stderr, "[err] 无法写入 %s\n", outPath.c_str());
        return 2;
    }
    // 中文乱码修复: Windows 控制台默认代码页 (GBK) 会把 UTF-8 输出字节误读 → 切到 UTF-8。
    // (重定向到文件时本调用无副作用; --out 的文件内容即 UTF-8)
    SetConsoleOutputCP(CP_UTF8);
    if (rates.empty()) {
        // 默认扫描: UI 现有档位 + 边界
        double d[] = { 30, 60, 90, 120, 144, 165, 200, 240, 300 };
        rates.assign(d, d + sizeof(d) / sizeof(d[0]));
        printf("[scan] 未指定档位, 使用默认: ");
        for (size_t i = 0; i < rates.size(); i++) printf("%g%s", rates[i], i + 1 < rates.size() ? "," : "\n");
    }
    bool scanning = rates.size() > 1;
    leds = std::clamp(leds, 1, 180);

    timeBeginPeriod(1);

    serial::SerialPort sp;
    if (!dry) {
        if (!sp.Open(std::wstring(port.begin(), port.end()), baud)) {
            printf("[err] 无法打开 %s —— 请先退出 AmbientLight (它独占串口), 或用 --port 指定。\n", port.c_str());
            timeEndPeriod(1);
            return 2;
        }
        uint8_t resp = 0;
        if (!serial::Handshake(sp, resp)) {
            printf("[err] %s 无握手应答 (不是灯带? 线没插好?)。\n", port.c_str());
            sp.Close();
            timeEndPeriod(1);
            return 2;
        }
        printf("[ok] %s 握手 0x%02X (type %d id %d), leds=%d %s\n",
               port.c_str(), resp, (resp >> 2) & 3, resp & 3, leds,
               stat ? "(static)" : "");
    } else {
        printf("[dry] 不开串口 —— 仅自检时序/相位/统计\n");
    }

    std::vector<uint8_t> fr(543, 0);
    double runT0 = NowS();
    std::vector<RunStats> all;

    // dry 档位样本检查: 打印相位推进的正确性 (第 0/1/末帧)
    for (size_t k = 0; k < rates.size(); k++) {
        double fps = rates[k];
        double dur = scanning ? dwell : singleSecs;
        printf("=== %g Hz (%.1fs) ===\n", fps, dur);
        fflush(stdout);
        // 档位标记: 整带快闪蓝 ×3 (与"光团"区分; --no-mark 关闭)
        if (mark && !dry) {
            BuildSolidFrame(fr, leds, 0, 64, 255);
            for (int m = 0; m < 3; m++) {
                sp.WriteAll(fr.data(), (uint32_t)fr.size(), 300, nullptr, nullptr);
                Sleep(30);
            }
        }
        RunStats st = RunRate(&sp, fr, fps, dur, runT0, speed, leds, sigma, bright, stat, dry);
        PrintStats(st);
        if (dry) {
            // 相位抽查: 头两帧 + 1 秒处的帧 (验证环形与匀速)
            double c0 = stat ? leds * 0.5 : std::fmod((0.0) * speed, (double)leds);
            double c1 = stat ? leds * 0.5 : std::fmod((1.0 / fps) * speed, (double)leds);
            double cS = stat ? leds * 0.5 : std::fmod(1.0 * speed, (double)leds);
            printf("  [dry] phase: t=0 -> center=%.2f (peak %d) | t=1/fps -> %.2f (peak %d) | t=1s -> %.2f (peak %d)\n",
                   c0, ((int)std::lround(c0)) % leds, c1, ((int)std::lround(c1)) % leds,
                   cS, ((int)std::lround(cS)) % leds);
        }
        all.push_back(st);
        if (st.writeFail) { printf("[err] 写入失败/卡死, 终止。\n"); break; }
        fflush(stdout);
    }

    if (!dry) {
        BuildSolidFrame(fr, leds, 0, 0, 0);            // 收尾: 灭
        sp.WriteAll(fr.data(), (uint32_t)fr.size(), 500, nullptr, nullptr);
        sp.Close();
    }
    printf("---- 汇总 ----\n");
    for (const auto& st : all) PrintStats(st);
    printf("观察: 从哪一档开始出现 光团边缘毛刺/整体闪烁/亮度变暗 —— 即灯带有效上限附近。\n"
           "若某档 achieved 明显 < 目标 或 write 列变大, 说明是 USB/驱动侧限流; achieved 正常\n"
           "但仍抖, 则是固件渲染侧的限制。\n");
    timeEndPeriod(1);
    return 0;
}

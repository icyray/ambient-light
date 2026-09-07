// audio.h — WASAPI 回环拾音 (M3): 默认输出端点事件驱动捕获 + 自研包络算法
//
// 设计:
//   - 进程内单例音源: 至多一条捕获线程, 多设备会话共享同一包络快照
//   - 线程内拥有全部 COM 对象 (MTA); 端点失效/默认输出切换 → 原地重建 (500ms 退避)
//   - 输出 EnvelopeByte(): 已过 AGC+门限+快起慢落包络的归一值 0..255,
//     未乘灵敏度/主音量 — 会话侧再乘各自 audioGain (与可选主音量)
//   - 无音频包 (停止播放) > 40ms → 包络按 release 衰减到 0
//     (厂商"队列空冻结最后一帧"的缺陷不复制)
//
// 算法 (对 ~10ms 混音包, float32):
//   1. 全频能量 = 通道峰值 |x| 的 RMS (避免 L/R 反相抵消)
//      低频能量 = 均值单声道过 120Hz 双一阶低通的 RMS
//   2. 慢速峰值归一 (AGC, tau≈5s, 地板 0.02): 各自归一到 [0,1]
//   3. 混合 = (1-bass)*normFull + bass*normBass
//   4. 绝对底噪门限 (AGC 之前的原始 fullRMS, 防底噪被 AGC 放大) → 0
//   5. 柔和度整形 (soft, 仅有声段): gamma 1→0.5 抬中段 + 呼吸下限 0→15%
//   6. 包络: 起音平滑 (10-100ms 随 soft), 慢落 (audioRelease 时间常数)
#pragma once
#include <windows.h>
#include <atomic>
#include <cstdint>
#include <string>
#include <thread>

namespace audio {

struct Params {
    double release = 0.30;    // 包络回落时间常数 (s), 0.05..1.0
    double gate    = 0.003;   // 底噪门限 (原始全频 RMS), 0..0.1
    double bass    = 0.35;    // 低频加权 0..1 (0=全频, 1=纯低频)
    bool   agc     = true;    // 慢速峰值归一 (安静音源自动抬升)
    double soft    = 0.5;     // 律动柔和度 0..1: 起音 10-100ms / gamma 1→0.5 / 呼吸下限 0-15%
};

class Meter {
public:
    Meter() = default;
    ~Meter();
    Meter(const Meter&) = delete;
    Meter& operator=(const Meter&) = delete;

    // 幂等; 启动捕获线程 (设备不可用不失败 — 见 Healthy/重建循环)
    void Start();
    void Stop();                                    // 幂等
    bool Running() const { return running_.load(std::memory_order_acquire); }

    void SetParams(const Params& p);                // 任意线程, 下一包生效
    void SetSource(bool microphone);                // 任意线程: false=回环(默认) true=麦克风; 变更触发重建
    void SetTargetDevice(const char* endpointId);   // 任意线程: UTF-8 端点 ID, 空=跟随默认; 变更触发重建
    uint8_t EnvelopeByte() const { return envByte_.load(std::memory_order_relaxed); }
    float   MasterVolume() const { return masterVol_.load(std::memory_order_relaxed); }
    int     Healthy() const { return healthy_.load(std::memory_order_relaxed); }  // 0=停 1=正常 2=重建中
    std::string EndpointName() const;               // UTF-8, 任意线程

private:
    void ThreadLoop();
    void ProcessPacket(const uint8_t* data, uint32_t frames, uint32_t blockAlign,
                       uint32_t channels, bool silent, double dtMs);
    void StepEnvelope(double target, double dtMs);

    // ---- 捕获线程私有 (仅该线程访问, 无需加锁) ----
    double aLp_ = 0.9843;       // 120Hz 一阶系数 (会话建立时按 fs 重算)
    double lp1_ = 0, lp2_ = 0;  // 低频链状态
    double peakF_ = 0.02;       // AGC 慢速峰值 (全频/低频)
    double peakB_ = 0.006;
    double env_ = 0;            // 包络 0..1
    Params live_{};             // 线程内参数副本

    // ---- 线程/句柄 ----
    std::thread thread_;
    HANDLE evStop_ = nullptr;                       // manual-reset
    HANDLE evCapture_ = nullptr;                    // WASAPI 事件 (auto-reset)
    std::atomic<bool> running_{ false };

    // ---- 跨线程 ----
    CRITICAL_SECTION paramsCs_{};                   // params_/wantId_ 写 (Set) / 读 (线程)
    Params params_{};
    std::string wantId_;                            // 目标端点 ID (UTF-8, 空=默认)
    std::atomic<int> wantMic_{ 0 };                 // 0=回环 1=麦克风 (SetSource 写)
    std::atomic<uint8_t> envByte_{ 0 };             // 输出包络 0..255
    std::atomic<float>   masterVol_{ 1.0f };
    std::atomic<int>     healthy_{ 0 };             // 0=停 1=正常 2=重建中
    CRITICAL_SECTION nameCs_{};                     // endpointName_
    std::string endpointName_;
};

} // namespace audio

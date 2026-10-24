// AmbientCore.cpp — 导出 + 核心线程 (可靠性状态机) + 设备监视线程 + 电源事件
//
// 线程模型 (v1.3): 1 条设备监视线程 (全局) + 每会话 1 条核心流线程 (最多 8 slot)
//                  + 拾音模式时 1 条 WASAPI 回环捕获线程 (进程单例, 多会话共享)。
//                  全部重建路径在各自核心线程内串行执行 (厂商双端口争用的教训)。
//                  电源回调只置原子标志。
//
// 状态机 (API 文档 3.1/3.6): 采集侧任何故障绝不传导为串口断开;
//   WAIT_DISP  = 采集暂不可用 (锁屏/拓扑切换/独占全屏/熄屏), 串口保持, 500ms 重试
//   DEV_LOST   = 串口侧故障, 恢复后按 VID/PID 身份跟随 (COM 号可变), 500ms×5 → 2s
//   ERROR      = D3D/资源硬错误, 5s 慢速重试
//
// 多会话 (M3): AC_Start/Stop/SetConfig/GetStatus = slot 0 的兼容别名;
//   AC_*Ex(slot, ...) 按槽位独立启停。监视线程全局共享, 每会话独立重连。
#include "AmbientCore.h"
#include "capture.h"
#include "colorchain.h"
#include "serial.h"
#include "audio.h"
#include "log.h"

#include <windows.h>
#include <initguid.h>
#include <powrprof.h>
#include <mmdeviceapi.h>
#include <wrl/client.h>
#include <atomic>
#include <algorithm>
#include <chrono>
#include <cstring>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "powrprof.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "ole32.lib")

using namespace std::chrono;
using Microsoft::WRL::ComPtr;

namespace {

// PKEY_Device_FriendlyName (与 audio.cpp 同款本地定义, 避免 initguid 顺序耦合)
static const PROPERTYKEY kPKEY_Name = { { 0xa45c254e, 0xdf1c, 0x4efd,
                                          { 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0 } }, 14 };

constexpr uint32_t kVersion = (1u << 16) | (6u << 8) | 0u;   // 1.6.0 (ACConfig 增补 chaseSmoothMode)

// ================= 淡入淡出 / 平滑参数 (要微调就改这一块) =================
// 观感含义: 淡入 = 灯"亮起来"的过程, 淡出 = 灯"灭下去"的过程。调大 = 更慢更柔, 调小 = 更干脆。
// 详见 docs/PROJECT.md §6 (输出包络与追光平滑)。
constexpr double kFadeInMs        = 400.0;   // 开灯: 0 → 目标亮度
constexpr double kFadeOutMs       = 300.0;   // 关灯: 当前 → 0 (会计入 Stop 的等待时间)
constexpr double kSuspendFadeMs   = 150.0;   // 系统睡眠/待机前: 更短! 进程随时可能被冻结,
                                             //   太长的淡出会被"冻在半亮" (实测 S0 低电量待机)
constexpr double kModeFadeMs      = 120.0;   // 切模式(追光/拾音/纯色 之间): 出/入各一段  [S3]
constexpr double kMcuFadeMs       = 150.0;   // 进 MCU 前淡出 / 出 MCU 后淡入            [S3]
constexpr double kWaitDispFadeMs  = 150.0;   // 进/出 WAIT_DISP: 调短的唯一理由是**睡眠风险** ——
                                             //   屏幕灭之后系统可能很快冻结进程, 250ms 会被冻在半亮
constexpr double kBrightSlewMs    = 250.0;   // 亮度限幅: 任意值走到任意值的最短时间      [S2]
constexpr double kChaseSmoothTauMs =  80.0;  // 追光「时域柔和」指数平均时间常数          [S4]
constexpr double kChaseFlashGuard  = 0.12;   // 追光「抑制闪光」单帧亮度突变上限比例      [S4]
// =========================================================================

// ⚠️ 下面这两条**不是观感参数**, 别和上面那组混: 它们是串口写策略 (见 CoreLoopInner 的 WriteDurable)。
//   含义 = "允许设备最多多久不吃数据" —— 固件切模式/收尾特效时会有静默期, 在预算内一律**补写等待**,
//   超了才判掉线 (2026-10-09: 切出 MCU 特效后误判"正在重连"就是这里)。
//   实测依据 (22:55~22:56 日志): 切出后 ~103ms 写失败; 重连时**+716ms 的握手还失败**, +1272ms 才通
//   ⇒ 设备的静默期 ≈0.8~1.2s ⇒ 预算取 3s 留足余量。**停止不受影响**: WriteAll 每轮补写前查 evStop ✓
constexpr double kWriteBudgetMs = 3000.0;
constexpr uint32_t kFadeWriteBudgetMs = 600;   // 淡变逐帧写: 预算更短 (淡变丢一帧无所谓, 半帧不行)

/// 毫秒时钟 (steady_clock 起点的偏移) —— 淡入淡出/限幅都用它算时间。
inline double Millis()
{
    return duration_cast<duration<double, std::milli>>(steady_clock::now().time_since_epoch()).count();
}

/// **输出包络**: 一个 0..1 的乘子, 只作用在"写出去的字节"上, **绝不写回配置**
/// (配置里的 brightness 永远是用户意图; 淡出只是"此刻输出多少")。
/// 曲线 SmoothStep 两端缓, 观感像"灯慢慢亮起来"。曲线形状刻意不做成配置。
struct Envelope
{
    double value = 1.0;                      // 当前乘子
    double from = 1.0, to = 1.0;
    double t0 = 0.0, dur = 0.0;              // 毫秒
    bool running = false;

    void Snap(double v) { value = from = to = v; running = false; }
    void StartTo(double target, double ms)
    {
        if (ms <= 0.0) { Snap(target); return; }
        from = value; to = target; dur = ms; t0 = Millis(); running = true;
    }
    void Step()
    {
        if (!running) return;
        double s = (Millis() - t0) / dur;
        if (s >= 1.0) { value = to; running = false; return; }
        if (s < 0.0) s = 0.0;
        value = from + (to - from) * (s * s * (3.0 - 2.0 * s));
    }
};

/// 交叉淡化: from → to 逐字节线性混合, 逐帧写出 (SmoothStep 两端缓)。
/// 用在"进 WAIT_DISP"—— 末帧内容 → policy 帧 (policy=保持时两者相同, 直接跳过)。
inline bool CrossFade(serial::SerialPort& sp, const std::vector<uint8_t>& from,
                      const std::vector<uint8_t>& to, std::vector<uint8_t>& scratch, double ms)
{
    if (!sp.IsOpen() || from.size() != to.size() || from.size() <= 3) return false;
    if (from == to) return true;                     // 目标 = 现状 → 不用过渡
    double t0f = Millis();
    while (true) {
        double s = (Millis() - t0f) / ms;
        if (s >= 1.0) break;
        if (s < 0.0) s = 0.0;
        double e = s * s * (3.0 - 2.0 * s);
        scratch.resize(from.size());
        for (size_t i = 0; i < 3; i++) scratch[i] = to[i];          // 头部(帧类型)以后者为准
        for (size_t i = 3; i < scratch.size(); i++)
            scratch[i] = (uint8_t)(from[i] * (1.0 - e) + to[i] * e + 0.5);
        if (!sp.WriteAll(scratch.data(), (uint32_t)scratch.size(), kFadeWriteBudgetMs))
            return false;                                        // 写不完 → 放弃淡变 (调用方决定)
        Sleep(16);
    }
    return true;
}

/// 一段淡出: 把 lastOut (最后一份"可淡"字节) 按 1→0 逐帧写出。
/// 只缩放**第 3 字节之后**的部分 —— 这样线帧("Ada"+RGB)、纯色帧("Adl")、
/// 拾音帧("Adz"+音量×3) 三种都自然覆盖; MCU 的 3 字节命令不在这里(它没有亮度通道)。
inline void FadeOutToBlack(serial::SerialPort& sp, const std::vector<uint8_t>& lastOut,
                           std::vector<uint8_t>& scratch, double ms)
{
    if (!sp.IsOpen() || lastOut.size() <= 3) return;
    double t0f = Millis();
    while (true) {
        double s = (Millis() - t0f) / ms;
        if (s >= 1.0) break;
        if (s < 0.0) s = 0.0;
        double e = 1.0 - s * s * (3.0 - 2.0 * s);
        scratch.assign(lastOut.begin(), lastOut.end());
        for (size_t i = 3; i < scratch.size(); i++)
            scratch[i] = (uint8_t)(scratch[i] * e + 0.5);
        if (!sp.WriteAll(scratch.data(), (uint32_t)scratch.size(), kFadeWriteBudgetMs)) break;
        Sleep(16);
    }
}

ACConfig DefaultConfig()
{
    ACConfig c{};
    c.structSize = sizeof(ACConfig);
    c.baudRate = 2000000;
    c.modeIndex = 0;
    c.monitorIndex = 0;
    c.ledCount = 60;
    c.sides = 4;
    c.direction = 0;
    c.captureRange = 15;
    c.taps = 8;
    c.fpsCap = 60;              // 帧率上限默认 60 (>60 有轻微抖动风险; 144 为实测硬上限)
    c.blackBars = 1;
    c.blackBarLevel = 3;
    c.brightness = 1.0;
    c.vibrance = 9.0;
    c.hueMul = 1.0;
    c.gamma = 0.65;
    c.rGain = c.gGain = c.bGain = 1.0;
    c.colorCut = 1;
    c.minBrightness = 1;
    c.outputOrder = 0;
    c.solidRgb[0] = 255; c.solidRgb[1] = 225; c.solidRgb[2] = 214;
    c.waitDispPolicy = 2;
    c.colorfix = 0;
    c.sfloor = 24.0;
    c.audioSource = 0;
    c.audioGain = 1.0;
    c.audioRelease = 0.30;
    c.audioGate = 0.003;
    c.audioBass = 0.35;
    c.audioAgc = 1;
    c.audioFollowVolume = 0;
    c.audioSoft = 0.5;
    c.chaseSmoothMode = 0;      // v1.6: 追光柔化平滑默认**关** (保持原样的高刷跟屏感)
    return c;
}

std::string WideToUtf8(const std::wstring& w)
{
    if (w.empty()) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), s.data(), n, nullptr, nullptr);
    return s;
}

std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty()) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), w.data(), n);
    return w;
}

// ---- 会话 (每 slot 一份) / 全局单例 ----------------------------------
struct Sess {
    std::thread thread;
    HANDLE evStop = nullptr;                    // manual-reset

    // 配置原子快照
    std::shared_mutex cfgMx;
    std::shared_ptr<const ACConfig> cfg;
    std::atomic<uint64_t> cfgSerial{ 1 };

    // 状态快照 (轮询读)
    std::shared_mutex stMx;
    ACStatus st{};
    char diag[256] = { 0 };

    std::atomic<int> active{ 0 };
    std::atomic<int> started{ 0 };              // 曾启动过 (区分"从未运行"的零状态)
};

struct Core {
    std::thread monitorThread;
    HANDLE evStopMon = nullptr;                 // manual-reset

    // 跨线程信号 (只置位, 各核心线程消费)
    std::atomic<uint32_t> deviceChanged{ 0 };   // 监视线程: VID/PID 端口集合变化
    std::atomic<uint32_t> topologyVer{ 0 };     // 监视线程: 显示器拓扑变化
    std::atomic<int> suspendEvt{ 0 }, resumeEvt{ 0 };
    std::atomic<int> monitorPower{ -1 };        // 1=亮 0=熄 -1=未知 (DPMS)
    std::atomic<int> testPattern{ 0 };          // 测试图: 施加于所有活动会话

    HPOWERNOTIFY hSuspend = nullptr, hMonPower = nullptr, hMonPower2 = nullptr;

    Sess slots[AC_MAX_DEVICES];

    int ActiveCount() const
    {
        int n = 0;
        for (auto& s : slots) if (s.active.load()) n++;
        return n;
    }
};

std::mutex g_lifeMx;
Core* g = nullptr;   // 一经创建不销毁 (Stop 后保留状态供 UI 查询)

// 拾音音源单例 (进程至多一条捕获线程, 多会话共享包络; users 引用计数)
std::mutex g_meterMx;
audio::Meter* g_meter = nullptr;
int g_meterUsers = 0;

void MeterAcquire()
{
    std::lock_guard lk(g_meterMx);
    if (!g_meter) g_meter = new audio::Meter();
    g_meterUsers++;
    g_meter->Start();
}

void MeterRelease()
{
    std::lock_guard lk(g_meterMx);
    if (--g_meterUsers <= 0) {
        g_meterUsers = 0;
        if (g_meter) g_meter->Stop();
    }
}

void MeterPushParams(const ACConfig& c)
{
    std::lock_guard lk(g_meterMx);
    if (g_meter && g_meter->Running()) {
        audio::Params p;
        p.release = c.audioRelease;
        p.gate = c.audioGate;
        p.bass = c.audioBass;
        p.agc = c.audioAgc != 0;
        p.soft = c.audioSoft;
        g_meter->SetParams(p);
    }
}

void MeterSetSource(const ACConfig& c)
{
    std::lock_guard lk(g_meterMx);
    if (g_meter) g_meter->SetSource(c.audioSource == 2);   // 2=麦克风, 其余=回环
}

void MeterSetTarget(const ACConfig& c)
{
    std::lock_guard lk(g_meterMx);
    if (g_meter) g_meter->SetTargetDevice(c.audioDevId, c.audioOutDevId);
}

void MeterRead(uint8_t* env, float* master)
{
    std::lock_guard lk(g_meterMx);
    if (g_meter && env) *env = g_meter->EnvelopeByte();
    if (g_meter && master) *master = g_meter->MasterVolume();
}

// 以下函数的 g 参数 = 会话指针 (CoreLoop 内以 g 影蔽命名, 全局事件仍经
// 全局 g 访问 — 见 CoreLoop 的调用约定)
bool WaitStop(Sess* g, DWORD ms) { return WaitForSingleObject(g->evStop, ms) == WAIT_OBJECT_0; }

std::shared_ptr<const ACConfig> SnapCfg(Sess* g)
{
    std::shared_lock lk(g->cfgMx);
    return g->cfg;
}

void SetPhase(Sess* g, int state)
{
    int prev;
    {
        std::unique_lock lk(g->stMx);
        prev = g->st.state;
        g->st.state = state;
    }
    // 相位时间线 (诊断锁屏/熄屏时序要看它); 放在锁外 —— 别在状态锁里做文件 I/O
    if (prev != state) ACLOGD("phase %d -> %d", prev, state);
}

// 记录最近诊断文本 (UI 经 AC_GetLastDiagEx 读取) 并落日志
void SetDiag(Sess* g, const char* lvl, const char* fmt, ...)
{
    char msg[256];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(msg, _TRUNCATE, fmt, ap);
    va_end(ap);
    ::alog::Write(lvl, "%s", msg);
    std::unique_lock lk(g->stMx);
    strncpy_s(g->diag, msg, _TRUNCATE);
}

// SEH 过滤器: 线程内未处理异常记入日志 (M4 的崩溃兜底提前到这里)
LONG CrashFilter(const char* who, EXCEPTION_POINTERS* ep)
{
    if (ep && ep->ExceptionRecord)
        ::alog::Write("ERROR", "%s thread SEH 0x%08X at %p",
                      who, (unsigned)ep->ExceptionRecord->ExceptionCode,
                      ep->ExceptionRecord->ExceptionAddress);
    else
        ::alog::Write("ERROR", "%s thread SEH (no record)", who);
    return EXCEPTION_EXECUTE_HANDLER;
}

// ---- 配置校验 (mutating copy; 返回 0=OK 1=参数无效) ------------------
int ValidateConfig(const ACConfig* in, ACConfig* out)
{
    if (!in || in->structSize != sizeof(ACConfig)) return 1;
    *out = *in;
    // 0=屏幕追光 1=拾音(M3 已实现) 11=纯色 2..10,12,13=MCU 特效透传
    if (out->modeIndex < 0 || out->modeIndex > 13) return 1;
    out->ledCount     = std::clamp(out->ledCount, 1, 512);
    out->sides        = std::clamp(out->sides, 2, 4);
    out->direction    = out->direction ? 1 : 0;
    out->captureRange = std::clamp(out->captureRange, 1, 50);
    out->depthOverride = std::max(0, out->depthOverride);
    out->taps         = std::clamp(out->taps, 1, 32);
    out->smoothing    = std::max(0, out->smoothing);
    // fpsCap 归一 (**与托管 FpsCapChoices 表耦合**: 表 / 设置页 XAML 项 / 这里, 改一处同步改):
    //   >144 → 144 —— 实测硬上限: 145Hz 起 USB CDC 写阻塞 (write 1.08→6.24ms、发送被钳),
    //     ≤144 写耗时恒定 ~1.1ms (2026-10-14 tools/LedRateProbe 扫描);
    //   ≤0 (旧"不限") 或 1~29 (表外) → 60 —— 安全默认; "不限"已退役 (240Hz 屏上会直接放飞)。
    //   要放开更高档位: 先用 tools/LedRateProbe 实测目标设备的阻塞点, 再同步改三处。
    if      (out->fpsCap > 144)                  out->fpsCap = 144;
    else if (out->fpsCap <= 0 || out->fpsCap < 30) out->fpsCap = 60;
    out->blackBars    = out->blackBars ? 1 : 0;
    out->blackBarLevel = std::clamp(out->blackBarLevel, 0, 128);
    out->outputOrder  = std::clamp(out->outputOrder, 0, 5);
    out->waitDispPolicy = std::clamp(out->waitDispPolicy, 0, 2);
    out->colorfix     = out->colorfix ? 1 : 0;
    out->sfloor       = std::clamp(out->sfloor, 0.0, 255.0);
    out->baudRate     = std::clamp(out->baudRate, 9600u, 4000000u);
    out->comPort[AC_PORT_LEN - 1] = 0;
    out->audioSource  = std::clamp(out->audioSource, 0, 2);   // 0=回环 1=MCU 自拾音 2=麦克风
    out->audioDevId[AC_AUDIO_ID_LEN - 1] = 0;
    out->audioOutDevId[AC_AUDIO_ID_LEN - 1] = 0;
    out->audioGain    = std::clamp(out->audioGain, 0.1, 5.0);
    out->audioRelease = std::clamp(out->audioRelease, 0.05, 1.0);
    out->audioGate    = std::clamp(out->audioGate, 0.0, 0.1);
    out->audioBass    = std::clamp(out->audioBass, 0.0, 1.0);
    out->audioAgc     = out->audioAgc ? 1 : 0;
    out->audioFollowVolume = out->audioFollowVolume ? 1 : 0;
    out->audioSoft    = std::clamp(out->audioSoft, 0.0, 1.0);
    {   // 灯序偏移钳制 (同厂商 LedRotate: ±灯数 12%; 灯数 <10 无意义直接 0)
        int n = std::min(out->ledCount, serial::kMaxLedCount);
        int maxRot = n >= 10 ? n * 12 / 100 : 0;
        out->ledRotate  = std::clamp(out->ledRotate, -maxRot, maxRot);
    }
    for (uint32_t v : out->reserved) if (v) return 1;
    return 0;
}

// ---- 电源事件回调 (系统线程, 只置原子标志) ---------------------------
ULONG CALLBACK PowerCallback(PVOID /*ctx*/, ULONG type, PVOID setting)
{
    if (!g) return 0;
    if (type == PBT_APMSUSPEND) {
        ACLOGD("power: PBT_APMSUSPEND");
        g->suspendEvt = 1;
    } else if (type == PBT_APMRESUMEAUTOMATIC || type == PBT_APMRESUMESUSPEND) {
        ACLOGD("power: PBT_APMRESUME%s", type == PBT_APMRESUMEAUTOMATIC ? "AUTOMATIC" : "SUSPEND");
        g->resumeEvt = 1;
    } else if (type == PBT_POWERSETTINGCHANGE) {
        auto* s = static_cast<POWERBROADCAST_SETTING*>(setting);
        if (s->DataLength < sizeof(DWORD)) return 0;
        DWORD v = *reinterpret_cast<const DWORD*>(s->Data);
        if (s->PowerSetting == GUID_MONITOR_POWER_ON) {          // 旧 GUID (Win8 起已废弃, 保留兜底)
            ACLOGD("power: GUID_MONITOR_POWER_ON = %lu", v);
            g->monitorPower = (int)v;
        } else if (s->PowerSetting == GUID_CONSOLE_DISPLAY_STATE) {   // Win8+ 的正主
            // 0 = 熄灭 / 1 = 点亮 / 2 = 变暗 (变暗仍发光, 按"亮"处理)
            ACLOGD("power: GUID_CONSOLE_DISPLAY_STATE = %lu", v);
            g->monitorPower = (v == 0) ? 0 : 1;
        }
    }
    return 0;
}

DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS g_powerParams = { PowerCallback, nullptr };

// ---- 设备监视线程: 1s 轮询, 只枚举不打开端口 -------------------------
void MonitorLoopInner()
{
    std::vector<std::wstring> lastPorts, lastTopo;
    bool firstPorts = true, firstTopo = true;
    for (;;) {
        DWORD w = WaitForSingleObject(g->evStopMon, 1000);
        if (w != WAIT_TIMEOUT) break;

        // 1) 灯带在位性 (VID_2E3C&PID_5740 的 COM 口)
        std::vector<serial::ComPortInfo> ports;
        serial::EnumerateVidPidPorts(ports);
        std::vector<std::wstring> names;
        for (auto& p : ports) names.push_back(p.port);
        if (!firstPorts && names != lastPorts) {
            g->deviceChanged++;
            ACLOGI("device set changed: %zu -> %zu port(s)", lastPorts.size(), names.size());
        }
        lastPorts = std::move(names);
        firstPorts = false;

        // 2) 显示器拓扑 (名称+分辨率+主屏)
        std::vector<capture::MonitorDesc> mons;
        capture::EnumerateMonitors(mons);
        std::vector<std::wstring> topo;
        for (auto& m : mons)
            topo.push_back(m.name + L"|" + std::to_wstring(m.w) + L"x" + std::to_wstring(m.h) +
                           (m.primary ? L"|P" : L""));
        if (!firstTopo && topo != lastTopo) {
            g->topologyVer++;
            ACLOGD("monitor topology changed (%zu -> %zu outputs)", lastTopo.size(), topo.size());
        }
        lastTopo = std::move(topo);
        firstTopo = false;
    }
}

void MonitorLoop()
{
    SetThreadDescription(GetCurrentThread(), L"AmbientCore-Monitor");
    __try {
        MonitorLoopInner();
    } __except (CrashFilter("monitor", GetExceptionInformation())) {
        // 监视线程崩溃不拖垮进程; 核心线程的写失败检测仍可兜底
    }
}

// ---- 核心线程 (每会话一条; g = 本会话) --------------------------------
// 命名约定: 参数名 g 影蔽全局单例指针, 会话级状态 (cfg/st/evStop/diag) 全走 g->;
// 全局事件 (deviceChanged/topologyVer/电源/测试图) 同名成员在 Core 上, 访问形式
// 相同 — Core 上恰好没有会话级成员, 不会混。
void CoreLoopInner(Sess* g)
{
    SetThreadDescription(GetCurrentThread(), L"AmbientCore-Stream");
    ACLOGI("core thread enter (slot session)");

    serial::SerialPort sp;
    capture::CaptureSession cap;
    std::vector<uint8_t> frame;                       // 543B 线帧 (惰性建)
    std::vector<capture::Float4> cols;

    bool serialOpen = false;
    std::wstring activePort;
    int  fastLeft = 5;                                // 快速重连配额: 500ms×5 → 2s
    bool capValid = false;
    int  monitorFallback = 0;
    bool waitDisp = false;
    auto lastPolicyResend = steady_clock::now() - seconds(10);
    auto lastFrameSend = steady_clock::now();       // RUNNING 静态桌面重发节拍 (见 STEP_TIMEOUT)
    // 启动/唤醒后的宽限期: D3D 设备创建失败按瞬时处理 (驱动未就绪), 避免日志误报 ERROR
    auto hardGraceUntil = steady_clock::now() + seconds(3);

    int phase = AC_STATE_STARTING;

    auto cfg = SnapCfg(g);
    ACConfig applied = *cfg;
    uint64_t appliedSerial = g->cfgSerial.load();
    Core* C = ::g;  // 全局事件经 C (参数 g 影蔽了全局单例)
    uint32_t seenDeviceChanged = C->deviceChanged.load();
    uint32_t seenTopology = C->topologyVer.load();
    int monPowerSeen = -1;

    // 统计
    uint32_t fpsWindow = 0;
    auto fpsT0 = steady_clock::now();
    double procEma = 0, wireEma = 0;
    uint32_t allBlackRun = 0;
    uint32_t frameCounter = 0;
    // MCU 特效透传状态: 模式切换立即发命令, 之后 1Hz 心跳重发
    int  lastMcuMode = -1;
    auto lastMcuCmd = steady_clock::now() - seconds(10);

    // 主机拾音 (WASAPI 回环) 会话持有; 音源参数只在 cfgSerial 变化时推送
    bool audioOn = false;
    uint64_t audioParamsSerial = 0;
    bool audioCleared = false;          // 进入拾音时清预览帧/处理耗时 (一次性)

    auto EffLeds = [](const ACConfig& c) { return std::min(c.ledCount, serial::kMaxLedCount); };
    auto IsMcuMode = [&](const ACConfig& c, int m) {
        if ((m >= 2 && m <= 10) || m == 12 || m == 13) return true;
        return m == 1 && c.audioSource == 1;              // MCU 自拾音 "Adb"
    };
    auto IsHostAudio = [](const ACConfig& c) {
        // 主机拾音 "Adz": audioSource 0=系统声音回环 2=麦克风 (1=MCU 自拾音)
        return c.modeIndex == 1 && c.audioSource != 1;
    };

    auto MakeCaptureParams = [](const ACConfig& c) {
        capture::Params p;
        p.monitorIndex = c.monitorIndex;
        p.leds = c.ledCount;
        p.sides = c.sides;
        p.direction = c.direction;
        p.range = c.captureRange;
        p.depthOverride = c.depthOverride;
        p.taps = c.taps;
        p.smoothing = c.smoothing;
        p.ledRotate = c.ledRotate;
        p.blackbars = c.blackBars != 0;
        p.barlevel = c.blackBarLevel;
        return p;
    };
    auto MakeColorParams = [](const ACConfig& c, double brightness) {
        color::Params p;
        p.brightness = brightness;
        p.vibrance = c.vibrance;
        p.hueMul = c.hueMul;
        p.gamma = c.gamma;
        p.rGain = c.rGain; p.gGain = c.gGain; p.bGain = c.bGain;
        p.cut = c.colorCut;
        p.minb = c.minBrightness;
        p.output = c.outputOrder;
        p.colorfix = c.colorfix;
        p.sfloor = c.sfloor;
        return p;
    };

    auto EnsureFrame = [&]() {
        if (frame.size() != (size_t)serial::kFrameLen) {
            frame.assign((size_t)serial::kFrameLen, 0);
            frame[0] = 'A'; frame[1] = 'd'; frame[2] = 'a';
        }
    };

    // 最近下发帧快照 → ACStatus.ledRgb (LedRingView 预览数据源)
    auto SnapshotFrame = [&](const std::vector<uint8_t>& f, const ACConfig& c) {
        int n = std::min<int>((int)f.size() - 3, EffLeds(c) * 3);
        if (n <= 0) return;
        std::unique_lock lk(g->stMx);
        memcpy(g->st.ledRgb, f.data() + 3, (size_t)n);
    };

    // ==== 输出包络 + 亮度限幅 (S1/S2; 见 docs/PROJECT.md §6) ====
    // 只缩放"写出去的字节", **绝不写回配置** —— 配置里的 brightness 永远是用户意图。
    Envelope env;
    env.Snap(0.0);                                    // 会话起步 = 全黑, 握手成功后淡入
    std::vector<uint8_t> lastOut, scratch, pol;       // 最后一份"可淡"字节 / 缩放缓冲 / policy 帧
    double slewB = applied.brightness;                // 被限幅的亮度 (输出用, 不写配置)
    double slewT = Millis();
    double lastFrameAt = 0.0, lastStatusLog = 0.0;    // 诊断: 末帧时刻 / 上次状态行时刻
    bool blockedLog = false;                          // 诊断: "屏幕仍熄灭"只记一次
    bool reconnLogDone = false;                       // 串口重连失败: 只把**首次**记 WARN (实测占日志 38%)
    bool graceLog = false;                            // 诊断: "唤醒宽限期内忽略"只记一次
    double wakeGraceUntil = 0.0;                      // 刚醒的拓扑抖动宽限截止 (毫秒)
    ULONGLONG tickLoop = GetTickCount64();            // S0 唤醒自愈用 (含睡眠时间的墙钟)

    /// 把一整帧/一条命令**写完**才返回 true; 写超时(设备一时没收)不判掉线, 只补写剩余部分。
    /// 为什么必须这么写 (见 docs/PROJECT.md §5 串口不变式):
    ///   · 固件**按定长 `serial::kFrameLen`(543) 给帧定界** ⇒ 半帧被丢掉 = 它把下一帧的帧头当成这帧的
    ///     尾巴, 之后**永久错位** (只有握手能复位) ✗ —— 所以"写不进去就丢帧"是禁止的, 必须补写;
    ///   · 串口写超时只有 100ms (`ConfigurePort` 的 WriteTotalTimeoutConstant), 而**从 MCU 特效切回
    ///     内容模式时固件要收尾特效、会有 ~100ms+ 不吃 CDC 数据** (用户实测: 切出"电闪雷鸣"后
    ///     ~103ms 必然写失败 → 判 DEV_LOST → UI 显示"正在重连" → 600ms 后又连回来 ✗)。
    ///     端口和设备从头到尾都是好的 (拔线时 WriteFile 是立刻报错的, 不会卡到超时) ✓
    ///   ⇒ 策略: 软失败(超时) → 继续补写, 预算 `kWriteBudgetMs` 内写完就没事; 超预算(设备真挂了)
    ///      或硬错误(端口没了) → 返回 false, 由调用方 WireFail 走重连 (保持原来的快速检测) ✓
    auto WriteDurable = [&](const uint8_t* d, size_t len, const char* what) -> bool {
        double t0 = Millis();
        bool stuck = false;
        if (sp.WriteAll(d, (uint32_t)len, (uint32_t)kWriteBudgetMs, &stuck, g->evStop)) {
            double ms = Millis() - t0;
            if (ms > 30.0)                              // 设备卡了一下但补写成功 (诊断用, 缺省不落盘)
                ACLOGD("write slow %s: %zu B in %.0fms —— 设备一时没收数据, 已补写", what, len, ms);
            return true;
        }
        if (stuck)
            ACLOGW("write stuck on %ls (%s: %zu B 超过 %.0fms 没写完) -> DEV_LOST",
                   activePort.c_str(), what, len, kWriteBudgetMs);
        return false;                                   // stuck / 硬错误 / 停止请求 → 交给调用方
    };

    /// 按包络缩放后写出 (≈1 时零拷贝; 只动第 3 字节之后的负载)
    auto WriteEnv = [&](const uint8_t* d, size_t len) -> bool {
        double m = env.value;
        if (m >= 0.999 || len <= 3) return WriteDurable(d, len, "env");
        scratch.assign(d, d + len);
        for (size_t i = 3; i < scratch.size(); i++)
            scratch[i] = (uint8_t)(scratch[i] * m + 0.5);
        return WriteDurable(scratch.data(), scratch.size(), "env");
    };
    /// 内容帧: 先记下**原始字节**(供淡出复用, 不累乘以免量化误差), 再按包络写出。
    /// ⚠️ **WAIT_DISP 期间一律不写内容帧** —— 屏幕上该显示的是 policy 帧 (熄灭/最暗/保持),
    /// 由 `SendPolicyFrame` 负责 (进入时 + 每 2s 重发)。
    /// 为什么必须挡: 进 WAIT_DISP 时只把**字节**交叉淡化到了 policy 内容, **没有归零包络** ——
    /// 而某些显示器 (实测: 笔记本外接屏 / 独显那条输出) 在熄灭后 DDA **仍继续送帧** ✗ →
    /// 这些内容帧会按 env=1.0 原样写出去 = 灯又亮了 (用户实测"淡出熄灭后又马上亮起")。
    /// 内屏熄灭后不再送帧, 所以以前看不出这个问题 ✗ (多屏才有 = 用户定位的规律 ✓)。
    auto PushFrame = [&](const uint8_t* d, size_t len) -> bool {
        if (len > 3) lastOut.assign(d, d + len);
        if (waitDisp) return true;                  // policy 期间由 SendPolicyFrame 独占输出
        return WriteEnv(d, len);
    };
    // ---- S4: 追光时域平滑的状态 (两个开关都是每设备配置) ----
    std::vector<float> chaseBuf;                      // 逐灯逐通道的指数平均缓冲 (3*leds)
    bool chaseInit = false;                           // false = 下一帧直接"吸附"(不做插值)
    double chaseMean = -1.0;                          // 上一帧写出的整帧平均亮度 (-1 = 无)
    double chasePrevAt = 0.0;                         // 上一帧时刻 (算 dt)
    /// 状态重置: 重连 / 改灯数 / 切模式 / 熄屏进出 / 开关翻转 —— 都要调, 否则拿旧帧插值出怪色
    auto InvalidateChaseSmooth = [&]() { chaseInit = false; chaseMean = -1.0; chasePrevAt = 0.0; };

    /// 退出 WAIT_DISP (内容恢复/唤醒): 从全黑淡入 —— 与"进"时的淡出对称。
    /// ⚠️ **只要屏幕被明确报告为熄灭 (`monitorPower == 0`), 一律不许退出** —— 所有理由都挡:
    ///   · 帧驱动 ("content/desktop resumed"): 屏幕熄灭过程中采集侧还会送来**最后一帧**,
    ///     实测进 WAIT_DISP 后 63ms 就被它弹回 RUNNING → 灯一直亮着;
    ///   · "capture built": DDA **在显示器关着时也能重建成功** (输出还在, 只是面板灭了) ——
    ///     实测 17:03 出现 1~3 秒一轮的 进出循环 = 用户看到的"灭了又马上亮、闪一下" ✗
    /// 退出只能靠屏幕状态通知恢复 (日志实测会来 `GUID_CONSOLE_DISPLAY_STATE = 1`) ✓
    auto ExitWaitDisp = [&](const char* why) {
        if (C->monitorPower.load() == 0) {
            if (!blockedLog) {
                blockedLog = true;
                ACLOGD("WAIT_DISP: 忽略 \"%s\" (屏幕仍被报告为熄灭)", why);
            }
            return;
        }
        blockedLog = false;
        waitDisp = false;
        wakeGraceUntil = Millis() + 1500.0;          // 醒来后的拓扑抖动宽限 (见 EnterWaitDisp)
        InvalidateChaseSmooth();                     // 熄屏进出: 内容会跳, 别拿旧帧插值
        env.Snap(0.0);
        env.StartTo(1.0, kWaitDispFadeMs);
        ACLOGI("%s (fade in %.0fms)", why, kWaitDispFadeMs);
    };

    auto SendOff = [&]() {
        if (!sp.IsOpen()) return;
        std::vector<uint8_t> z;
        serial::BuildZeroFrame(z);
        WriteDurable(z.data(), z.size(), "off");
    };

    auto SendPolicyFrame = [&](const ACConfig& c) {
        if (!sp.IsOpen()) return;
        EnsureFrame();
        if (c.waitDispPolicy == 2) {
            std::fill(frame.begin() + 3, frame.end(), (uint8_t)0);       // 熄灭
        } else if (c.waitDispPolicy == 1) {                              // 最暗 (minB 灰帧)
            uint8_t g8 = (uint8_t)std::clamp(c.minBrightness, 0, 255);
            std::fill(frame.begin() + 3, frame.end(), g8);
        }   // 0 = 保持最后帧: 直接重发现有 frame 内容
        WriteDurable(frame.data(), frame.size(), "policy");
    };

    /// WAIT_DISP 进入。`graceOnWake`: 采集侧报的拓扑/访问抖动 —— 唤醒后 1.5s 内忽略,
    /// 因为"显示器刚亮"那一下 DDA 常会报一次 access lost (实测外接屏: 亮起 0.32s 后又报 access lost
    /// → 淡出 → 用户看到"闪一下")。**屏幕明确熄灭 (monitor power off) 那条不走宽限** ✓
    auto EnterWaitDisp = [&](const char* why, bool graceOnWake = false) {
        if (graceOnWake && Millis() < wakeGraceUntil) {
            if (!graceLog) { graceLog = true; ACLOGD("WAIT_DISP: 忽略 \"%s\" (唤醒宽限期内)", why); }
            return;
        }
        graceLog = false;
        if (!waitDisp) {
            waitDisp = true;
            ACLOGI("enter WAIT_DISP (%s), policy=%d", why, applied.waitDispPolicy);
            InvalidateChaseSmooth();                 // 进 WAIT_DISP: 同上
            // S2: 把"屏幕上的最后一份内容"交叉淡化到 policy 帧。
            // policy=保持(0) 时目标就是它自己 → CrossFade 直接跳过; 熄灭(2)/最暗(1) 则平滑过渡 ✓
            EnsureFrame();
            pol.assign(frame.begin(), frame.end());
            if (applied.waitDispPolicy == 2) {
                std::fill(pol.begin() + 3, pol.end(), (uint8_t)0);
            } else if (applied.waitDispPolicy == 1) {
                std::fill(pol.begin() + 3, pol.end(),
                          (uint8_t)std::clamp(applied.minBrightness, 0, 255));
            }
            CrossFade(sp, lastOut, pol, scratch, kWaitDispFadeMs);
            lastOut = pol;                        // 屏幕上现在是 policy 内容 → 供后续淡出复用
            SendPolicyFrame(applied);
            lastPolicyResend = steady_clock::now();
        }
        if (phase != AC_STATE_WAIT_DISP) { phase = AC_STATE_WAIT_DISP; SetPhase(g, phase); }
    };

    auto WireFail = [&](const char* why) {
        ACLOGE("serial %s on %ls -> DEV_LOST", why, activePort.c_str());
        SetDiag(g, "WARN ", "serial %s on %s, reconnecting", why, WideToUtf8(activePort).c_str());
        sp.Close();
        serialOpen = false;
        fastLeft = 3;
        waitDisp = false;
        phase = AC_STATE_DEV_LOST;
        SetPhase(g, phase);
    };

    // 串口连接 (VID/PID 身份跟随): 返回 true = 已握手
    auto TryConnect = [&](const ACConfig& c) -> bool {
        std::vector<serial::ComPortInfo> ports;
        serial::EnumerateVidPidPorts(ports);
        wchar_t cfgPort[AC_PORT_LEN] = { 0 };
        MultiByteToWideChar(CP_UTF8, 0, c.comPort, -1, cfgPort, AC_PORT_LEN);
        std::vector<std::wstring> candidates;
        if (cfgPort[0]) {
            // 手动指定了端口 → 只认它。判"在不在位"必须查**本机所有 COM 口**:
            // 拿上面那个按 VID/PID 过滤过的 `ports` 来判是错的 —— 任何一个"非灯带"的 COM 口
            // (例如用户故意填的 COM4) 都不在 `ports` 里, 会被当成"配置端口消失",
            // 于是走身份跟随、偷偷连回灯带 (实测现象: 端口填 COM4, 主页开启却连到 COM5)。
            std::vector<std::wstring> allPorts;
            serial::EnumerateAllPorts(allPorts);
            bool present = false;
            for (auto& p : allPorts)
                if (_wcsicmp(p.c_str(), cfgPort) == 0) { present = true; break; }
            if (present) {
                // 只用它: 握手失败就是失败, **不回落到 VID/PID 身份跟随** ——
                // 手动指定的用途就是"换芯片/桥接/多灯带/排查", 偷偷连回灯带会让人无法判断。
                candidates.push_back(cfgPort);
            } else {
                // 端口真的不在位 → 按 VID/PID 身份自动跟随新端口 (API 3.6 #5)
                if (ports.size() == 1) {
                    ACLOGW("configured port %ls gone, following VID/PID -> %ls",
                           cfgPort, ports[0].port.c_str());
                    candidates.push_back(ports[0].port);
                } else {
                    for (auto& p : ports) candidates.push_back(p.port);  // 多匹配逐个握手判型
                }
            }
        } else {
            for (auto& p : ports) candidates.push_back(p.port);          // 自动探测
        }
        for (auto& cand : candidates) {
            if (WaitStop(g, 0)) return false;                            // 停止时放弃
            // 与 UI 的设备枚举 (serial::ProbeDevice) 串行化: 两边都会独占打开同一端口,
            // 并发时会互相抢成"忙" → 会话误判无可用端口。见 serial.h ProbeLock 的说明。
            std::lock_guard probeLk(serial::ProbeLock());
            if (!sp.Open(cand, c.baudRate)) {
                if (!reconnLogDone) { reconnLogDone = true; ACLOGW("open %ls failed (busy or gone)", cand.c_str()); }
                else ACLOGD("open %ls failed (busy or gone)", cand.c_str());
                continue;
            }
            uint8_t resp = 0;
            if (serial::Handshake(sp, resp, g->evStop)) {
                serialOpen = true;
                activePort = cand;
                {
                    std::unique_lock lk(g->stMx);
                    g->st.devType = (resp >> 2) & 3;
                    g->st.devId = resp & 3;
                    strncpy_s(g->st.comPort, WideToUtf8(cand).c_str(), _TRUNCATE);
                }
                ACLOGI("connected %ls, handshake 0x%02X (type %d id %d)",
                       cand.c_str(), resp, (resp >> 2) & 3, resp & 3);
                return true;
            }
            sp.Close();
            if (!reconnLogDone) { reconnLogDone = true; ACLOGW("handshake failed on %ls", cand.c_str()); }
            else ACLOGD("handshake failed on %ls", cand.c_str());
        }
        return false;
    };

    SetPhase(g, phase);
    for (;;) {
        if (WaitStop(g, 0)) break;                   // 停止请求 → 走退出清理
        // S0 低电量待机的兜底自愈: 实测这份日志里 PBT_APMSUSPEND **整份只出现过 1 次** ——
        // 系统根本不通知我们, 于是"唤醒后卡住/不重建"没有任何事件可依。改用**墙钟跳变**:
        // GetTickCount64() 含睡眠时间, 主循环每轮比一次, 跳过 > 8s 就说明刚被冻过
        // (阈值 8s > 我们自己最长的等待 5s, 不会误触发) → 强制整段重建。
        {
            ULONGLONG tickNow = GetTickCount64();
            if (tickNow - tickLoop > 8000) {
                ACLOGW("wake detected (wall-clock jumped %llu ms) -> full rebuild",
                       tickNow - tickLoop);
                cap.Destroy(); capValid = false;
                sp.Close(); serialOpen = false;
                waitDisp = false;
                fastLeft = 5;                                  // 唤醒后快速期优先
                hardGraceUntil = steady_clock::now() + seconds(3);
                phase = AC_STATE_STARTING;
                SetPhase(g, phase);
            }
            tickLoop = tickNow;
        }

        env.Step();                                  // 包络按**时间**推进 (与帧率/模式无关)

        // 亮度限幅 (S2): 每帧最多走 dt/kBrightSlewMs → 全程约 kBrightSlewMs 走完。
        // 拖动滑条时逐帧变化都小于限幅 → 不介入(保持跟手); 预设/托盘/系统亮度的跳变 → 被抹平。
        {
            double nowS = Millis();
            double dtS = nowS - slewT;
            slewT = nowS;
            if (dtS > 0.0) {
                double maxStep = dtS / kBrightSlewMs;
                double dS = applied.brightness - slewB;
                if (dS > maxStep) dS = maxStep;
                else if (dS < -maxStep) dS = -maxStep;
                slewB += dS;
            }
        }

        // ============ 1. 挂起 / 恢复 ============
        if (C->resumeEvt.exchange(0)) {
            // ⚠️ 醒来优先: 把还挂着的 suspendEvt 一起清掉 —— 睡眠里"先挂起后恢复"的两个标志会同时挂着,
            //    否则同一次循环轮次里: resume 重建完 → 紧接着 suspend 分支立刻把它关掉 (实测 S0:
            //    14:30:58 "resume: full rebuild" 之后 1ms 就是 "suspend: fading out" + phase 1 -> 0,
            //    然后就卡在 STOPPED 等一个永远不会再来的 resume ✗)
            C->suspendEvt = 0;
            ACLOGI("resume: full rebuild (adapter -> output -> D3D -> DDA -> serial)");
            cap.Destroy(); capValid = false;
            sp.Close(); serialOpen = false;
            waitDisp = false;
            fastLeft = 5;                       // 唤醒后快速期优先
            hardGraceUntil = steady_clock::now() + seconds(3);
            phase = AC_STATE_STARTING;
            SetPhase(g, phase);
        }
        else if (C->suspendEvt.exchange(0)) {
            ACLOGI("suspend: fading out, then off-frame");
            // 用**更短**的淡出: 系统睡眠时进程随时被冻结, 300ms 会被冻在半亮 (实测 S0 待机)
            FadeOutToBlack(sp, lastOut, scratch, kSuspendFadeMs);
            SendOff();                          // 防半帧/端口消失中途写
            if (audioOn) { audioOn = false; MeterRelease(); }   // 挂起期间停止音源重建循环
            cap.Destroy(); capValid = false;
            sp.Close(); serialOpen = false;
            waitDisp = false;
            phase = AC_STATE_STOPPED;
            SetPhase(g, phase);
            // 等唤醒: 正常走 resume 通知; **兜底**用"墙钟跳变" —— S0 低电量待机下通知
            //   可能收不到 (用户实测: 唤醒后停在"已停止"、不自动回连), 而 GetTickCount64
            //   是含睡眠时间的, 睡醒后必然出现一段跳变 → 据此自愈重建。
            ULONGLONG tickLast = GetTickCount64();
            ULONGLONG waitStart = tickLast;
            while (!WaitStop(g, 200)) {
                if (C->resumeEvt.load()) break;
                ULONGLONG tickNow = GetTickCount64();
                if (tickNow - tickLast > 2000) {          // 这段"时间"里我们被冻结过 → 已醒
                    ACLOGW("suspend: wall-clock jumped %llu ms -> self-heal resume",
                           tickNow - tickLast);
                    C->resumeEvt = 1;
                    break;
                }
                if (tickNow - waitStart > 30000) {        // 兜底: 醒着但 resume 通知丢了, 别卡死
                    ACLOGW("suspend: no resume in 30s -> forcing rebuild");
                    C->resumeEvt = 1;
                    break;
                }
                tickLast = tickNow;
            }
            continue;
        }

        // ============ 2. 配置热更新 (原子快照 diff) ============
        cfg = SnapCfg(g);
        uint64_t s = g->cfgSerial.load();
        if (s != appliedSerial) {
            const ACConfig& nc = *cfg;
            if (strncmp(nc.comPort, applied.comPort, AC_PORT_LEN) != 0 ||
                nc.baudRate != applied.baudRate) {
                ACLOGI("config: com/baud changed -> reconnect");
                sp.Close(); serialOpen = false;
                fastLeft = 5;
            }
            if (nc.monitorIndex != applied.monitorIndex) {
                ACLOGI("config: monitor %d -> %d, rebuild capture",
                       applied.monitorIndex, nc.monitorIndex);
                cap.Destroy(); capValid = false;
            }
            bool layoutChanged =
                nc.ledCount != applied.ledCount || nc.sides != applied.sides ||
                nc.direction != applied.direction || nc.captureRange != applied.captureRange ||
                nc.depthOverride != applied.depthOverride || nc.taps != applied.taps ||
                nc.smoothing != applied.smoothing || nc.blackBars != applied.blackBars ||
                nc.blackBarLevel != applied.blackBarLevel ||
                nc.ledRotate != applied.ledRotate;   // 灯序偏移也走取色区重建 (校准窗实时生效)
            if (capValid && layoutChanged) cap.UpdateLayout(MakeCaptureParams(nc));
            // 灯数/边数变了 → 平滑缓冲长度不再对应; 两个开关翻转也要重新吸附
            if (layoutChanged || nc.chaseSmoothMode != applied.chaseSmoothMode ||
                std::abs(nc.chaseSmoothMode) != std::abs(applied.chaseSmoothMode))
                InvalidateChaseSmooth();
            // cols 必须与新灯数同步, 否则 Step 的 memcpy 越界写堆 (曾致 CLR 堆损坏)
            cols.resize((size_t)std::min(nc.ledCount, serial::kMaxLedCount) + 4);
            if (nc.modeIndex != applied.modeIndex) {
                ACLOGI("config: mode %d -> %d", applied.modeIndex, nc.modeIndex);
                InvalidateChaseSmooth();             // 切模式: 内容完全不同了, 平滑状态清零
                // ---- S3: 切模式淡入淡出 (docs/PROJECT.md §6) ----
                // 内容模式 = 追光(0) / 拾音(1) / 纯色(11) —— 亮度语义连续, 只是内容变;
                // MCU 特效的帧由固件渲染, 我们只能"进出"两端淡 (淡的是**我们自己**的颜色帧) ✓
                // ⚠️ 同模式内改参数 (采样深度/边数/灯数…) **不淡** —— 用户正在看"改了会怎样",
                //    加延迟反而干扰判断 ✓
                auto isContent = [](int m) { return m == 0 || m == 1 || m == 11; };
                bool oldC = isContent(applied.modeIndex), newC = isContent(nc.modeIndex);
                bool oldMcu = IsMcuMode(applied, applied.modeIndex);
                bool newMcu = IsMcuMode(nc, nc.modeIndex);
                if (oldC && newC) {
                    // 内容 ↔ 内容: 淡出旧内容 → (下面 applied = nc) → 淡入新内容
                    FadeOutToBlack(sp, lastOut, scratch, kModeFadeMs);
                    env.Snap(0.0);
                    env.StartTo(1.0, kModeFadeMs);
                } else if (oldC && newMcu) {
                    // **进 MCU**: 命令下发之前先淡出 (此刻我们还在写颜色帧 ✓ 之后就没得淡了)
                    FadeOutToBlack(sp, lastOut, scratch, kMcuFadeMs);
                } else if (oldMcu && newC) {
                    // **出 MCU**: 我们重新开始写颜色帧 → 从全黑淡入
                    env.Snap(0.0);
                    env.StartTo(1.0, kMcuFadeMs);
                }
                // MCU ↔ MCU: 两边都由固件渲染, 无从淡起 (也没必要) → 什么都不做 ✓
            }
            applied = nc;
            appliedSerial = s;
            if (phase == AC_STATE_ERROR) {      // 配置变化给予重试机会
                phase = AC_STATE_STARTING;
                SetPhase(g, phase);
            }
        }

        // ============ 2b. 主机拾音会话生命周期 (跟随配置, Start 即拾音也覆盖) =====
        if (IsHostAudio(applied)) {
            if (!audioOn) { audioOn = true; MeterAcquire(); }
            MeterSetSource(applied);            // 回环/麦克风切换 (变更触发音源重建)
            MeterSetTarget(applied);            // 指定输入端点 (空=跟随默认, 变更触发重建)
            if (audioParamsSerial != s) { MeterPushParams(applied); audioParamsSerial = s; }
            if (!audioCleared) {            // 预览帧清零 + procEma 归零 (拾音不走采集路径)
                std::unique_lock lk(g->stMx);
                memset(g->st.ledRgb, 0, sizeof(g->st.ledRgb));
                procEma = 0;
                audioCleared = true;
            }
            if (capValid) {                     // 拾音不依赖显示器, 释放 DDA 会话
                cap.Destroy();
                capValid = false;
            }
            if (waitDisp) ExitWaitDisp("audio mode resumed");   // 拾音不受熄屏/锁屏策略影响
        } else {
            audioCleared = false;
            if (audioOn) {
                audioOn = false;
                MeterRelease();
                audioParamsSerial = 0;
            }
        }

        // ============ 3. 事件: 热插拔 / 拓扑 / DPMS ============
        uint32_t dc = C->deviceChanged.load();
        if (dc != seenDeviceChanged) {
            seenDeviceChanged = dc;
            fastLeft = 5;                       // 热插拔后快速重连
        }
        uint32_t tv = C->topologyVer.load();
        if (tv != seenTopology) {
            seenTopology = tv;
            if (capValid) {
                ACLOGD("topology change -> full capture rebuild");
                cap.Destroy(); capValid = false;   // 适配器级全量重建
            }
        }
        int mp = C->monitorPower.load();
        if (mp != monPowerSeen) {
            monPowerSeen = mp;
            // 仅追光模式受熄屏影响 (画面源熄灭); 纯色/MCU/拾音的画面源不依赖显示器
            if (mp == 0 && phase == AC_STATE_RUNNING && !waitDisp && applied.modeIndex == 0)
                EnterWaitDisp("monitor power off");
        }

        // ============ 4. 串口连接 ============
        if (!serialOpen) {
            if (TryConnect(applied)) {
                lastMcuMode = -1;           // 重连后固件特效已清, 强制重发 MCU 命令
                phase = (capValid || applied.modeIndex != 0) ? AC_STATE_RUNNING : AC_STATE_STARTING;
                SetPhase(g, phase);
                reconnLogDone = false;               // 连上了 → 下次断连重新记一条 WARN
                InvalidateChaseSmooth();             // 新会话: 平滑状态清零
                env.Snap(0.0);                       // 新会话/重连: 从全黑起
                env.StartTo(1.0, kFadeInMs);         // 淡入 (追光/拾音/纯色 都吃这一份)
                slewB = applied.brightness;          // 限幅器归位到当前配置值 (别从陈旧值爬)
                slewT = Millis();
            } else {
                if (phase != AC_STATE_DEV_LOST) {
                    // 手动指定了端口时把话说清 (轮 34 补 6): 原来统一报
                    // "no usable port (VID_2E3C&PID_5740)", 会让人以为"没插灯带/驱动没装",
                    // 而真实原因常常是"你指定的那个口不是灯带"(用户实测的困惑点)。
                    if (applied.comPort[0])
                        SetDiag(g, "WARN ",
                                "指定端口 %s 未应答 (不是灯带? 检查接线/换口, 或在高级页改回\"自动检测\")",
                                applied.comPort);
                    else
                        SetDiag(g, "WARN ", "device lost: no usable port (VID_2E3C&PID_5740)");
                }
                phase = AC_STATE_DEV_LOST;
                SetPhase(g, phase);
                // 这里会 continue 跳过 §9 状态快照 —— 但"灯带被拔掉/端口消失"恰恰是最需要让 UI
                // 知道热插拔计数器变了的时刻: 不回写的话 UI 的 DevicesChanged 永不触发,
                // 主页设备列表就一直不刷新 (轮 32 用户反馈)。故单独补一次。
                { std::unique_lock lk(g->stMx); g->st.deviceChanged = C->deviceChanged.load(); }
                int delay = fastLeft > 0 ? (fastLeft--, 500) : 2000;
                if (WaitStop(g, (DWORD)delay)) break;   // 等待期间收到停止
                continue;
            }
        }

        // ============ 5. 测试图 (校准/排障, 覆盖正常内容, ~30Hz) ============
        int pat = C->testPattern.load();
        if (pat != 0) {
            EnsureFrame();
            if (pat == 1) {
                capture::CaptureSession::BuildPatternFrame(MakeCaptureParams(applied), frame);
            } else if (pat == 2) {
                for (int i = 0; i < EffLeds(applied); i++) {
                    frame[3 + i * 3 + 0] = 255; frame[3 + i * 3 + 1] = 0; frame[3 + i * 3 + 2] = 255;
                }
            }
            SnapshotFrame(frame, applied);
            if (!PushFrame(frame.data(), frame.size())) { WireFail("write failed"); continue; }
            uint8_t sb;
            if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
            frameCounter++;
            fpsWindow++;
            {   // 测试图块经 continue 跳过循环底部快照, 这里直接刷新计数
                std::unique_lock lk(g->stMx);
                g->st.frameCounter = frameCounter;
            }
            WaitStop(g, 33);
            continue;
        }

        // ============ 6. 采集构建 (mode 0, 未建或失效) ============
        if (applied.modeIndex == 0 && !capValid) {
            int fb = 0;
            auto res = cap.Build(MakeCaptureParams(applied), &fb);
            if (res == capture::CaptureSession::BUILD_OK) {
                capValid = true;
                monitorFallback = fb;
                cols.resize((size_t)EffLeds(applied) + 4);
                if (waitDisp) ExitWaitDisp("capture built");
                if (!waitDisp && phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
                ACLOGD("capture built: monitor %d%s (%dx%d hdr=%d)", applied.monitorIndex,
                       fb ? " (fallback->primary)" : "", cap.Width(), cap.Height(), 0);
            } else if (res == capture::CaptureSession::BUILD_TRANSIENT ||
                       steady_clock::now() < hardGraceUntil) {
                EnterWaitDisp(res == capture::CaptureSession::BUILD_TRANSIENT
                                  ? "capture unavailable"
                                  : "capture not ready (device warm-up)", /*graceOnWake*/ true);
                WaitStop(g, 500);               // 锁屏/无输出/驱动未就绪: 500ms 重试
                continue;
            } else {
                SetDiag(g, "ERROR", "capture build failed (D3D/resources), slow retry");
                phase = AC_STATE_ERROR;
                SetPhase(g, phase);
                WaitStop(g, 5000);
                continue;
            }
        }

        // ============ 7. 模式分派 ============
        if (applied.modeIndex == 11) {
            // ---- 纯色: "Adl" 定长帧, solidRgb 过颜色链, ~30Hz
            EnsureFrame();
            frame[2] = 'l';
            uint8_t rgb[3];
            color::AdjustLedColors(MakeColorParams(applied, slewB),       // 亮度走限幅值
                                   applied.solidRgb[0] / 255.0, applied.solidRgb[1] / 255.0,
                                   applied.solidRgb[2] / 255.0, rgb);
            for (int i = 0; i < EffLeds(applied); i++) {
                frame[3 + i * 3 + 0] = rgb[0];
                frame[3 + i * 3 + 1] = rgb[1];
                frame[3 + i * 3 + 2] = rgb[2];
            }
            SnapshotFrame(frame, applied);
            auto tw = steady_clock::now();
            if (!PushFrame(frame.data(), frame.size())) { WireFail("write failed"); continue; }
            wireEma = wireEma == 0 ? duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count()
                                   : wireEma * 0.9 + duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count() * 0.1;
            uint8_t sb;
            if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
            frameCounter++;
            fpsWindow++;
            if (waitDisp) ExitWaitDisp("content resumed");
            if (!waitDisp && phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
            if (applied.fpsCap > 0) {
                double need = 1000.0 / applied.fpsCap;
                WaitStop(g, (DWORD)std::max(0.0, need - 33.0));
            } else {
                WaitStop(g, 33);
            }
        } else if (IsHostAudio(applied)) {
            // ---- 拾音律动 (主机拾音): 5ms 节拍 "Adz"+音量×3; 不建 DDA, 与显示器无关。
            //      固件把该字节同时用作整体渲染亮度 (静音全灭) → 亮度参数 = 灯带最高亮度上限。
            uint8_t env = 0;
            float master = 1.0f;
            MeterRead(&env, &master);
            double cap = std::clamp(slewB, 0.0, 1.0);          // 亮度走限幅值
            double v = (double)env * applied.audioGain * cap *
                       (applied.audioFollowVolume ? master : 1.0f);
            uint8_t b = (uint8_t)std::clamp((int)(v + 0.5), 0, 255);
            const uint8_t f6[6] = { 'A', 'd', 'z', b, b, b };
            auto tw = steady_clock::now();
            if (!PushFrame(f6, sizeof(f6))) { WireFail("write failed"); continue; }
            double wireMs = duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count();
            wireEma = wireEma == 0 ? wireMs : wireEma * 0.9 + wireMs * 0.1;
            uint8_t sb;
            if (sp.PollStatusByte(sb)) {
                std::unique_lock lk(g->stMx);
                g->st.ledStatusByte = sb;
                g->st.audioLevel = b;
            }
            frameCounter++;
            fpsWindow++;
            if (!waitDisp && phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
            WaitStop(g, 5);                     // timeBeginPeriod(1) 已开 → ~170-200Hz
        } else if (IsMcuMode(applied, applied.modeIndex)) {
            // ---- MCU 特效透传: 3B 命令 Ad+(0x61+mode), 模式切换立即发, 之后 1Hz
            //      心跳重发; MCU 每秒回 1B 状态 (bit0=供电)。特效由 MCU 自渲染,
            //      主机不发灯帧, ledRgb 预览清零。mode 1 + audioSource=1 → "Adb"。
            auto now1 = steady_clock::now();
            if (applied.modeIndex != lastMcuMode || now1 - lastMcuCmd >= seconds(1)) {
                if (applied.modeIndex != lastMcuMode) {
                    std::unique_lock lk(g->stMx);
                    memset(g->st.ledRgb, 0, sizeof(g->st.ledRgb));
                    lastMcuMode = applied.modeIndex;
                }
                const uint8_t cmd[3] = { 'A', 'd', (uint8_t)(0x61 + applied.modeIndex) };
                auto tw = steady_clock::now();
                if (!WriteDurable(cmd, 3, "mcu cmd")) { WireFail("write failed"); continue; }
                double wireMs = duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count();
                wireEma = wireEma == 0 ? wireMs : wireEma * 0.9 + wireMs * 0.1;
                lastMcuCmd = now1;
                frameCounter++;
                fpsWindow++;
                if (waitDisp) ExitWaitDisp("MCU mode");
                if (!waitDisp && phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
            }
            uint8_t sb;
            if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
            WaitStop(g, 50);
        } else if (applied.modeIndex == 0 && capValid) {
            // ---- 屏幕追光: 事件驱动帧循环
            auto t0 = steady_clock::now();
            auto stp = cap.Step(50, cols.data());
            if (stp == capture::CaptureSession::STEP_FRAME) {
                double procMs = duration_cast<duration<double, std::milli>>(steady_clock::now() - t0).count();
                procEma = procEma == 0 ? procMs : procEma * 0.9 + procMs * 0.1;
                lastFrameAt = Millis();          // 诊断: 末帧(有更新)时刻
                EnsureFrame();                   // 线帧缓冲 ("Ada" + 543B, 尾部补零)
                color::Params cp = MakeColorParams(applied, slewB);    // 亮度走限幅值
                for (int i = 0; i < EffLeds(applied); i++) {
                    uint8_t px[3];
                    color::AdjustLedColors(cp, cols[i].x, cols[i].y, cols[i].z, px);
                    frame[3 + i * 3 + 0] = px[0];
                    frame[3 + i * 3 + 1] = px[1];
                    frame[3 + i * 3 + 2] = px[2];
                }

                // ---- S4: 追光时域平滑 (两个每设备开关; 都只作用于"写出去的字节", 不动颜色链) ----
                size_t chN = (size_t)EffLeds(applied) * 3;
                double chNow = Millis();
                // 档位字段语义 = "符号是开关, 绝对值是档位" → **关闭时完全不介入** (绝对值只是记忆)
                const int  chaseMag = std::abs(applied.chaseSmoothMode);
                const bool chaseOn = applied.chaseSmoothMode > 0;
                const bool chaseGuard = chaseOn && chaseMag >= 1;        // 1 = 抑制闪光
                const bool chaseSmooth = chaseOn && chaseMag >= 2;       // 2 = 再加时域柔和
                if (chaseSmooth && chN > 0) {
                    // ① 时域柔和: 逐灯逐通道一阶低通 (τ = kChaseSmoothTauMs)。治"局部颜色跳变",
                    //    代价是轻微拖影 + 一点延迟 → 游戏用户建议关 (UI 文案也这么写)。
                    if (chaseBuf.size() < chN) { chaseBuf.assign(chN, 0.0f); chaseInit = false; }
                    if (!chaseInit) {
                        for (size_t i = 0; i < chN; i++) chaseBuf[i] = (float)frame[3 + i];
                        chaseInit = true;
                    } else {
                        double dt = chasePrevAt > 0 ? std::clamp(chNow - chasePrevAt, 1.0, 250.0) : 16.0;
                        double k = std::clamp(dt / kChaseSmoothTauMs, 0.0, 1.0);
                        for (size_t i = 0; i < chN; i++) {
                            double cur = chaseBuf[i];
                            cur += ((double)frame[3 + i] - cur) * k;
                            chaseBuf[i] = (float)cur;
                            frame[3 + i] = (uint8_t)std::clamp(cur + 0.5, 0.0, 255.0);
                        }
                    }
                }
                if (chaseGuard && chN > 0) {
                    // ② 抑制闪光: 整帧**平均亮度**的突变限幅 (每帧最多走 kChaseFlashGuard × 255)。
                    //    专治镜头切换/闪白: 颜色跟随仍然即时, 只有"整体亮度"被抹平 ✓
                    double sum = 0;
                    for (size_t i = 0; i < chN; i++) sum += frame[3 + i];
                    double actual = sum / (double)chN;
                    if (chaseMean >= 0.0) {
                        double step = kChaseFlashGuard * 255.0;
                        double target = std::clamp(actual, chaseMean - step, chaseMean + step);
                        if (std::abs(target - actual) > 0.5) {
                            double k = target / actual;                     // actual > 0 时才需要
                            if (k >= 0.0 && k < 1.0e6)
                                for (size_t i = 0; i < chN; i++)
                                    frame[3 + i] = (uint8_t)std::clamp((double)frame[3 + i] * k + 0.5,
                                                                       0.0, 255.0);
                        }
                    }
                    double s2 = 0;                                          // 记"实际写出的均值"
                    for (size_t i = 0; i < chN; i++) s2 += frame[3 + i];
                    chaseMean = s2 / (double)chN;
                } else {
                    chaseMean = -1.0;                                       // 关掉时不留状态
                }
                chasePrevAt = chNow;

                SnapshotFrame(frame, applied);
                allBlackRun = (cap.LastFrameAllBlack() && cap.LastFrameHadUpdate()) ? allBlackRun + 1 : 0;

                auto tw = steady_clock::now();
                bool ok = PushFrame(frame.data(), frame.size());
                lastFrameSend = tw;
                double wireMs = duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count();
                wireEma = wireEma == 0 ? wireMs : wireEma * 0.9 + wireMs * 0.1;
                if (!ok) { WireFail("write failed"); continue; }
                uint8_t sb;
                if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
                frameCounter++;
                fpsWindow++;
                if (waitDisp) ExitWaitDisp("desktop resumed");
                if (!waitDisp && phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
                // 诊断状态线 (60s/行, 只在追光模式): 用于定位"显示器灭了但灯还亮着" ——
                // 关键看 monitorPower 有没有跟着变、帧还在不在来、allBlack 有没有涨。
                {
                    double nowL = Millis();
                    if (nowL - lastStatusLog >= 60000.0) {
                        lastStatusLog = nowL;
                        int mpNow = C->monitorPower.load();
                        float fpsNow; unsigned long long fcNow; uint32_t abNow;
                        {
                            std::unique_lock lk(g->stMx);
                            fpsNow = g->st.fps;
                            fcNow = g->st.frameCounter;
                            abNow = g->st.allBlackFrames;
                        }
                        ACLOGD("status(trace): phase=%d monitorPower=%d fps=%.1f frames=%llu "
                               "allBlack=%u emptySkips=%llu lastFrameAgo=%.0fms",
                               phase, mpNow, fpsNow, fcNow, abNow,
                               (unsigned long long)cap.SkippedEmptyFrames(),
                               lastFrameAt > 0 ? nowL - lastFrameAt : -1.0);
                    }
                }
                if (applied.fpsCap > 0) {       // 帧率上限 (timeBeginPeriod(1) 已开)
                    double need = 1000.0 / applied.fpsCap;
                    double el = duration_cast<duration<double, std::milli>>(steady_clock::now() - t0).count();
                    if (el < need) WaitStop(g, (DWORD)(need - el));
                }
            } else if (stp == capture::CaptureSession::STEP_TIMEOUT) {
                // 静态屏: 不采样 (0 占用)。但帧只在桌面更新时发送, 若就此沉默,
                // 主机侧热改 (亮度/颜色链/灯序偏移) 在静止画面上永远不可见, 固件掉电
                // 丢帧也无法自愈 —— 每 200ms 重发最后一帧 (frame 即最近下发内容;
                // WAIT_DISP 除外, 那里由策略帧 2s 重发接管)。
                auto nowT = steady_clock::now();
                if (!waitDisp && serialOpen && frameCounter > 0 && nowT - lastFrameSend >= milliseconds(200)) {
                    if (!WriteDurable(frame.data(), frame.size(), "frame")) { WireFail("write failed"); continue; }
                    uint8_t sb;
                    if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
                    lastFrameSend = nowT;
                }
            } else {
                cap.Destroy();
                capValid = false;
                EnterWaitDisp(stp == capture::CaptureSession::STEP_ACCESS_DENIED ? "access denied (lock screen)"
                              : stp == capture::CaptureSession::STEP_ACCESS_LOST ? "access lost (topology)"
                                                                                 : "capture error",
                              /*graceOnWake*/ true);
                WaitStop(g, 200);               // 拓扑切换抖动抑制
            }
        } else {
            WaitStop(g, 50);
        }

        // ============ 8. WAIT_DISP 策略帧重发 (每 2s 防固件掉电丢帧) ============
        if (waitDisp && serialOpen) {
            auto now2 = steady_clock::now();
            if (now2 - lastPolicyResend >= milliseconds(2000)) {
                SendPolicyFrame(applied);
                lastPolicyResend = now2;
            }
        }

        // ============ 9. 状态快照刷新 ============
        {
            double el = duration_cast<duration<double>>(steady_clock::now() - fpsT0).count();
            std::unique_lock lk(g->stMx);
            g->st.deviceChanged = C->deviceChanged.load();
            g->st.monitorFallback = monitorFallback;
            g->st.allBlackFrames = allBlackRun;
            g->st.frameCounter = frameCounter;
            g->st.procMs = (float)procEma;
            g->st.wireMs = (float)wireEma;
            if (audioOn) {                      // 拾音音源健康/端点名 (UI 拾音页显示)
                std::lock_guard lk2(g_meterMx);
                if (g_meter) {
                    g->st.audioOk = g_meter->Healthy();
                    std::string n = g_meter->EndpointName();
                    strncpy_s(g->st.audioDev, n.c_str(), _TRUNCATE);
                }
            } else {
                g->st.audioOk = 0;
                g->st.audioDev[0] = 0;
                g->st.audioLevel = 0;
            }
            if (el >= 1.0) {
                g->st.fps = (float)(fpsWindow / el);
                fpsWindow = 0;
                fpsT0 = steady_clock::now();
            }
            if (capValid) {
                int m[4];
                cap.GetMargins(m);
                for (int i = 0; i < 4; i++) g->st.margins[i] = m[i];
            }
        }
    }

    // ---- 退出: 先淡出, 再关灯帧 + 清理
    // (MCU 模式下 lastOut 为空 —— 那 3 字节命令没有亮度通道, 无可淡, 与"MCU↔MCU 不能淡"同理)
    FadeOutToBlack(sp, lastOut, scratch, kFadeOutMs);
    if (audioOn) { audioOn = false; MeterRelease(); }
    SendOff();
    sp.Close();
    cap.Destroy();
    SetPhase(g, AC_STATE_STOPPED);
    ACLOGI("core thread exit");
}

void CoreLoop(Sess* g)
{
    SetThreadDescription(GetCurrentThread(), L"AmbientCore-Stream");
    __try {
        CoreLoopInner(g);
    } __except (CrashFilter("core", GetExceptionInformation())) {
        SetDiag(g, "ERROR", "core thread crashed, see log/ambientcore.log");
        SetPhase(g, AC_STATE_ERROR);
    }
}

// ---- slot 生命周期内部 (须持 g_lifeMx) --------------------------------
int32_t StartSlotLocked(uint32_t slot, const ACConfig* cfgIn)
{
    if (slot >= AC_MAX_DEVICES) return 1;
    ACConfig c{};
    int vr = ValidateConfig(cfgIn, &c);
    if (vr != 0) return vr;
    if (!g) g = new Core();

    Sess& s = g->slots[slot];
    if (s.active.load()) return 5;                 // 该 slot 已在运行
    if (!s.evStop) s.evStop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    else ResetEvent(s.evStop);
    {
        std::unique_lock stlk(s.stMx);
        memset(&s.st, 0, sizeof(s.st));
        s.st.devType = -1;
        s.st.devId = -1;
        s.diag[0] = 0;
        s.st.state = AC_STATE_STARTING;
    }
    {
        std::unique_lock cfglk(s.cfgMx);
        s.cfg = std::make_shared<const ACConfig>(c);
    }
    s.cfgSerial = 1;

    if (g->ActiveCount() == 0) {                   // 首个会话: 全局资源
        g->suspendEvt = 0; g->resumeEvt = 0; g->monitorPower = -1; g->testPattern = 0;
        g_powerParams.Context = g;
        if (!g->hSuspend)
            PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, &g_powerParams, &g->hSuspend);
        if (!g->hMonPower)
            PowerSettingRegisterNotification(&GUID_MONITOR_POWER_ON, DEVICE_NOTIFY_CALLBACK,
                                             &g_powerParams, &g->hMonPower);
            // ⚠️ GUID_MONITOR_POWER_ON 自 Win8 起已废弃 (现代 Windows 根本不发它) →
            // 必须**同时**注册 GUID_CONSOLE_DISPLAY_STATE, 否则"显示器灭了但灯还亮着"
            // (只能靠采集侧的 access-lost 兜底, 会滞后甚至不发生)。用户实测现象即此。
            PowerSettingRegisterNotification(&GUID_CONSOLE_DISPLAY_STATE, DEVICE_NOTIFY_CALLBACK,
                                             &g_powerParams, &g->hMonPower2);
        timeBeginPeriod(1);                        // Sleep 精度 15.6ms -> 1ms
        if (!g->evStopMon) g->evStopMon = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        else ResetEvent(g->evStopMon);
        g->monitorThread = std::thread(MonitorLoop);
    }
    s.active = 1;
    s.started = 1;
    s.thread = std::thread(CoreLoop, &s);
    ACLOGI("AC_StartEx slot %u ok (mode %d, com '%s', leds %d)",
           slot, c.modeIndex, c.comPort, c.ledCount);
    return 0;
}

void StopSlotLocked(uint32_t slot)
{
    if (!g || slot >= AC_MAX_DEVICES) return;
    Sess& s = g->slots[slot];
    if (!s.active.load()) return;
    ACLOGI("AC_StopEx slot %u", slot);
    SetEvent(s.evStop);
    if (s.thread.joinable()) s.thread.join();
    s.active = 0;
    {
        std::unique_lock lk2(s.stMx);
        s.st.state = AC_STATE_STOPPED;
    }
    if (g->ActiveCount() == 0) {                   // 最后一个会话: 释放全局资源
        timeEndPeriod(1);
        if (g->hSuspend) { PowerUnregisterSuspendResumeNotification(g->hSuspend); g->hSuspend = nullptr; }
        if (g->hMonPower) { PowerUnregisterSuspendResumeNotification(g->hMonPower); g->hMonPower = nullptr; }
        if (g->hMonPower2) { PowerUnregisterSuspendResumeNotification(g->hMonPower2); g->hMonPower2 = nullptr; }
        if (g->evStopMon) SetEvent(g->evStopMon);
        if (g->monitorThread.joinable()) g->monitorThread.join();
    }
}

} // namespace

// ======================================================================
// 导出函数
// ======================================================================

AC_API uint32_t AC_Version(void) { return kVersion; }

// ---- 兼容别名: 单设备 = slot 0 ----------------------------------------
AC_API int32_t AC_Start(const ACConfig* cfg)
{
    std::lock_guard lk(g_lifeMx);
    return StartSlotLocked(0, cfg);
}

AC_API void AC_Stop(void)
{
    std::lock_guard lk(g_lifeMx);
    if (!g) return;
    for (uint32_t i = 0; i < AC_MAX_DEVICES; i++) StopSlotLocked(i);
}

AC_API int32_t AC_SetConfig(const ACConfig* cfg) { return AC_SetConfigEx(0, cfg); }

AC_API void AC_GetStatus(ACStatus* out) { AC_GetStatusEx(0, out); }

AC_API int32_t AC_GetLastDiag(char* buf, uint32_t cap) { return AC_GetLastDiagEx(0, buf, cap); }

// ---- 多会话 (M3) -------------------------------------------------------
AC_API int32_t AC_StartEx(uint32_t slot, const ACConfig* cfg)
{
    std::lock_guard lk(g_lifeMx);
    return StartSlotLocked(slot, cfg);
}

AC_API void AC_StopEx(uint32_t slot)
{
    std::lock_guard lk(g_lifeMx);
    StopSlotLocked(slot);
}

AC_API int32_t AC_SetConfigEx(uint32_t slot, const ACConfig* cfgIn)
{
    if (slot >= AC_MAX_DEVICES) return 1;
    if (!cfgIn) return 1;
    ACConfig c{};
    if (ValidateConfig(cfgIn, &c) != 0) return 1;
    std::lock_guard lk(g_lifeMx);
    if (!g || !g->slots[slot].active.load()) return 0;   // 未运行: 仅校验 (Start 时传入配置)
    {
        std::unique_lock lk2(g->slots[slot].cfgMx);
        g->slots[slot].cfg = std::make_shared<const ACConfig>(c);
        g->slots[slot].cfgSerial++;
    }
    ACLOGD("AC_SetConfigEx slot %u (serial %llu, mode %d, com '%s', leds %d)",
           slot, (unsigned long long)g->slots[slot].cfgSerial.load(), c.modeIndex, c.comPort, c.ledCount);
    return 0;
}

AC_API void AC_GetStatusEx(uint32_t slot, ACStatus* out)
{
    if (!out) return;
    out->structSize = sizeof(ACStatus);
    if (!g || slot >= AC_MAX_DEVICES || !g->slots[slot].started.load()) {
        memset(out, 0, sizeof(*out));
        out->structSize = sizeof(ACStatus);
        out->devType = -1;
        out->devId = -1;
        out->state = AC_STATE_STOPPED;
        return;
    }
    Sess& s = g->slots[slot];
    std::shared_lock lk(s.stMx);
    ACStatus tmp = s.st;
    tmp.structSize = sizeof(ACStatus);
    *out = tmp;
}

AC_API int32_t AC_GetLastDiagEx(uint32_t slot, char* buf, uint32_t cap)
{
    if (!buf || cap == 0) return 0;
    buf[0] = 0;
    if (!g || slot >= AC_MAX_DEVICES) return 0;
    Sess& s = g->slots[slot];
    std::shared_lock lk(s.stMx);
    strncpy_s(buf, cap, s.diag, _TRUNCATE);
    return s.diag[0] ? 1 : 0;
}

AC_API int32_t AC_GetMonitors(ACMonitorInfo* out, uint32_t max, int32_t* count)
{
    if (!count) return 1;
    *count = 0;
    std::vector<capture::MonitorDesc> mons;
    capture::EnumerateMonitors(mons);
    uint32_t n = 0;
    for (auto& m : mons) {
        if (n >= max) break;
        if (out) {
            ACMonitorInfo& d = out[n];
            memset(&d, 0, sizeof(d));
            strncpy_s(d.name, WideToUtf8(m.name).c_str(), _TRUNCATE);
            strncpy_s(d.adapter, WideToUtf8(m.adapter).c_str(), _TRUNCATE);
            d.width = m.w;
            d.height = m.h;
            d.primary = m.primary;
        }
        n++;
    }
    *count = (int32_t)n;
    return 0;
}

AC_API int32_t AC_GetDevices(ACDeviceInfo* out, uint32_t max, int32_t* count)
{
    if (!count) return 1;
    *count = 0;
    std::vector<serial::ComPortInfo> ports;
    serial::EnumerateVidPidPorts(ports);
    uint32_t n = 0;
    for (auto& p : ports) {
        if (n >= max) break;
        if (out) {
            ACDeviceInfo& d = out[n];
            memset(&d, 0, sizeof(d));
            strncpy_s(d.comPort, WideToUtf8(p.port).c_str(), _TRUNCATE);
            d.devType = -1;
            d.devId = -1;
            d.inUse = 0;
            uint8_t resp = 0;
            int pr = serial::ProbeDevice(p.port, 2000000, resp);
            if (pr == -1) {
                d.inUse = 1;                       // 本核心或其他进程占用
            } else if (pr == 0) {
                d.devType = (resp >> 2) & 3;
                d.devId = resp & 3;
            }                                      // -2 = 握手失败: 设备在位但不应答
        }
        n++;
    }
    *count = (int32_t)n;
    return 0;
}

AC_API int32_t AC_SetTestPattern(int32_t mode)
{
    if (mode < 0 || mode > 2) return 1;
    if (!g) return 1;
    g->testPattern = mode;
    ACLOGI("test pattern -> %d", mode);
    return 0;
}

AC_API int32_t AC_GetAudioEndPoints(int32_t capture, ACAudioDevInfo* out, uint32_t max, int32_t* count)
{
    if (!count) return 1;
    *count = 0;
    HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    int32_t rc = 0;
    {
        ComPtr<IMMDeviceEnumerator> en;
        HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                      __uuidof(IMMDeviceEnumerator), (void**)en.GetAddressOf());
        if (FAILED(hr)) {
            if (SUCCEEDED(co)) CoUninitialize();
            return 6;                               // D3D/内部错误族: COM 不可用
        }
        // capture=1 → eCapture (麦克风/线路输入); 0 → eRender (输出)
        ComPtr<IMMDeviceCollection> col;
        hr = en->EnumAudioEndpoints(capture ? eCapture : eRender,
                                    DEVICE_STATE_ACTIVE, &col);
        if (FAILED(hr)) {
            if (SUCCEEDED(co)) CoUninitialize();
            return 6;
        }
        UINT n = 0;
        col->GetCount(&n);
        // 默认端点 ID (isDefault 标记用)
        ComPtr<IMMDevice> def;
        std::string defId;
        if (SUCCEEDED(en->GetDefaultAudioEndpoint(capture ? eCapture : eRender, eConsole, &def))) {
            LPWSTR p = nullptr;
            if (SUCCEEDED(def->GetId(&p)) && p) {
                int m = WideCharToMultiByte(CP_UTF8, 0, p, -1, nullptr, 0, nullptr, nullptr);
                if (m > 1) {
                    defId.resize((size_t)m - 1);
                    WideCharToMultiByte(CP_UTF8, 0, p, -1, defId.data(), m, nullptr, nullptr);
                }
                CoTaskMemFree(p);
            }
        }
        uint32_t k = 0;
        for (UINT i = 0; i < n && k < max; i++) {
            ComPtr<IMMDevice> dev;
            if (FAILED(col->Item(i, &dev))) continue;
            LPWSTR pid = nullptr;
            if (FAILED(dev->GetId(&pid)) || !pid) continue;
            ACAudioDevInfo* d = out ? &out[k] : nullptr;
            if (d) {
                memset(d, 0, sizeof(*d));
                int m = WideCharToMultiByte(CP_UTF8, 0, pid, -1, nullptr, 0, nullptr, nullptr);
                if (m > 1) {
                    std::string id8((size_t)m - 1, 0);
                    WideCharToMultiByte(CP_UTF8, 0, pid, -1, id8.data(), m, nullptr, nullptr);
                    strncpy_s(d->id, id8.c_str(), _TRUNCATE);
                }
                // 友好名
                ComPtr<IPropertyStore> ps;
                if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &ps))) {
                    PROPVARIANT v;
                    PropVariantInit(&v);
                    if (SUCCEEDED(ps->GetValue(kPKEY_Name, &v)) && v.vt == VT_LPWSTR && v.pwszVal) {
                        int w = WideCharToMultiByte(CP_UTF8, 0, v.pwszVal, -1, nullptr, 0, nullptr, nullptr);
                        if (w > 1) {
                            std::string nm((size_t)w - 1, 0);
                            WideCharToMultiByte(CP_UTF8, 0, v.pwszVal, -1, nm.data(), w, nullptr, nullptr);
                            strncpy_s(d->name, nm.c_str(), _TRUNCATE);
                        }
                    }
                    PropVariantClear(&v);
                }
                d->isDefault = (defId == d->id) ? 1 : 0;
            }
            CoTaskMemFree(pid);
            k++;
        }
        *count = (int32_t)k;
    }
    if (SUCCEEDED(co)) CoUninitialize();
    return rc;
}

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

constexpr uint32_t kVersion = (1u << 16) | (5u << 8) | 0u;   // 1.5.0 (ACMonitorInfo.adapter 32→64, 显卡名不再截断)

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

    HPOWERNOTIFY hSuspend = nullptr, hMonPower = nullptr;

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
    std::unique_lock lk(g->stMx);
    g->st.state = state;
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
    out->fpsCap       = std::max(0, out->fpsCap);
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
        g->suspendEvt = 1;
    } else if (type == PBT_APMRESUMEAUTOMATIC || type == PBT_APMRESUMESUSPEND) {
        g->resumeEvt = 1;
    } else if (type == PBT_POWERSETTINGCHANGE) {
        auto* s = static_cast<POWERBROADCAST_SETTING*>(setting);
        if (s->PowerSetting == GUID_MONITOR_POWER_ON && s->DataLength >= sizeof(DWORD)) {
            DWORD v = *reinterpret_cast<const DWORD*>(s->Data);
            g->monitorPower = (int)v;
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
            ACLOGI("monitor topology changed (%zu -> %zu outputs)", lastTopo.size(), topo.size());
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
    auto MakeColorParams = [](const ACConfig& c) {
        color::Params p;
        p.brightness = c.brightness;
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

    auto SendOff = [&]() {
        if (!sp.IsOpen()) return;
        std::vector<uint8_t> z;
        serial::BuildZeroFrame(z);
        sp.Write(z.data(), (DWORD)z.size());
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
        sp.Write(frame.data(), (DWORD)frame.size());
    };

    auto EnterWaitDisp = [&](const char* why) {
        if (!waitDisp) {
            waitDisp = true;
            ACLOGI("enter WAIT_DISP (%s), policy=%d", why, applied.waitDispPolicy);
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
                ACLOGW("open %ls failed (busy or gone)", cand.c_str());
                continue;
            }
            uint8_t resp = 0;
            if (serial::Handshake(sp, resp)) {
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
            ACLOGW("handshake failed on %ls", cand.c_str());
        }
        return false;
    };

    SetPhase(g, phase);
    for (;;) {
        if (WaitStop(g, 0)) break;                   // 停止请求 → 走退出清理

        // ============ 1. 挂起 / 恢复 ============
        if (C->resumeEvt.exchange(0)) {
            ACLOGI("resume: full rebuild (adapter -> output -> D3D -> DDA -> serial)");
            cap.Destroy(); capValid = false;
            sp.Close(); serialOpen = false;
            waitDisp = false;
            fastLeft = 5;                       // 唤醒后快速期优先
            hardGraceUntil = steady_clock::now() + seconds(3);
            phase = AC_STATE_STARTING;
            SetPhase(g, phase);
        }
        if (C->suspendEvt.exchange(0)) {
            ACLOGI("suspend: sending off-frame, stopping stream");
            SendOff();                          // 防半帧/端口消失中途写
            if (audioOn) { audioOn = false; MeterRelease(); }   // 挂起期间停止音源重建循环
            cap.Destroy(); capValid = false;
            sp.Close(); serialOpen = false;
            waitDisp = false;
            phase = AC_STATE_STOPPED;
            SetPhase(g, phase);
            while (!WaitStop(g, 200) && !C->resumeEvt.load()) {}
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
            // cols 必须与新灯数同步, 否则 Step 的 memcpy 越界写堆 (曾致 CLR 堆损坏)
            cols.resize((size_t)std::min(nc.ledCount, serial::kMaxLedCount) + 4);
            if (nc.modeIndex != applied.modeIndex)
                ACLOGI("config: mode %d -> %d", applied.modeIndex, nc.modeIndex);
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
            if (waitDisp) {                     // 拾音不受熄屏/锁屏策略影响
                waitDisp = false;
                ACLOGI("audio mode -> exit WAIT_DISP");
            }
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
                ACLOGI("topology change -> full capture rebuild");
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
            } else {
                if (phase != AC_STATE_DEV_LOST)
                    SetDiag(g, "WARN ", "device lost: no usable port (VID_2E3C&PID_5740)");
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
            if (!sp.Write(frame.data(), (DWORD)frame.size())) { WireFail("write failed"); continue; }
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
                if (waitDisp) {
                    waitDisp = false;
                    ACLOGI("capture built -> exit WAIT_DISP");
                }
                if (phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
                ACLOGI("capture built: monitor %d%s (%dx%d hdr=%d)", applied.monitorIndex,
                       fb ? " (fallback->primary)" : "", cap.Width(), cap.Height(), 0);
            } else if (res == capture::CaptureSession::BUILD_TRANSIENT ||
                       steady_clock::now() < hardGraceUntil) {
                EnterWaitDisp(res == capture::CaptureSession::BUILD_TRANSIENT
                                  ? "capture unavailable"
                                  : "capture not ready (device warm-up)");
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
            color::AdjustLedColors(MakeColorParams(applied),
                                   applied.solidRgb[0] / 255.0, applied.solidRgb[1] / 255.0,
                                   applied.solidRgb[2] / 255.0, rgb);
            for (int i = 0; i < EffLeds(applied); i++) {
                frame[3 + i * 3 + 0] = rgb[0];
                frame[3 + i * 3 + 1] = rgb[1];
                frame[3 + i * 3 + 2] = rgb[2];
            }
            SnapshotFrame(frame, applied);
            auto tw = steady_clock::now();
            if (!sp.Write(frame.data(), (DWORD)frame.size())) { WireFail("write failed"); continue; }
            wireEma = wireEma == 0 ? duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count()
                                   : wireEma * 0.9 + duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count() * 0.1;
            uint8_t sb;
            if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
            frameCounter++;
            fpsWindow++;
            if (waitDisp) { waitDisp = false; ACLOGI("content resumed -> exit WAIT_DISP"); }
            if (phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
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
            double cap = std::clamp(applied.brightness, 0.0, 1.0);
            double v = (double)env * applied.audioGain * cap *
                       (applied.audioFollowVolume ? master : 1.0f);
            uint8_t b = (uint8_t)std::clamp((int)(v + 0.5), 0, 255);
            const uint8_t f6[6] = { 'A', 'd', 'z', b, b, b };
            auto tw = steady_clock::now();
            if (!sp.Write(f6, 6)) { WireFail("write failed"); continue; }
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
            if (phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
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
                if (!sp.Write(cmd, 3)) { WireFail("write failed"); continue; }
                double wireMs = duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count();
                wireEma = wireEma == 0 ? wireMs : wireEma * 0.9 + wireMs * 0.1;
                lastMcuCmd = now1;
                frameCounter++;
                fpsWindow++;
                if (waitDisp) { waitDisp = false; ACLOGI("MCU mode -> exit WAIT_DISP"); }
                if (phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
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
                EnsureFrame();                   // 线帧缓冲 ("Ada" + 543B, 尾部补零)
                color::Params cp = MakeColorParams(applied);
                for (int i = 0; i < EffLeds(applied); i++) {
                    uint8_t px[3];
                    color::AdjustLedColors(cp, cols[i].x, cols[i].y, cols[i].z, px);
                    frame[3 + i * 3 + 0] = px[0];
                    frame[3 + i * 3 + 1] = px[1];
                    frame[3 + i * 3 + 2] = px[2];
                }
                SnapshotFrame(frame, applied);
                allBlackRun = (cap.LastFrameAllBlack() && cap.LastFrameHadUpdate()) ? allBlackRun + 1 : 0;

                auto tw = steady_clock::now();
                bool ok = sp.Write(frame.data(), (DWORD)frame.size());
                lastFrameSend = tw;
                double wireMs = duration_cast<duration<double, std::milli>>(steady_clock::now() - tw).count();
                wireEma = wireEma == 0 ? wireMs : wireEma * 0.9 + wireMs * 0.1;
                if (!ok) { WireFail("write failed"); continue; }
                uint8_t sb;
                if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
                frameCounter++;
                fpsWindow++;
                if (waitDisp) { waitDisp = false; ACLOGI("desktop resumed -> RUNNING"); }
                if (phase != AC_STATE_RUNNING) { phase = AC_STATE_RUNNING; SetPhase(g, phase); }
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
                    if (!sp.Write(frame.data(), (DWORD)frame.size())) { WireFail("write failed"); continue; }
                    uint8_t sb;
                    if (sp.PollStatusByte(sb)) { std::unique_lock lk(g->stMx); g->st.ledStatusByte = sb; }
                    lastFrameSend = nowT;
                }
            } else {
                cap.Destroy();
                capValid = false;
                EnterWaitDisp(stp == capture::CaptureSession::STEP_ACCESS_DENIED ? "access denied (lock screen)"
                              : stp == capture::CaptureSession::STEP_ACCESS_LOST ? "access lost (topology)"
                                                                                 : "capture error");
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

    // ---- 退出: 关灯帧 + 清理
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
    ACLOGI("AC_SetConfigEx slot %u (serial %llu, mode %d, com '%s', leds %d)",
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

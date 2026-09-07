// audio.cpp — WASAPI 回环拾音实现 (见 audio.h 头注释)
#include "audio.h"
#include "log.h"

#include <mmdeviceapi.h>
#include <audioclient.h>
#include <endpointvolume.h>
#include <wrl/client.h>

#include <cmath>
#include <cstring>

#pragma comment(lib, "ole32.lib")

using namespace std::chrono;
using Microsoft::WRL::ComPtr;

namespace audio {

// 本地常量定义 (避免 initguid 引入顺序问题):
// KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
static const GUID kIeeeFloat = { 0x00000003, 0x0000, 0x0010,
                                 { 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 } };
// PKEY_Device_FriendlyName
static const PROPERTYKEY kPKEY_Name = { { 0xa45c254e, 0xdf1c, 0x4efd,
                                          { 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0 } }, 14 };

// ---- 默认输出/输入设备通知 (MTA; 生命周期归捕获线程, Release 不 delete) --------
class EndpointEvents : public IMMNotificationClient {
public:
    std::atomic<int> renderChanged{ 0 };
    std::atomic<int> captureChanged{ 0 };

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == __uuidof(IMMNotificationClient)) {
            *ppv = static_cast<IMMNotificationClient*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++ref_; }
    STDMETHODIMP_(ULONG) Release() override { return --ref_; }

    STDMETHODIMP OnDeviceStateChanged(LPCWSTR, DWORD) override { return S_OK; }
    STDMETHODIMP OnDeviceAdded(LPCWSTR) override { return S_OK; }
    STDMETHODIMP OnDeviceRemoved(LPCWSTR) override { return S_OK; }
    STDMETHODIMP OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR) override {
        if (role != eConsole) return S_OK;
        if (flow == eRender) renderChanged.store(1);
        else if (flow == eCapture) captureChanged.store(1);
        return S_OK;
    }
    STDMETHODIMP OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }

private:
    LONG ref_ = 1;
};

// ---- 会话对象 (全部归捕获线程) ---------------------------------------
namespace {
struct Session {
    ComPtr<IMMDeviceEnumerator>  enumerator;
    ComPtr<IMMDevice>            device;
    ComPtr<IAudioClient>         client;
    ComPtr<IAudioCaptureClient>  capture;
    ComPtr<IAudioEndpointVolume> vol;
    WAVEFORMATEX* wfx = nullptr;
    UINT32 channels = 2;
    double fs = 48000.0;
    HANDLE evCapture = nullptr;                     // Meter::evCapture_ 别名
    bool mic = false;                               // false=渲染回环 true=麦克风采集
    EndpointEvents events;
};
} // namespace

static std::string ReadEndpointName(IMMDevice* dev)
{
    std::string out = "默认输出设备";
    if (!dev) return out;
    ComPtr<IPropertyStore> ps;
    if (FAILED(dev->OpenPropertyStore(STGM_READ, &ps))) return out;
    PROPVARIANT v;
    PropVariantInit(&v);
    if (SUCCEEDED(ps->GetValue(kPKEY_Name, &v)) && v.vt == VT_LPWSTR && v.pwszVal) {
        int n = WideCharToMultiByte(CP_UTF8, 0, v.pwszVal, -1, nullptr, 0, nullptr, nullptr);
        if (n > 1) {
            out.resize((size_t)n - 1);
            WideCharToMultiByte(CP_UTF8, 0, v.pwszVal, -1, out.data(), n, nullptr, nullptr);
        }
    }
    PropVariantClear(&v);
    return out;
}

// ---- 生命周期 --------------------------------------------------------
Meter::~Meter() { Stop(); }

void Meter::Start()
{
    if (running_.exchange(true)) return;
    InitializeCriticalSectionAndSpinCount(&paramsCs_, 0x400);
    InitializeCriticalSectionAndSpinCount(&nameCs_, 0x400);
    evStop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    evCapture_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    thread_ = std::thread(&Meter::ThreadLoop, this);
}

void Meter::Stop()
{
    if (!running_.exchange(false)) return;
    if (evStop_) SetEvent(evStop_);
    if (thread_.joinable()) thread_.join();
    if (evCapture_) { CloseHandle(evCapture_); evCapture_ = nullptr; }
    if (evStop_) { CloseHandle(evStop_); evStop_ = nullptr; }
    DeleteCriticalSection(&nameCs_);
    DeleteCriticalSection(&paramsCs_);
    envByte_ = 0;
    healthy_ = 0;
}

void Meter::SetParams(const Params& p)
{
    EnterCriticalSection(&paramsCs_);
    params_ = p;
    LeaveCriticalSection(&paramsCs_);
}

void Meter::SetSource(bool microphone)
{
    wantMic_ = microphone ? 1 : 0;
}

std::string Meter::EndpointName() const
{
    EnterCriticalSection(const_cast<CRITICAL_SECTION*>(&nameCs_));
    std::string s = endpointName_;
    LeaveCriticalSection(const_cast<CRITICAL_SECTION*>(&nameCs_));
    return s;
}

// ---- 会话建立/拆除 ----------------------------------------------------
static bool InitSession(Session& s, std::string* nameOut)
{
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  __uuidof(IMMDeviceEnumerator), (void**)s.enumerator.GetAddressOf());
    if (FAILED(hr)) return false;
    s.enumerator->RegisterEndpointNotificationCallback(&s.events);
    // 麦克风 = 采集端点 (普通共享捕获); 回环 = 渲染端点 (AUDCLNT_STREAMFLAGS_LOOPBACK)
    EDataFlow flow = s.mic ? eCapture : eRender;
    hr = s.enumerator->GetDefaultAudioEndpoint(flow, eConsole, &s.device);
    if (FAILED(hr)) return false;
    *nameOut = ReadEndpointName(s.device.Get());
    if (FAILED(s.device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_ALL,
                                  nullptr, (void**)s.vol.GetAddressOf())))
        s.vol = nullptr;                            // 主音量读不到 → 跟随音量功能退化, 不阻塞
    if (FAILED(s.device->Activate(__uuidof(IAudioClient), CLSCTX_ALL,
                                  nullptr, (void**)s.client.GetAddressOf()))) return false;
    if (FAILED(s.client->GetMixFormat(&s.wfx)) || !s.wfx) return false;

    // 仅支持 float32 混音格式 (Win7+ 共享模式恒为 IEEE float; 保守校验)
    const WAVEFORMATEX* w = s.wfx;
    bool isFloat = w->wFormatTag == 3;              // WAVE_FORMAT_IEEE_FLOAT
    if (!isFloat && w->wFormatTag == 0xFFFE) {      // WAVE_FORMAT_EXTENSIBLE
        auto* e = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(w);
        isFloat = e->SubFormat == kIeeeFloat;
    }
    if (!isFloat || w->nChannels == 0 || w->nSamplesPerSec == 0) {
        ACLOGW("audio: unsupported mix format (tag %u, ch %u, %u Hz)",
               w->wFormatTag, w->nChannels, w->nSamplesPerSec);
        return false;
    }
    s.channels = w->nChannels;
    s.fs = (double)w->nSamplesPerSec;

    // 事件驱动共享模式; 缓冲 1s 足够抗抖, 事件节拍仍为设备周期 (~10ms)
    DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
    if (!s.mic) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
    hr = s.client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 10000000, 0, w, nullptr);
    if (FAILED(hr)) return false;
    if (FAILED(s.client->SetEventHandle(s.evCapture))) return false;
    if (FAILED(s.client->GetService(__uuidof(IAudioCaptureClient),
                                    (void**)s.capture.GetAddressOf()))) return false;
    if (FAILED(s.client->Start())) return false;
    return true;
}

static void TeardownSession(Session& s)
{
    if (s.client) s.client->Stop();
    s.capture.Reset();
    s.vol.Reset();
    s.client.Reset();
    s.device.Reset();
    if (s.wfx) { CoTaskMemFree(s.wfx); s.wfx = nullptr; }
    if (s.enumerator) s.enumerator->UnregisterEndpointNotificationCallback(&s.events);
    s.enumerator.Reset();
}

// ---- 包络 -------------------------------------------------------------
void Meter::StepEnvelope(double target, double dtMs)
{
    if (target > env_) {
        // 起音平滑 (10-100ms 随柔和度), 消除逐包硬跳变带来的频闪感
        double tauA = 10.0 + 90.0 * live_.soft;
        env_ += (target - env_) * (1.0 - exp(-dtMs / tauA));
        if (env_ > 1.0) env_ = 1.0;
    } else {
        env_ *= exp(-dtMs / (live_.release * 1000.0 + 1e-9)); // 慢落 (用户可调)
    }
    if (env_ < 0.0005) env_ = 0;
    envByte_ = (uint8_t)(env_ * 255.0 + 0.5);
}

void Meter::ProcessPacket(const uint8_t* data, uint32_t frames, uint32_t blockAlign,
                          uint32_t channels, bool silent, double dtMs)
{
    double sqF = 0, sqB = 0;
    if (!silent && data) {
        const double a = aLp_;
        for (uint32_t f = 0; f < frames; f++) {
            const float* s = reinterpret_cast<const float*>(data + (size_t)f * blockAlign);
            double mono = 0, peak = 0;
            for (uint32_t c = 0; c < channels; c++) {
                mono += (double)s[c];
                double v = fabs((double)s[c]);
                if (v > peak) peak = v;
            }
            mono /= (double)channels;
            sqF += peak * peak;
            lp1_ += a * (mono - lp1_);                 // 120Hz 双一阶低通 → 低频能量
            lp2_ += a * (lp1_ - lp2_);
            sqB += lp2_ * lp2_;
        }
    }
    double fullRms = frames ? sqrt(sqF / (double)frames) : 0.0;
    double bassRms = frames ? sqrt(sqB / (double)frames) : 0.0;

    if (live_.agc) {
        // 慢速峰值跟踪 (tau≈5s) + 地板: 最近峰值 → 归一 1.0
        double dec = exp(-dtMs / 5000.0);
        peakF_ = fullRms > peakF_ * dec ? fullRms : peakF_ * dec;
        peakB_ = bassRms > peakB_ * dec ? bassRms : peakB_ * dec;
        if (peakF_ < 0.02) peakF_ = 0.02;
        if (peakB_ < 0.006) peakB_ = 0.006;
    } else {
        peakF_ = peakB_ = 1.0;
    }
    double target = (1.0 - live_.bass) * (fullRms / peakF_) + live_.bass * (bassRms / peakB_);
    if (target > 1.0) target = 1.0;
    if (fullRms < live_.gate) {
        target = 0;                        // 绝对门限在 AGC 之前取值 (防底噪放大); 静默→包络落 0
    } else {
        // 柔和度整形 (仅作用于有声段, 门限之下不抬升):
        //   gamma 1→0.5 抬中段 (鼓点间隙不再深黑), 呼吸下限 0→15% (消灭大幅亮灭频闪)
        double shaped = pow(target, 1.0 - 0.5 * live_.soft);
        double fl = 0.15 * live_.soft;
        target = fl + (1.0 - fl) * shaped;
        if (target > 1.0) target = 1.0;
    }
    StepEnvelope(target, dtMs);
}

// ---- 捕获线程 ---------------------------------------------------------
void Meter::ThreadLoop()
{
    SetThreadDescription(GetCurrentThread(), L"AmbientCore-Audio");
    HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    Session s;
    s.evCapture = evCapture_;
    auto lastPacket = steady_clock::now() - seconds(10);
    auto lastVolPoll = steady_clock::now() - seconds(10);
    bool loggedNoDev = false;

    for (;;) {
        healthy_ = 2;
        std::string name;
        s.mic = wantMic_.load() != 0;
        if (!InitSession(s, &name)) {
            healthy_ = 0;
            envByte_ = 0;
            if (!loggedNoDev) {
                ACLOGW("audio: no usable %s endpoint, retrying (500ms)",
                       s.mic ? "capture (mic)" : "render (loopback)");
                loggedNoDev = true;
            }
            TeardownSession(s);
            if (WaitForSingleObject(evStop_, 500) != WAIT_TIMEOUT) break;
            continue;
        }
        loggedNoDev = false;
        {
            EnterCriticalSection(&nameCs_);
            endpointName_ = name;
            LeaveCriticalSection(&nameCs_);
        }
        EnterCriticalSection(&paramsCs_);
        live_ = params_;
        LeaveCriticalSection(&paramsCs_);
        aLp_ = exp(-2.0 * 3.14159265358979 * 120.0 / s.fs);
        lp1_ = lp2_ = 0;
        peakF_ = 0.02;
        peakB_ = 0.006;
        env_ = 0;
        ACLOGI("audio: %s on '%s' (%u ch, %.0f Hz)",
               s.mic ? "mic" : "loopback", name.c_str(), s.channels, s.fs);
        healthy_ = 1;
        lastPacket = steady_clock::now();

        bool rebuild = false, stopReq = false;
        while (!rebuild && !stopReq) {
            HANDLE hs[2] = { evStop_, evCapture_ };
            DWORD w = WaitForMultipleObjects(2, hs, FALSE, 10);
            if (w == WAIT_OBJECT_0) { stopReq = true; break; }
            auto now1 = steady_clock::now();

            if (wantMic_.load() != 0 != s.mic) {
                ACLOGI("audio: source switch (%s) -> rebuild", s.mic ? "mic->loopback" : "loopback->mic");
                rebuild = true;
                break;
            }
            if ((s.mic ? s.events.captureChanged : s.events.renderChanged).exchange(0)) {
                ACLOGI("audio: default %s endpoint changed -> rebuild",
                       s.mic ? "capture" : "render");
                rebuild = true;
                break;
            }

            // 参数热更新 (每轮取一次, 成本可忽略)
            EnterCriticalSection(&paramsCs_);
            live_ = params_;
            LeaveCriticalSection(&paramsCs_);

            // 排空当前可用的全部包
            for (;;) {
                UINT32 pk = 0;
                if (FAILED(s.capture->GetNextPacketSize(&pk))) {
                    ACLOGW("audio: GetNextPacketSize failed -> rebuild");
                    rebuild = true;
                    break;
                }
                if (pk == 0) break;
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                UINT64 devPos = 0, qpc = 0;
                if (FAILED(s.capture->GetBuffer(&data, &frames, &flags, &devPos, &qpc))) {
                    ACLOGW("audio: GetBuffer failed -> rebuild");
                    rebuild = true;
                    break;
                }
                double dtMs = (double)frames * 1000.0 / s.fs;
                ProcessPacket(data, frames, s.wfx->nBlockAlign, s.channels,
                              (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0, dtMs);
                s.capture->ReleaseBuffer(frames);
                lastPacket = steady_clock::now();
            }
            if (rebuild) break;

            // 无包看门狗: 系统静默时 WASAPI 不再投递事件 → 包络按 release 归零
            if (duration_cast< milliseconds >(steady_clock::now() - lastPacket).count() > 40)
                StepEnvelope(0, 10.0);

            // 主音量 10Hz 轮询 (跟随音量用)
            if (s.vol && duration_cast< milliseconds >(now1 - lastVolPoll).count() >= 100) {
                float v = 1.0f;
                if (SUCCEEDED(s.vol->GetMasterVolumeLevelScalar(&v))) masterVol_ = v;
                lastVolPoll = now1;
            }
        }

        TeardownSession(s);
        envByte_ = 0;
        healthy_ = 2;
        if (stopReq) break;
        if (WaitForSingleObject(evStop_, 500) != WAIT_TIMEOUT) break;   // 重建退避
    }

    TeardownSession(s);
    envByte_ = 0;
    healthy_ = 0;
    if (SUCCEEDED(co)) CoUninitialize();
    ACLOGI("audio: thread exit");
}

} // namespace audio

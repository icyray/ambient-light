#include "capture.h"
#include "serial.h"
#include "log.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <cstring>
#include <cmath>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "d3dcompiler.lib")

using Microsoft::WRL::ComPtr;

namespace capture {

// ---------------------------------------------------------------- HLSL
// 每颗灯一个线程采样边缘矩形均值; 附加 4 个黑边探测线程 (N..N+3)
static const char* g_hlsl = R"HLSL(
Texture2D<float4>          DeskTex  : register(t0);
StructuredBuffer<float4>   LedRects : register(t1);   // x0,y0,x1,y1 (px, x1/y1 exclusive)
RWStructuredBuffer<float4> OutCols  : register(u0);   // [0..N)=灯色, [N..N+4)=黑边探测
cbuffer P : register(b0) {
    uint N; uint TX; uint TY; uint HDR;
    uint TEST; uint BLvl; uint Wpx; uint Hpx;
    uint4 Margs;                                    // 黑边 margin: x=底 y=右 z=顶 w=左
    uint pad0; uint pad1; uint pad2; uint pad3;
};

[numthreads(64,1,1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    uint i = tid.x;
    if (i >= N + 4u) return;

    // ---- 黑边探测 (每个边一个线程): 检查候选边界行是否有内容 / 当前边界行是否全黑
    if (i >= N) {
        uint side = i - N;                          // 0=底 1=右 2=顶 3=左
        uint m    = (side == 0u) ? Margs.x : (side == 1u) ? Margs.y
                  : (side == 2u) ? Margs.z : Margs.w;
        uint cand = (m >= 20u) ? m - 15u : m;       // 候选边界 (CPU 侧同样限制 m-15>=5)
        float found = 0.0f, allB = 1.0f;
        if (side == 0u || side == 2u) {             // 横边: 沿 x 扫描
            for (uint x = 0u; x < Wpx; x += 30u) {
                int yC = (side == 0u) ? (int)(Hpx - cand) : (int)cand;
                int yU = (side == 0u) ? (int)(Hpx - m)    : (int)m;
                float3 cc = DeskTex.Load(int3((int)x, yC, 0)).rgb * 255.0f;
                float3 cu = DeskTex.Load(int3((int)x, yU, 0)).rgb * 255.0f;
                if (cc.r > BLvl || cc.g > BLvl || cc.b > BLvl) found = 1.0f;
                if (cu.r > BLvl || cu.g > BLvl || cu.b > BLvl) allB = 0.0f;
            }
        } else {                                    // 竖边: 沿 y 扫描
            for (uint y = 0u; y < Hpx; y += 30u) {
                int xC = (side == 1u) ? (int)(Wpx - cand) : (int)cand;
                int xU = (side == 1u) ? (int)(Wpx - m)    : (int)m;
                float3 cc = DeskTex.Load(int3(xC, (int)y, 0)).rgb * 255.0f;
                float3 cu = DeskTex.Load(int3(xU, (int)y, 0)).rgb * 255.0f;
                if (cc.r > BLvl || cc.g > BLvl || cc.b > BLvl) found = 1.0f;
                if (cu.r > BLvl || cu.g > BLvl || cu.b > BLvl) allB = 0.0f;
            }
        }
        OutCols[i] = float4(found, allB, 0, 0);
        return;
    }

    float4 r = LedRects[i];
    float3 acc = 0;
    for (uint y = 0; y < TY; y++) {
        float v = r.y + (r.w - r.y) * ((float)y + 0.5f) / TY;
        for (uint x = 0; x < TX; x++) {
            float u = r.x + (r.z - r.x) * ((float)x + 0.5f) / TX;
            float3 c = DeskTex.Load(int3((int)u, (int)v, 0)).rgb;
            if (HDR) c = pow(saturate(c), 1.0f / 2.2f);   // FP16 线性 -> sRGB 近似
            acc += c;
        }
    }
    OutCols[i] = float4(acc / (TX * TY), 1);
}
)HLSL";

// ---------------------------------------------------------------- 显示器枚举
int EnumerateMonitors(std::vector<MonitorDesc>& out)
{
    out.clear();
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return 0;
    ComPtr<IDXGIAdapter1> adapter;
    for (UINT a = 0; factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; a++) {
        DXGI_ADAPTER_DESC1 ad{};
        if (FAILED(adapter->GetDesc1(&ad))) continue;
        ComPtr<IDXGIOutput> output;
        for (UINT oi = 0; adapter->EnumOutputs(oi, &output) != DXGI_ERROR_NOT_FOUND; oi++) {
            DXGI_OUTPUT_DESC od{};
            if (FAILED(output->GetDesc(&od)) || !od.AttachedToDesktop) continue;
            MONITORINFOEXW mi{};
            mi.cbSize = sizeof(mi);
            int w = 0, h = 0, primary = 0;
            if (GetMonitorInfoW(od.Monitor, &mi))
                primary = (mi.dwFlags & MONITORINFOF_PRIMARY) ? 1 : 0;
            // 分辨率必须取物理像素: rcMonitor/桌面坐标在非 DPI-aware 进程会被虚拟化
            // (本机 4K 曾偏差 1.5 倍), EnumDisplaySettingsW 不受进程 DPI 感知影响
            DEVMODEW dm{};
            dm.dmSize = sizeof(dm);
            if (EnumDisplaySettingsW(od.DeviceName, ENUM_CURRENT_SETTINGS, &dm)) {
                w = (int)dm.dmPelsWidth;
                h = (int)dm.dmPelsHeight;
            }
            out.push_back({ od.DeviceName, ad.Description, w, h, primary });
        }
        adapter.Reset();
    }
    return (int)out.size();
}

// ---------------------------------------------------------------- LED 布局
// 厂商顺序 (UpdateLeds → ScreenColors 调用序, direction 决定绕向):
//   dir=0: 右(B→T) → 顶(R→L) → 左(T→B) → 底(L→R)   #0 = 右下角
//   dir=1: 左(B→T) → 顶(L→R) → 右(T→B) → 底(R→L)   #0 = 左下角
// 灯数分配 (sides=2/4): 横边各 round(W/(W+H)*N/2), 竖边各 round(H/(W+H)*N/2)
namespace layout {

static void ComputeCounts(int leds, int sides, int W, int H, int* horiz, int* vert)
{
    if (sides == 4) {
        double wShare = (double)W / (W + H);
        *horiz = (int)std::lround(wShare * leds / 2.0);
        *vert  = (int)std::lround((1.0 - wShare) * leds / 2.0);
    } else if (sides == 3) {
        *horiz = (int)std::lround((double)leds * W / (W + 2.0 * H));
        *vert  = (leds - *horiz) / 2;
    } else {
        *vert = leds / 2; *horiz = 0;
    }
}

static void BuildRects(int leds, int sides, int direction, int depth, int rotate, int W, int H,
                       const int m[4], std::vector<Float4>& rects)
{
    rects.clear();
    int horiz, vert;
    ComputeCounts(leds, sides, W, H, &horiz, &vert);

    auto hseg = [&](int count, bool bottom, bool l2r) {  // 横边
        if (count <= 0) return;
        int mg = bottom ? m[0] : m[2];
        float y0 = bottom ? (float)(H - mg - depth) : (float)mg;
        float y1 = bottom ? (float)(H - mg)         : (float)(mg + depth);
        for (int i = 0; i < count; i++) {
            int k = l2r ? i : (count - 1 - i);
            float x0 = (float)W * k / count, x1 = (float)W * (k + 1) / count;
            rects.push_back({ x0, y0, x1, y1 });
        }
    };
    auto vseg = [&](int count, bool left, bool b2t) {    // 竖边
        if (count <= 0) return;
        int mg = left ? m[3] : m[1];
        float x0 = left ? (float)mg : (float)(W - mg - depth);
        float x1 = left ? (float)(mg + depth) : (float)(W - mg);
        for (int i = 0; i < count; i++) {
            int k = b2t ? (count - 1 - i) : i;   // b2t: LED#0 在底端 (y 随 i 递减)
            float y0 = (float)H * k / count, y1 = (float)H * (k + 1) / count;
            rects.push_back({ x0, y0, x1, y1 });
        }
    };

    if (direction == 0) {   // 右(B→T) → 顶(R→L) → 左(T→B) → 底(L→R)
        vseg(vert, false, true);
        hseg(horiz, false, false);
        vseg(vert, true,  false);
        hseg(horiz, true,  true);
    } else {                // 左(B→T) → 顶(L→R) → 右(T→B) → 底(R→L)
        vseg(vert, true,  true);
        hseg(horiz, false, true);
        vseg(vert, false, false);
        hseg(horiz, true,  false);
    }
    rects.resize(leds);

    // 灯序偏移 (M4): LED i 的取色区 = 段 (i+rotate) mod N; 校准窗用于微调灯带起点错位。
    // 取模对负 rotate 同样成立; rotate=0 时零开销。
    if (rotate != 0 && leds > 0) {
        std::vector<Float4> tmp = rects;
        for (int i = 0; i < leds; i++) {
            int j = (i + rotate) % leds;
            if (j < 0) j += leds;
            rects[i] = tmp[j];
        }
    }
}

} // namespace layout

static int EffectiveDepth(const Params& p, int H)
{
    int D = p.depthOverride;
    if (D <= 0) D = H / 100 * p.range / 2;            // 厂商深度公式 (整数语义)
    return std::max(2, D);
}

// ---------------------------------------------------------------- 构建
bool CaptureSession::PickOutput(const Params& p, int* fallback)
{
    *fallback = 0;
    ComPtr<IDXGIFactory1> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return false;

    // 先统计可用输出, 判断 monitorIndex 是否需要回退
    std::vector<std::pair<ComPtr<IDXGIAdapter1>, ComPtr<IDXGIOutput>>> outs;
    ComPtr<IDXGIAdapter1> adapter;
    for (UINT a = 0; factory->EnumAdapters1(a, &adapter) != DXGI_ERROR_NOT_FOUND; a++) {
        ComPtr<IDXGIOutput> output;
        for (UINT oi = 0; adapter->EnumOutputs(oi, &output) != DXGI_ERROR_NOT_FOUND; oi++) {
            DXGI_OUTPUT_DESC od{};
            if (FAILED(output->GetDesc(&od)) || !od.AttachedToDesktop) continue;
            outs.push_back({ adapter, output });
        }
        adapter.Reset();
    }
    if (outs.empty()) return false;

    int idx = p.monitorIndex;
    if (idx < 0 || idx >= (int)outs.size()) {
        // 配置显示器不可用 → 回退主屏 (GetMonitorInfo 的 PRIMARY 标记)
        int pick = 0;
        for (int i = 0; i < (int)outs.size(); i++) {
            DXGI_OUTPUT_DESC od{};
            if (SUCCEEDED(outs[i].second->GetDesc(&od))) {
                MONITORINFOEXW mi{};
                mi.cbSize = sizeof(mi);
                if (GetMonitorInfoW(od.Monitor, &mi) && (mi.dwFlags & MONITORINFOF_PRIMARY)) {
                    pick = i; break;
                }
            }
        }
        idx = pick;
        *fallback = 1;
    }
    adapter_ = outs[idx].first;
    output_  = outs[idx].second;
    return true;
}

HRESULT CaptureSession::CreateDuplication()
{
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fl{};
    HRESULT hr = D3D11CreateDevice(adapter_.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags,
                                   nullptr, 0, D3D11_SDK_VERSION, &device_, &fl, &ctx_);
    if (FAILED(hr)) return hr;
    ComPtr<IDXGIOutput1> out1;
    if (FAILED(output_.As(&out1))) return E_FAIL;
    hr = out1->DuplicateOutput(device_.Get(), &dup_);
    if (FAILED(hr)) return hr;
    DXGI_OUTDUPL_DESC ddesc{};
    dup_->GetDesc(&ddesc);
    hdr_ = ddesc.ModeDesc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
    W_ = (int)ddesc.ModeDesc.Width;
    H_ = (int)ddesc.ModeDesc.Height;
    return S_OK;
}

bool CaptureSession::CompileShader()
{
    static ComPtr<ID3DBlob> cached;
    static bool tried = false;
    if (!tried) {
        tried = true;
        ComPtr<ID3DBlob> err;
        if (FAILED(D3DCompile(g_hlsl, strlen(g_hlsl), "ambient.hlsl", nullptr, nullptr,
                              "main", "cs_5_0", 0, 0, &cached, &err)))
            cached.Reset();
    }
    if (!cached) return false;
    return SUCCEEDED(device_->CreateComputeShader(cached->GetBufferPointer(),
                                                  cached->GetBufferSize(), nullptr, &cs_));
}

void CaptureSession::ComputeTaps(const Params& p)
{
    // --smoothing: 按厂商采样密度 (每 smoothing px 一个采样点) 换算 taps, 覆盖 taps
    if (p.smoothing > 0 && H_ > 0) {
        int Dp = EffectiveDepth(p, H_);
        int avgSeg = 2 * (W_ + H_) / std::max(1, p_.leds);
        tapsY_ = std::min(32, std::max(2, Dp / p.smoothing));
        tapsX_ = std::min(32, std::max(2, avgSeg / p.smoothing));
    } else {
        tapsX_ = tapsY_ = std::clamp(p.taps, 1, 32);
    }
}

bool CaptureSession::RecreateResources()
{
    rectBuf_.Reset(); rectSRV_.Reset();
    outBuf_.Reset(); stageBuf_.Reset(); outUAV_.Reset(); cbParams_.Reset();

    const UINT N = p_.leds;
    layout::BuildRects(N, p_.sides, p_.direction, EffectiveDepth(p_, H_), p_.ledRotate, W_, H_, margins_, rects_);

    HRESULT hr;
    {
        D3D11_BUFFER_DESC bd{ sizeof(Float4) * (UINT)rects_.size(), D3D11_USAGE_DEFAULT,
                              D3D11_BIND_SHADER_RESOURCE, 0,
                              D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, sizeof(Float4) };
        hr = device_->CreateBuffer(&bd, nullptr, &rectBuf_);
        if (FAILED(hr)) return false;
        D3D11_SHADER_RESOURCE_VIEW_DESC sd{};
        sd.Format = DXGI_FORMAT_UNKNOWN;
        sd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
        sd.Buffer.FirstElement = 0;
        sd.Buffer.NumElements = (UINT)rects_.size();
        hr = device_->CreateShaderResourceView(rectBuf_.Get(), &sd, &rectSRV_);
        if (FAILED(hr)) return false;
    }
    const UINT NPROBE = N + 4;
    {
        D3D11_BUFFER_DESC bd{ sizeof(Float4) * NPROBE, D3D11_USAGE_DEFAULT,
                              D3D11_BIND_UNORDERED_ACCESS, 0,
                              D3D11_RESOURCE_MISC_BUFFER_STRUCTURED, sizeof(Float4) };
        hr = device_->CreateBuffer(&bd, nullptr, &outBuf_);
        if (FAILED(hr)) return false;
        D3D11_BUFFER_DESC sd2 = bd;
        sd2.Usage = D3D11_USAGE_STAGING;
        sd2.BindFlags = 0;
        sd2.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        sd2.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
        hr = device_->CreateBuffer(&sd2, nullptr, &stageBuf_);
        if (FAILED(hr)) return false;
        D3D11_UNORDERED_ACCESS_VIEW_DESC ud{};
        ud.Format = DXGI_FORMAT_UNKNOWN;
        ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
        ud.Buffer.NumElements = NPROBE;
        hr = device_->CreateUnorderedAccessView(outBuf_.Get(), &ud, &outUAV_);
        if (FAILED(hr)) return false;
    }
    {
        D3D11_BUFFER_DESC bd{ 64, D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0 };
        hr = device_->CreateBuffer(&bd, nullptr, &cbParams_);
        if (FAILED(hr)) return false;
    }
    PushRects();
    return true;
}

void CaptureSession::PushRects()
{
    ctx_->UpdateSubresource(rectBuf_.Get(), 0, nullptr, rects_.data(), 0, 0);
    UINT cb[16] = {};
    cb[0] = p_.leds; cb[1] = (UINT)tapsX_; cb[2] = (UINT)tapsY_; cb[3] = hdr_ ? 1u : 0u;
    cb[4] = 0; cb[5] = (UINT)p_.barlevel; cb[6] = (UINT)W_; cb[7] = (UINT)H_;
    cb[8] = (UINT)margins_[0]; cb[9] = (UINT)margins_[1];
    cb[10] = (UINT)margins_[2]; cb[11] = (UINT)margins_[3];
    ctx_->UpdateSubresource(cbParams_.Get(), 0, nullptr, cb, 0, 0);
}

CaptureSession::BuildResult CaptureSession::Build(const Params& p, int* fallback)
{
    Destroy();
    p_ = p;
    p_.leds = std::clamp(p_.leds, 1, serial::kMaxLedCount);
    ACLOGD("capture build: enter (monitor %d leds %d)", p_.monitorIndex, p_.leds);
    if (!PickOutput(p_, fallback)) return BUILD_TRANSIENT;   // 暂无可用输出
    HRESULT hr = CreateDuplication();
    if (FAILED(hr)) {
        Destroy();
        // DuplicateOutput 的 ACCESS_DENIED/NOT_CURRENTLY_AVAILABLE 等为瞬时;
        // 其余 (设备创建失败等) 属硬错误
        if (hr == HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED) ||
            hr == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE ||
            hr == DXGI_ERROR_UNSUPPORTED)
            return BUILD_TRANSIENT;
        return BUILD_HARD;
    }
    if (!CompileShader() || !RecreateResources()) {
        Destroy();
        return BUILD_HARD;
    }
    return BUILD_OK;
}

void CaptureSession::Destroy()
{
    if (ctx_) ctx_->ClearState();
    dup_.Reset();
    rectSRV_.Reset();
    rectBuf_.Reset();
    outUAV_.Reset();
    outBuf_.Reset();
    stageBuf_.Reset();
    cbParams_.Reset();
    cs_.Reset();
    ctx_.Reset();
    device_.Reset();
    output_.Reset();
    adapter_.Reset();
    W_ = H_ = 0;
    allBlack_ = false;
    hadUpdate_ = false;
}

void CaptureSession::UpdateLayout(const Params& p)
{
    bool depthOrGeometry = p.leds != p_.leds || p.sides != p_.sides ||
                           p.direction != p_.direction || p.range != p_.range ||
                           p.depthOverride != p_.depthOverride || p.smoothing != p_.smoothing ||
                           p.ledRotate != p_.ledRotate;
    bool cbOnly = !depthOrGeometry &&
                  (p.taps != p_.taps || p.barlevel != p_.barlevel || p.blackbars != p_.blackbars);
    Params old = p_;
    p_ = p;
    p_.leds = std::clamp(p_.leds, 1, serial::kMaxLedCount);
    if (depthOrGeometry) {
        ComputeTaps(p_);
        RecreateResources();
    } else if (cbOnly) {
        ComputeTaps(p_);
        PushRects();
    }
    (void)old;
}

CaptureSession::StepResult CaptureSession::Step(uint32_t timeoutMs, Float4* cols)
{
    if (!dup_) return STEP_ERROR;
    DXGI_OUTDUPL_FRAME_INFO info{};
    ComPtr<IDXGIResource> res;
    HRESULT hr = dup_->AcquireNextFrame(timeoutMs, &info, &res);
    if (hr == DXGI_ERROR_WAIT_TIMEOUT) { hadUpdate_ = false; return STEP_TIMEOUT; }
    if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_INVALID_CALL) return STEP_ACCESS_LOST;
    if (hr == HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED)) return STEP_ACCESS_DENIED;
    if (FAILED(hr)) return STEP_ERROR;

    {
        ComPtr<ID3D11Texture2D> tex;
        res.As(&tex);
        ComPtr<ID3D11ShaderResourceView> deskSRV;
        // duplication 纹理通常自带 SRV bind; 失败则 CopyResource 到自有纹理
        if (FAILED(device_->CreateShaderResourceView(tex.Get(), nullptr, &deskSRV))) {
            D3D11_TEXTURE2D_DESC td;
            tex->GetDesc(&td);
            td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            td.MiscFlags = 0;
            ComPtr<ID3D11Texture2D> copyTex;
            device_->CreateTexture2D(&td, nullptr, &copyTex);
            ctx_->CopyResource(copyTex.Get(), tex.Get());
            device_->CreateShaderResourceView(copyTex.Get(), nullptr, &deskSRV);
        }
        ID3D11ShaderResourceView* srvs[2] = { deskSRV.Get(), rectSRV_.Get() };
        ctx_->CSSetShader(cs_.Get(), nullptr, 0);
        ctx_->CSSetShaderResources(0, 2, srvs);
        ID3D11UnorderedAccessView* uavs[1] = { outUAV_.Get() };
        ctx_->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        ID3D11Buffer* cbs[1] = { cbParams_.Get() };
        ctx_->CSSetConstantBuffers(0, 1, cbs);
        ctx_->Dispatch((p_.leds + 4 + 63) / 64, 1, 1);
        ctx_->CopyResource(stageBuf_.Get(), outBuf_.Get());
    }
    {
        D3D11_MAPPED_SUBRESOURCE m{};
        if (FAILED(ctx_->Map(stageBuf_.Get(), 0, D3D11_MAP_READ, 0, &m))) {
            dup_->ReleaseFrame();
            return STEP_ERROR;
        }
        memcpy(cols, m.pData, sizeof(Float4) * (p_.leds + 4));
        ctx_->Unmap(stageBuf_.Get(), 0);
    }
    dup_->ReleaseFrame();
    hadUpdate_ = info.LastPresentTime.QuadPart != 0 || info.AccumulatedFrames > 0;

    // 全黑判定 (链前原始采样, 独占全屏/MPO 提示用)
    float mx = 0;
    for (UINT i = 0; i < (UINT)p_.leds; i++)
        mx = std::max(mx, std::max(cols[i].x, std::max(cols[i].y, cols[i].z)));
    allBlack_ = mx * 255.0f < 8.0f;

    // ---- 黑边自适应 (厂商语义: 每帧最多 ±15px 渐进逼近内容边界)
    if (p_.blackbars) {
        bool changed = false;
        int dimMax[4] = { H_ / 2, W_ / 2, H_ / 2, W_ / 2 };
        for (int s = 0; s < 4; s++) {
            bool found    = cols[p_.leds + s].x > 0.5f;   // 候选边界行有内容 → 外扩
            bool allBlack = cols[p_.leds + s].y > 0.5f;   // 当前边界行全黑   → 内收
            if (found && margins_[s] - 15 >= 5) { margins_[s] -= 15; changed = true; }
            else if (allBlack && margins_[s] + 15 < dimMax[s]) { margins_[s] += 15; changed = true; }
        }
        if (changed) {
            layout::BuildRects(p_.leds, p_.sides, p_.direction, EffectiveDepth(p_, H_), p_.ledRotate,
                               W_, H_, margins_, rects_);
            PushRects();
        }
    }
    return STEP_FRAME;
}

// ---------------------------------------------------------------- 四象限测试帧
void CaptureSession::BuildPatternFrame(const Params& p, std::vector<uint8_t>& frame543)
{
    // 厂商校准图风格: 虚拟 16:9 屏幕四象限各一色, 每颗灯按其取色区中心所在象限取色
    const uint8_t palette[4][3] = { {255,0,0}, {0,255,0}, {0,0,255}, {255,255,0} };
    int mzero[4] = { 0, 0, 0, 0 };
    std::vector<Float4> rects;
    layout::BuildRects(p.leds, p.sides, p.direction, 30, p.ledRotate, 160, 90, mzero, rects);
    for (int i = 0; i < (int)p.leds; i++) {
        double cx = (rects[i].x + rects[i].z) / 2.0, cy = (rects[i].y + rects[i].w) / 2.0;
        int quad = (cy < 45.0 ? 0 : 2) + (cx < 80.0 ? 0 : 1);   // 0=TL 1=TR 2=BL 3=BR
        frame543[3 + i * 3 + 0] = palette[quad][0];
        frame543[3 + i * 3 + 1] = palette[quad][1];
        frame543[3 + i * 3 + 2] = palette[quad][2];
    }
}

} // namespace capture

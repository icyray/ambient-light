// capture.h — DXGI Desktop Duplication + GPU 条带采样 (PoC 管线移植) + 显示器枚举
#pragma once
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <cstdint>
#include <string>
#include <vector>

namespace capture {

// 与 HLSL float4 对齐 (16B)
struct Float4 { float x, y, z, w; };

struct MonitorDesc {
    std::wstring name;      // \\.\DISPLAY1
    std::wstring adapter;   // NVIDIA GeForce ...
    int w = 0, h = 0;
    int primary = 0;
};

// 仅 AttachedToDesktop 输出, 顺序即 monitorIndex (与 AC_GetMonitors 一致)
int EnumerateMonitors(std::vector<MonitorDesc>& out);

struct Params {
    int  monitorIndex  = 0;
    int  leds          = 60;    // 有效值钳到固件上限 180
    int  sides         = 4;     // 2=左右 3=上+左右 4=四边
    int  direction     = 0;     // 0=逆时针右下起
    int  range         = 15;    // 深度系数: depth=H/100*range/2
    int  depthOverride = 0;     // >0 直接指定深度 px
    int  taps          = 8;     // smoothing>0 时被换算覆盖
    int  smoothing     = 0;
    int  ledRotate     = 0;     // 灯序偏移: LED i 用段 (i+rotate) mod N 的取色区 (已钳制)
    bool blackbars     = true;
    int  barlevel      = 3;
};

class CaptureSession {
public:
    CaptureSession() = default;
    ~CaptureSession() { Destroy(); }
    CaptureSession(const CaptureSession&) = delete;
    CaptureSession& operator=(const CaptureSession&) = delete;

    enum StepResult {
        STEP_FRAME = 0,      // 新帧已采样, cols 有效
        STEP_TIMEOUT,        // 无桌面更新
        STEP_ACCESS_LOST,    // 拓扑/分辨率变化 → 需全量重建
        STEP_ACCESS_DENIED,  // 锁屏/安全桌面 → 串口保持, 定时重试
        STEP_ERROR,          // 其他失败 (设备移除等)
    };

    enum BuildResult {
        BUILD_OK = 0,        // 构建成功
        BUILD_TRANSIENT,     // 暂不可用 (无输出/锁屏/占用): 500ms 重试 → WAIT_DISP
        BUILD_HARD,          // D3D/资源失败: 5s 慢速重试 → ERROR
    };

    // 从适配器枚举开始的全量构建; *fallback=1 表示配置显示器不可用已回退主屏
    BuildResult Build(const Params& p, int* fallback);
    void    Destroy();
    bool    Valid() const { return dup_ != nullptr; }

    // 布局热更新 (灯数/边数/方向/深度/采样/黑边): 重建 GPU 缓冲, 保留 Duplication 会话
    void UpdateLayout(const Params& p);

    // 采样一帧; cols 容量 >= (leds+4) 个 Float4
    StepResult Step(uint32_t timeoutMs, Float4* cols);
    void GetMargins(int m[4]) const { for (int i = 0; i < 4; i++) m[i] = margins_[i]; }
    bool LastFrameAllBlack() const { return allBlack_; }
    bool LastFrameHadUpdate() const { return hadUpdate_; }
    int  Width() const { return W_; }
    int  Height() const { return H_; }

    // 测试钩子: 厂商校准图风格四象限色块帧 (原始字节, 不过颜色链; 虚拟 16:9 映射)
    static void BuildPatternFrame(const Params& p, std::vector<uint8_t>& frame543);

private:
    bool PickOutput(const Params& p, int* fallback);
    HRESULT CreateDuplication();
    bool CompileShader();
    bool RecreateResources();     // 依赖 leds/布局/等级的全部 GPU 资源
    void ComputeTaps(const Params& p);
    void PushRects();             // rects → rectBuf, margins → cb

    Microsoft::WRL::ComPtr<IDXGIAdapter1>  adapter_;
    Microsoft::WRL::ComPtr<IDXGIOutput>    output_;
    Microsoft::WRL::ComPtr<ID3D11Device>   device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> ctx_;
    Microsoft::WRL::ComPtr<IDXGIOutputDuplication> dup_;

    Microsoft::WRL::ComPtr<ID3D11Buffer> rectBuf_, outBuf_, stageBuf_, cbParams_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView>      rectSRV_;
    Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView>     outUAV_;
    Microsoft::WRL::ComPtr<ID3D11ComputeShader> cs_;

    Params p_{};                  // 生效参数
    int W_ = 0, H_ = 0;
    bool hdr_ = false;
    int  tapsX_ = 8, tapsY_ = 8;
    int  margins_[4] = { 5, 5, 5, 5 };   // {底,右,顶,左}, 厂商初始 5
    std::vector<Float4> rects_;
    bool allBlack_ = false;
    bool hadUpdate_ = false;
};

} // namespace capture

// serial.h — USB-CDC 串口 + 厂商定长帧协议 (握手/复位/枚举), 已在 PoC 验证
#pragma once
#include <windows.h>
#include <stdint.h>
#include <mutex>
#include <string>
#include <vector>

namespace serial {

constexpr int kMaxLedCount = 180;
constexpr int kFrameLen    = 3 + kMaxLedCount * 3;   // 543, 固件按定长帧定界

// 厂商灯带 USB 身份 (端口身份按 VID/PID, 睡眠后 COM 号变化可自动跟随)
constexpr const wchar_t* kVid = L"VID_2E3C";
constexpr const wchar_t* kPid = L"PID_5740";

struct ComPortInfo {
    std::wstring port;   // L"COM5"
    std::wstring hwid;   // 设备实例 ID
};

class SerialPort {
public:
    SerialPort() = default;
    ~SerialPort() { Close(); }
    SerialPort(const SerialPort&) = delete;
    SerialPort& operator=(const SerialPort&) = delete;

    bool Open(const std::wstring& name, uint32_t baud);
    void Close();
    bool IsOpen() const { return h_ != INVALID_HANDLE_VALUE; }
    HANDLE handle() const { return h_; }

    bool Write(const uint8_t* p, uint32_t n);
    // 尽力写: 返回**本次实际写进去的字节数** (可能 < n)。0 = 没有进展。
    //   hardOut: true = 硬错误 (端口没了/被拔出, 拔线时 WriteFile 是**立刻**返回的, 不会等超时);
    //            false = 写超时 (ERROR_SEM_TIMEOUT) —— **设备一时没收数据, 端口还活着**。
    //   lastErrOut: 便于日志诊断的真实 GetLastError()。
    // 为什么需要它: 固件**按定长 kFrameLen 字节给帧定界** ⇒ 半帧被丢掉 = 它把下一帧的帧头当成
    //   这帧的尾巴, 之后**永久错位** (只有握手/重连能复位) ✗。所以上层必须"补写剩余部分",
    //   而不是把一次写超时当成掉线 (2026-10-09 的"切出 MCU 特效就重连"就是这么来的)。
    uint32_t WriteSome(const uint8_t* p, uint32_t n, bool* hardOut = nullptr,
                       DWORD* lastErrOut = nullptr);

    // 把 n 字节**写完**才算成功 (写超时就补写剩余部分)。
    //   budgetMs: "允许多久写不完" —— 超了返回 false (固件按定长 543 给帧定界, 半帧丢弃会让它
    //             永久错位, 所以调用方宁可重连也不能丢下半帧 ✗)。
    //   stuckOut: false = 硬错误(端口没了) / true = 预算内没写完(设备不吃数据)。
    //   abortEvent: 可选, 每轮补写前查一次 (与 Handshake 同一套约定) → 置位就立刻放弃。
    // 拔线时 WriteFile 是**立刻**报错的 (不会等到超时), 所以真掉线的检测速度不受本函数影响 ✓
    bool WriteAll(const uint8_t* p, uint32_t n, uint32_t budgetMs,
                  bool* stuckOut = nullptr, HANDLE abortEvent = nullptr);
    // 带超时的单字节读; 返回 0-255, -1 = 无数据/失败
    int  ReadByte(uint32_t timeoutMs);
    // 非阻塞读取固件周期状态字节 (cbInQue 驱动)
    bool PollStatusByte(uint8_t& out);

private:
    HANDLE h_ = INVALID_HANDLE_VALUE;
};

// 枚举当前在位、实例 ID 含 VID_x&PID_y 的 COM 口 (只枚举, 不打开端口)
int EnumerateVidPidPorts(std::vector<ComPortInfo>& out);

// 枚举本机**所有** COM 口 (不受 VID/PID 限制): 读 HKLM\HARDWARE\DEVICEMAP\SERIALCOMM。
// 用途: 用户**手动指定端口**时判断该端口到底在不在位 —— 不能拿上面那个按 VID/PID 过滤过的
// 列表来判, 否则任何一个"非灯带"的 COM 口都会被当成"端口消失", 于是触发身份跟随、
// 偷偷连回灯带 (实测现象: 端口填 COM4, 主页开启却连到 COM5)。
int EnumerateAllPorts(std::vector<std::wstring>& out);

// 在已打开端口上执行: 全零复位帧 → "Ad0" 握手 ×3 → 应答字节
// abortEvent (可选): 每次尝试前检查一次, 已置位就立刻放弃 —— 用于"停止时尽快跳出"。
//   没有它的话, 一个**不是灯带**的端口每次握手要 ~1.1s (实测 COM4 1122ms),
//   而会话线程卡在这里时 AC_StopEx 的 join 会把调用方(UI 线程)一起拖住。
bool Handshake(SerialPort& sp, uint8_t& resp, HANDLE abortEvent = nullptr);

// 独占探测: 打开→复位→握手→关闭; 返回 0=成功(resp 有效) -1=打开失败(占用/不存在) -2=握手失败
int ProbeDevice(const std::wstring& port, uint32_t baud, uint8_t& resp);

// 全局"打开端口并握手"互斥 (进程内单例)。**所有打开端口的操作都必须持有它**:
//   ① 会话的 TryConnect (AmbientCore.cpp)  ② UI 设备枚举走的 ProbeDevice。
// 两者会抢同一个端口: 枚举先打开 → 会话 `Open` 拿到"忙" → 全部候选失败 →
// 报 "device lost: no usable port" 并进 DEV_LOST (约 0.5s 后才连上)。
// 这就是"程序启动时偶现警告"的根因 —— 启动瞬间 UI 正好在枚举设备、会和自己的会话抢端口。
// 加锁后只是**排队**(枚举占几十~几百 ms), 不会失败。
std::mutex& ProbeLock();

// 543B 全零 "Ada" 帧 (关灯帧 / 解析器复位帧)
void BuildZeroFrame(std::vector<uint8_t>& f);

} // namespace serial

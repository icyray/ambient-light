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
bool Handshake(SerialPort& sp, uint8_t& resp);

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

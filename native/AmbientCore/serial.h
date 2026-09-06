// serial.h — USB-CDC 串口 + 厂商定长帧协议 (握手/复位/枚举), 已在 PoC 验证
#pragma once
#include <windows.h>
#include <stdint.h>
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

// 在已打开端口上执行: 全零复位帧 → "Ad0" 握手 ×3 → 应答字节
bool Handshake(SerialPort& sp, uint8_t& resp);

// 独占探测: 打开→复位→握手→关闭; 返回 0=成功(resp 有效) -1=打开失败(占用/不存在) -2=握手失败
int ProbeDevice(const std::wstring& port, uint32_t baud, uint8_t& resp);

// 543B 全零 "Ada" 帧 (关灯帧 / 解析器复位帧)
void BuildZeroFrame(std::vector<uint8_t>& f);

} // namespace serial

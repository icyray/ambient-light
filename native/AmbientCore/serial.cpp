#include "serial.h"
#include <initguid.h>
#include <devguid.h>
#include <setupapi.h>
#include <cfgmgr32.h>
#include <cstring>
#include <cwchar>

#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "cfgmgr32.lib")
#pragma comment(lib, "advapi32.lib")

namespace serial {

static void ConfigurePort(HANDLE h, uint32_t baud)
{
    DCB dcb{};
    dcb.DCBlength = sizeof(dcb);
    GetCommState(h, &dcb);
    dcb.BaudRate = baud;
    dcb.ByteSize = 8;
    dcb.Parity   = NOPARITY;
    dcb.StopBits = ONESTOPBIT;
    dcb.fDtrControl = DTR_CONTROL_ENABLE;
    SetCommState(h, &dcb);
    COMMTIMEOUTS to{};
    to.ReadIntervalTimeout      = 50;
    to.ReadTotalTimeoutConstant = 50;
    to.WriteTotalTimeoutConstant = 100;
    SetCommTimeouts(h, &to);
    EscapeCommFunction(h, SETDTR);
}

bool SerialPort::Open(const std::wstring& name, uint32_t baud)
{
    Close();
    wchar_t path[64];
    _snwprintf_s(path, _TRUNCATE, L"\\\\.\\%s", name.c_str());
    h_ = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                     OPEN_EXISTING, 0, nullptr);
    if (h_ == INVALID_HANDLE_VALUE) return false;
    ConfigurePort(h_, baud);
    return true;
}

void SerialPort::Close()
{
    if (h_ != INVALID_HANDLE_VALUE) {
        CloseHandle(h_);
        h_ = INVALID_HANDLE_VALUE;
    }
}

bool SerialPort::Write(const uint8_t* p, uint32_t n)
{
    if (h_ == INVALID_HANDLE_VALUE) return false;
    DWORD done = 0;
    if (!WriteFile(h_, p, n, &done, nullptr) || done != n) return false;
    return true;
}

uint32_t SerialPort::WriteSome(const uint8_t* p, uint32_t n, bool* hardOut, DWORD* lastErrOut)
{
    if (hardOut) *hardOut = false;
    if (lastErrOut) *lastErrOut = 0;
    if (h_ == INVALID_HANDLE_VALUE) { if (hardOut) *hardOut = true; return 0; }
    DWORD done = 0;
    BOOL ok = WriteFile(h_, p, n, &done, nullptr);
    if (!ok) {
        DWORD err = GetLastError();
        if (lastErrOut) *lastErrOut = err;
        // 121 = ERROR_SEM_TIMEOUT: 写超时 —— **设备一时没吃数据, 端口没坏** → 交给上层补写 ✓
        if (err == ERROR_SEM_TIMEOUT) return 0;
        if (hardOut) *hardOut = true;      // 其余错误 (端口消失 / 句柄失效) = 真掉线
        return 0;
    }
    return (uint32_t)done;                 // done < n = 写超时 (同上: 软)
}

bool SerialPort::WriteAll(const uint8_t* p, uint32_t n, uint32_t budgetMs,
                          bool* stuckOut, HANDLE abortEvent)
{
    if (stuckOut) *stuckOut = false;
    uint32_t off = 0;
    ULONGLONG t0 = GetTickCount64();
    while (off < n) {
        bool hard = false;
        uint32_t w = WriteSome(p + off, n - off, &hard);
        if (hard) return false;                              // 端口没了 → 立刻判掉线
        if (w > 0) { off += w; continue; }
        if ((uint32_t)(GetTickCount64() - t0) >= budgetMs) {  // 预算用尽: 设备长时间不吃数据
            if (stuckOut) *stuckOut = true;
            return false;
        }
        if (abortEvent && WaitForSingleObject(abortEvent, 1) == WAIT_OBJECT_0) return false;
        if (!abortEvent) Sleep(1);
    }
    return true;
}

int SerialPort::ReadByte(uint32_t timeoutMs)
{
    if (h_ == INVALID_HANDLE_VALUE) return -1;
    COMMTIMEOUTS to{};
    GetCommTimeouts(h_, &to);
    to.ReadIntervalTimeout      = timeoutMs;
    to.ReadTotalTimeoutMultiplier = 0;
    to.ReadTotalTimeoutConstant = timeoutMs;
    SetCommTimeouts(h_, &to);
    uint8_t b = 0;
    DWORD n = 0;
    if (!ReadFile(h_, &b, 1, &n, nullptr) || n == 0) return -1;
    return b;
}

bool SerialPort::PollStatusByte(uint8_t& out)
{
    if (h_ == INVALID_HANDLE_VALUE) return false;
    DWORD errs = 0;
    COMSTAT cs{};
    if (!ClearCommError(h_, &errs, &cs) || cs.cbInQue == 0) return false;
    DWORD n = 0;
    if (!ReadFile(h_, &out, 1, &n, nullptr) || n == 0) return false;
    return true;
}

int EnumerateVidPidPorts(std::vector<ComPortInfo>& out)
{
    out.clear();
    // 类枚举 (GUID_DEVCLASS_PORTS + DIGCF_PRESENT): COM 口是端口类设备节点,
    // 不能把类 GUID 当接口 GUID 走 DIGCF_DEVICEINTERFACE (那样枚举不到任何设备)
    HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_PORTS, nullptr, nullptr, DIGCF_PRESENT);
    if (set == INVALID_HANDLE_VALUE) return (int)out.size();

    SP_DEVINFO_DATA dev{};
    dev.cbSize = sizeof(dev);
    for (int i = 0; SetupDiEnumDeviceInfo(set, i, &dev); i++) {
        wchar_t id[MAX_DEVICE_ID_LEN]{};
        if (CM_Get_Device_IDW(dev.DevInst, id, MAX_DEVICE_ID_LEN, 0) != CR_SUCCESS) continue;
        // 实例 ID 形如 USB\VID_2E3C&PID_5740&MI_00\...; 大小写不敏感
        wchar_t up[MAX_DEVICE_ID_LEN]{};
        for (int k = 0; id[k] && k < MAX_DEVICE_ID_LEN; k++) up[k] = towupper(id[k]);
        if (!wcsstr(up, kVid) || !wcsstr(up, kPid)) continue;

        // PortName 位于设备实例的 Device Parameters 键 (DIREG_DEV); usbser 设备
        // 的 driver key 里没有该值 (实测 COM5 在 DIREG_DEV)
        HKEY key = SetupDiOpenDevRegKey(set, &dev, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ);
        if (key == INVALID_HANDLE_VALUE) continue;
        wchar_t port[16]{};
        DWORD sz = sizeof(port), type = 0;
        if (RegQueryValueExW(key, L"PortName", nullptr, &type,
                             (LPBYTE)port, &sz) == ERROR_SUCCESS && type == REG_SZ) {
            out.push_back({ port, id });
        }
        RegCloseKey(key);
    }
    SetupDiDestroyDeviceInfoList(set);
    return (int)out.size();
}

// 本机所有 COM 口 —— 注册表 HARDWARE\DEVICEMAP\SERIALCOMM (与托管侧 SerialPorts.All() 同一来源,
// 比按 VID/PID 过滤的枚举宽)。只读注册表, 不枚举设备、更不打开端口。
int EnumerateAllPorts(std::vector<std::wstring>& out)
{
    out.clear();
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"HARDWARE\\DEVICEMAP\\SERIALCOMM",
                      0, KEY_READ, &key) != ERROR_SUCCESS)
        return 0;
    for (DWORD i = 0; ; i++) {
        wchar_t name[256]{};
        wchar_t data[256]{};
        DWORD nameLen = (DWORD)(sizeof(name) / sizeof(wchar_t));
        DWORD dataLen = sizeof(data);
        DWORD type = 0;
        if (RegEnumValueW(key, i, name, &nameLen, nullptr, &type,
                          (LPBYTE)data, &dataLen) != ERROR_SUCCESS)
            break;
        if (type == REG_SZ && dataLen >= sizeof(wchar_t) && data[0])
            out.push_back(data);                       // 形如 L"COM5"
    }
    RegCloseKey(key);
    return (int)out.size();
}

void BuildZeroFrame(std::vector<uint8_t>& f){
    f.assign((size_t)kFrameLen, 0);
    f[0] = 'A'; f[1] = 'd'; f[2] = 'a';
}

bool Handshake(SerialPort& sp, uint8_t& resp, HANDLE abortEvent)
{
    std::vector<uint8_t> zeros;
    BuildZeroFrame(zeros);
    sp.Write(zeros.data(), (DWORD)zeros.size());   // 复位固件帧解析器
    Sleep(20);
    for (int attempt = 0; attempt < 3; attempt++) {
        // 停止请求已到 → 别再把时间花在这个端口上 (调用方在 join 我们)
        if (abortEvent && WaitForSingleObject(abortEvent, 0) == WAIT_OBJECT_0) return false;
        const uint8_t hs[3] = { 'A', 'd', '0' };
        sp.Write(hs, 3);
        Sleep(30);
        int r = sp.ReadByte(60);
        if (r >= 0 && (r & 0x80)) {
            resp = (uint8_t)r;
            return true;
        }
    }
    return false;
}

int ProbeDevice(const std::wstring& port, uint32_t baud, uint8_t& resp)
{
    // 与会话的 TryConnect 串行化: 否则启动瞬间 UI 枚举 + 会话握手会互相抢成"忙" (见 serial.h)
    std::lock_guard lk(ProbeLock());
    SerialPort sp;
    if (!sp.Open(port, baud)) return -1;      // 占用或不存在
    if (!Handshake(sp, resp)) return -2;
    return 0;
}

std::mutex& ProbeLock()
{
    static std::mutex m;                      // 进程内单例
    return m;
}

} // namespace serial

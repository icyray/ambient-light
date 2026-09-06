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

void BuildZeroFrame(std::vector<uint8_t>& f)
{
    f.assign((size_t)kFrameLen, 0);
    f[0] = 'A'; f[1] = 'd'; f[2] = 'a';
}

bool Handshake(SerialPort& sp, uint8_t& resp)
{
    std::vector<uint8_t> zeros;
    BuildZeroFrame(zeros);
    sp.Write(zeros.data(), (DWORD)zeros.size());   // 复位固件帧解析器
    Sleep(20);
    for (int attempt = 0; attempt < 3; attempt++) {
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
    SerialPort sp;
    if (!sp.Open(port, baud)) return -1;      // 占用或不存在
    if (!Handshake(sp, resp)) return -2;
    return 0;
}

} // namespace serial

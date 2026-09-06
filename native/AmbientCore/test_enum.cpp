// test_enum.cpp — 诊断 serial::EnumerateVidPidPorts 各环节
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <devguid.h>
#include <setupapi.h>
#include <cfgmgr32.h>
#include <cstdio>
#include <cwchar>

#pragma comment(lib, "setupapi.lib")
#pragma comment(lib, "cfgmgr32.lib")
#pragma comment(lib, "advapi32.lib")

int wmain()
{
    HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_PORTS, nullptr, nullptr, DIGCF_PRESENT);
    if (set == INVALID_HANDLE_VALUE) { wprintf(L"SetupDiGetClassDevs failed %u\n", GetLastError()); return 1; }
    SP_DEVINFO_DATA dev{};
    dev.cbSize = sizeof(dev);
    int n = 0;
    for (int i = 0; SetupDiEnumDeviceInfo(set, i, &dev); i++) {
        wchar_t id[MAX_DEVICE_ID_LEN]{};
        if (CM_Get_Device_IDW(dev.DevInst, id, MAX_DEVICE_ID_LEN, 0) != CR_SUCCESS) { wprintf(L"[%d] CM_Get_Device_ID failed\n", i); continue; }
        n++;
        wchar_t port[16] = L"?";
        for (DWORD dir = DIREG_DEV; dir <= DIREG_DRV; dir++) {
            HKEY key = SetupDiOpenDevRegKey(set, &dev, DICS_FLAG_GLOBAL, 0, dir, KEY_READ);
            if (key == INVALID_HANDLE_VALUE) { wprintf(L"    dir=%lu <no key>\n", dir); continue; }
            DWORD sz = sizeof(port), type = 0;
            LONG rq = RegQueryValueExW(key, L"PortName", nullptr, &type, (LPBYTE)port, &sz);
            RegCloseKey(key);
            wprintf(L"    dir=%lu -> %s\n", dir, rq == ERROR_SUCCESS ? port : L"<no PortName>");
            if (rq == ERROR_SUCCESS) break;
        }
        wprintf(L"[%d] %s -> %s\n", i, id, port);
    }
    SetupDiDestroyDeviceInfoList(set);
    wprintf(L"total %d port-class devnodes\n", n);
    return 0;
}

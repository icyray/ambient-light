#include "log.h"
#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cwchar>
#include <cwctype>

namespace alog {

static SRWLOCK g_lock = SRWLOCK_INIT;
static wchar_t g_path[MAX_PATH] = L"";

static void EnsurePath()
{
    if (g_path[0]) return;
    wchar_t exe[MAX_PATH] = L"";
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    wchar_t* slash = wcsrchr(exe, L'\\');
    if (slash) *slash = 0;
    _snwprintf_s(g_path, _TRUNCATE, L"%s\\log", exe);
    CreateDirectoryW(g_path, nullptr);
    wcscat_s(g_path, L"\\ambientcore.log");
}

bool DebugEnabled()
{
    static const bool on = [] {
        wchar_t buf[32] = L"";
        DWORD n = GetEnvironmentVariableW(L"AL_LOG_LEVEL", buf, 32);
        if (n == 0 || n >= 32) return false;
        for (DWORD i = 0; i < n; i++) buf[i] = (wchar_t)towlower(buf[i]);
        return wcsstr(buf, L"debug") != nullptr || wcsstr(buf, L"verbose") != nullptr;
    }();
    return on;
}

void Write(const char* lvl, const char* fmt, ...)
{
    EnsurePath();
    char msg[1024];
    va_list ap;
    va_start(ap, fmt);
    _vsnprintf_s(msg, _TRUNCATE, fmt, ap);
    va_end(ap);

    SYSTEMTIME st;
    GetLocalTime(&st);
    char line[1200];
    _snprintf_s(line, _TRUNCATE, "[%04u-%02u-%02u %02u:%02u:%02u.%03u][%s][tid %lu] %s\r\n",
                st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                lvl, GetCurrentThreadId(), msg);

    AcquireSRWLockExclusive(&g_lock);
    FILE* f = nullptr;
    if (_wfopen_s(&f, g_path, L"ab") == 0 && f) {
        fwrite(line, 1, strlen(line), f);
        fclose(f);
    }
    ReleaseSRWLockExclusive(&g_lock);
}

} // namespace alog

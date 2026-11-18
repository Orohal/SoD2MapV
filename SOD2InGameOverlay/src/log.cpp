#include "log.h"
#include <Windows.h>
#include <cstdio>
#include <cstdarg>

void LogLine(const char* line) {
    // Fixed path: GetTempPathW() inside the game process can resolve differently
    // (different environment / elevated context). C:\Users\Public is writable by all.
    const wchar_t* path = L"C:\\Users\\Public\\sod2_overlay.log";

    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, nullptr, FILE_END);

    char buf[512];
    int n = snprintf(buf, sizeof(buf), "%s\r\n", line);
    if (n > 0) {
        DWORD written = 0;
        WriteFile(h, buf, static_cast<DWORD>(n), &written, nullptr);
    }
    CloseHandle(h);
}

void LogF(const char* fmt, ...) {
    char buf[512];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    LogLine(buf);
}

template <typename T>
void LogPtr(const char* label, T ptr) {
    LogF("%s: 0x%llX", label, static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(ptr)));
}

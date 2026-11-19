#include "log.h"
#include <Windows.h>
#include <cstdio>
#include <cstdarg>
#include <cwchar>

void LogLine(const char* line) {
    // P19: per-user location instead of the world-writable C:\Users\Public.
    wchar_t base[MAX_PATH]{};
    DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", base, MAX_PATH);
    wchar_t path[MAX_PATH]{};
    if (n > 0 && n < MAX_PATH) {
        wchar_t dir[MAX_PATH]{};
        swprintf_s(dir, MAX_PATH, L"%s\\SoD2Overlay", base);
        CreateDirectoryW(dir, nullptr);
        swprintf_s(path, MAX_PATH, L"%s\\sod2_overlay.log", dir);
    } else {
        wcscpy_s(path, MAX_PATH, L"C:\\Users\\Public\\sod2_overlay.log");  // fallback
    }

    // Simple rotation: one generation, max ~1 MiB.
    HANDLE probe = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (probe != INVALID_HANDLE_VALUE) {
        LARGE_INTEGER sz{};
        if (GetFileSizeEx(probe, &sz) && sz.QuadPart > 1024 * 1024) {
            wchar_t old[MAX_PATH]{};
            swprintf_s(old, MAX_PATH, L"%s.old", path);
            DeleteFileW(old);
            MoveFileW(path, old);
        }
        CloseHandle(probe);
    }

    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, nullptr, FILE_END);

    char buf[512];
    int written = snprintf(buf, sizeof(buf), "%s\r\n", line);
    if (written > 0) {
        DWORD actual = 0;
        WriteFile(h, buf, static_cast<DWORD>(written), &actual, nullptr);
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

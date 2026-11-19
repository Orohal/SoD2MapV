#include <Windows.h>
#include <tlhelp32.h>
#include <cwchar>
#include <cstdio>

static const wchar_t* TARGET = L"StateOfDecay2-Win64-Shipping.exe";

static DWORD FindPid() {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W pe{}; pe.dwSize = sizeof(pe);
    DWORD pid = 0;
    if (Process32FirstW(snap, &pe)) {
        do {
            if (_wcsicmp(pe.szExeFile, TARGET) == 0) { pid = pe.th32ProcessID; break; }
        } while (Process32NextW(snap, &pe));
    }
    CloseHandle(snap);
    return pid;
}

static bool IsElevated() {
    HANDLE token = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
    TOKEN_ELEVATION elev{};
    DWORD size = sizeof(elev);
    BOOL ok = GetTokenInformation(token, TokenElevation, &elev, sizeof(elev), &size);
    CloseHandle(token);
    return ok && elev.TokenIsElevated;
}

static void ElevateSelf(const wchar_t* args) {
    wchar_t exe[MAX_PATH]{};
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    SHELLEXECUTEINFOW sei{};
    sei.cbSize = sizeof(sei);
    sei.lpVerb = L"runas";                 // UAC prompt
    sei.lpFile = exe;
    sei.lpParameters = args;
    sei.nShow = SW_SHOWNORMAL;
    if (!ShellExecuteExW(&sei)) {
        wprintf(L"[inject] Elevation fehlgeschlagen: %lu\n", GetLastError());
    }
}

int wmain(int argc, wchar_t** argv) {
    // No elevation check: SoD2 is started non-elevated on this machine, so
    // the injector runs at the same integrity level and OpenProcess succeeds.

    // Default: SOD2Overlay.dll next to this EXE; optional: explicit path as argv[1].
    wchar_t dllPath[MAX_PATH]{};
    if (argc > 1) {
        wcsncpy_s(dllPath, MAX_PATH, argv[1], MAX_PATH - 1);   // P17: guaranteed NUL
    } else {
        GetModuleFileNameW(nullptr, dllPath, MAX_PATH);
        wchar_t* slash = wcsrchr(dllPath, L'\\');
        if (slash) wcscpy(slash + 1, L"SOD2Overlay.dll");
        else wcscpy(dllPath, L"SOD2Overlay.dll");
    }

    DWORD pid = FindPid();
    if (!pid) {
        wprintf(L"[inject] %s nicht laufend - zuerst das Spiel starten.\n", TARGET);
        return 1;
    }

    HANDLE h = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                           PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
                           FALSE, pid);
    if (!h) {
        wprintf(L"[inject] OpenProcess(%u) fehlgeschlagen: %lu\n", pid, GetLastError());
        return 2;
    }

    size_t len = (wcslen(dllPath) + 1) * sizeof(wchar_t);
    LPVOID remote = VirtualAllocEx(h, nullptr, len, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) {
        wprintf(L"[inject] VirtualAllocEx fehlgeschlagen: %lu\n", GetLastError());
        CloseHandle(h);
        return 3;
    }
    if (!WriteProcessMemory(h, remote, dllPath, len, nullptr)) {
        wprintf(L"[inject] WriteProcessMemory fehlgeschlagen: %lu\n", GetLastError());
        VirtualFreeEx(h, remote, 0, MEM_RELEASE);
        CloseHandle(h);
        return 4;
    }

    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    LPTHREAD_START_ROUTINE loadLib =
        reinterpret_cast<LPTHREAD_START_ROUTINE>(GetProcAddress(k32, "LoadLibraryW"));
    HANDLE thread = CreateRemoteThread(h, nullptr, 0, loadLib, remote, 0, nullptr);
    if (!thread) {
        wprintf(L"[inject] CreateRemoteThread fehlgeschlagen: %lu\n", GetLastError());
        VirtualFreeEx(h, remote, 0, MEM_RELEASE);
        CloseHandle(h);
        return 5;
    }

    wprintf(L"[inject] %s in PID %u injiziert.\n", dllPath, pid);
    const bool finished = WaitForSingleObject(thread, 5000) == WAIT_OBJECT_0;
    if (finished) {
        DWORD exitCode = 0;
        GetExitCodeThread(thread, &exitCode);
        if (exitCode == 0) {
            wprintf(L"[inject] WARNUNG: LoadLibraryW im Zielprozess fehlgeschlagen "
                    L"(HMODULE=0) - meist fehlende DLL-Abhaengigkeit.\n");
        } else {
            wprintf(L"[inject] LoadLibraryW OK (HMODULE=0x%llX).\n",
                    (unsigned long long)exitCode);
        }
    } else {
        wprintf(L"[inject] LoadLibraryW thread laeuft noch (Timeout).\n");
    }
    CloseHandle(thread);
    // P17: never free the remote buffer while LoadLibraryW may still be using it.
    if (finished) VirtualFreeEx(h, remote, 0, MEM_RELEASE);
    else wprintf(L"[inject] Remote-Puffer bewusst nicht freigegeben (Thread aktiv).\n");
    CloseHandle(h);
    return 0;
}

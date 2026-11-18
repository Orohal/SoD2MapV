#include <Windows.h>
#include "hook.h"
#include "log.h"

static HANDLE g_hookThread = nullptr;

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID reserved) {
    (void)hModule; (void)reserved;
    switch (reason) {
    case DLL_PROCESS_ATTACH:
        LogLine("=== DllMain: PROCESS_ATTACH ===");
        DisableThreadLibraryCalls(hModule);
        g_hookThread = CreateThread(nullptr, 0, HookThread, nullptr, 0, nullptr);
        LogF("DllMain: hook thread = 0x%llX", (unsigned long long)g_hookThread);
        break;
    case DLL_PROCESS_DETACH:
        LogLine("=== DllMain: PROCESS_DETACH ===");
        HookShutdown();
        break;
    default:
        break;
    }
    return TRUE;
}

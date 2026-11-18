#include "hook.h"
#include "overlay.h"
#include "log.h"

#include <kiero.h>
#include <imgui.h>
#include <imgui_impl_win32.h>
#include <imgui_impl_dx11.h>
#include <d3d11.h>

#include <atomic>

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);

typedef HRESULT(__stdcall* Present_t)(IDXGISwapChain*, UINT, UINT);
typedef HRESULT(__stdcall* ResizeBuffers_t)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);

static Present_t       oPresent       = nullptr;
static ResizeBuffers_t oResizeBuffers = nullptr;
static WNDPROC         oWndProc       = nullptr;

static std::atomic<bool> g_initialized{ false };
static std::atomic<bool> g_shutdown{ false };
static HWND            g_hwnd = nullptr;
static ID3D11Device*        g_device  = nullptr;
static ID3D11DeviceContext* g_context = nullptr;

static LRESULT __stdcall HookedWndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    if (ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam))
        return 1;
    return CallWindowProc(oWndProc, hWnd, msg, wParam, lParam);
}

static bool InitImGui(IDXGISwapChain* swapChain) {
    LogLine("InitImGui: start");
    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(swapChain->GetDesc(&desc)) || !desc.OutputWindow) {
        LogLine("InitImGui: GetDesc failed / no HWND");
        return false;
    }

    g_hwnd = desc.OutputWindow;
    LogF("InitImGui: HWND=0x%llX", (unsigned long long)g_hwnd);
    if (FAILED(swapChain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&g_device)))) {
        LogLine("InitImGui: GetDevice failed");
        return false;
    }
    g_device->GetImmediateContext(&g_context);
    if (!g_context) { LogLine("InitImGui: no immediate context"); return false; }

    ImGui::CreateContext();
    ImGuiIO& io = ImGui::GetIO();
    io.IniFilename = nullptr;           // no imgui.ini in the game directory

    if (!ImGui_ImplWin32_Init(g_hwnd) || !ImGui_ImplDX11_Init(g_device, g_context)) {
        ImGui::DestroyContext();
        g_device = nullptr;
        g_context = nullptr;
        return false;
    }

    oWndProc = reinterpret_cast<WNDPROC>(SetWindowLongPtr(g_hwnd, GWLP_WNDPROC,
                                                          reinterpret_cast<LONG_PTR>(HookedWndProc)));
    g_initialized.store(true);
    LogLine("InitImGui: OK (ImGui context + DX11 + WndProc ready)");
    return true;
}

static int g_presentCount = 0;

static HRESULT __stdcall HookedPresent(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags) {
    if (g_shutdown.load()) return oPresent(swapChain, syncInterval, flags);

    if (++g_presentCount <= 3) LogF("HookedPresent: frame #%d", g_presentCount);

    if (!g_initialized.load()) {
        if (!InitImGui(swapChain)) return oPresent(swapChain, syncInterval, flags);
    }

    ImGui_ImplDX11_NewFrame();
    ImGui_ImplWin32_NewFrame();
    ImGui::NewFrame();

    OverlayDraw();                      // "SOD2 - Koordinaten" window

    ImGui::Render();
    ImDrawData* dd = ImGui::GetDrawData();
    if (g_presentCount <= 3 && dd)
        LogF("HookedPresent: DrawData drawLists=%d totalVerts=%d",
             dd->CmdListsCount, dd->TotalIdxCount);

    // UE4 binds its own render target before Present. ImGui must render into the
    // swapchain's BACK BUFFER, otherwise it is drawn into an offscreen RT and never
    // shows up. Save the game's RT, bind the backbuffer, draw, then restore.
    ID3D11RenderTargetView* savedRT = nullptr;
    ID3D11DepthStencilView* savedDS = nullptr;
    g_context->OMGetRenderTargets(1, &savedRT, &savedDS);

    ID3D11Texture2D* backBuffer = nullptr;
    if (SUCCEEDED(swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D),
                                       reinterpret_cast<void**>(&backBuffer))) && backBuffer) {
        D3D11_RENDER_TARGET_VIEW_DESC rtvDesc{};
        rtvDesc.Format = DXGI_FORMAT_UNKNOWN;
        rtvDesc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        ID3D11RenderTargetView* bbRTV = nullptr;
        if (SUCCEEDED(g_device->CreateRenderTargetView(backBuffer, &rtvDesc, &bbRTV)) && bbRTV) {
            g_context->OMSetRenderTargets(1, &bbRTV, nullptr);
            ImGui_ImplDX11_RenderDrawData(dd);
            bbRTV->Release();
        } else {
            ImGui_ImplDX11_RenderDrawData(dd);   // fallback: current RT
        }
        backBuffer->Release();
    } else {
        ImGui_ImplDX11_RenderDrawData(dd);       // fallback: current RT
    }

    // Restore the game's render target so the next frame is unaffected.
    g_context->OMSetRenderTargets(1, &savedRT, savedDS);
    if (savedRT) savedRT->Release();
    if (savedDS) savedDS->Release();

    return oPresent(swapChain, syncInterval, flags);
}

static HRESULT __stdcall HookedResizeBuffers(IDXGISwapChain* swapChain, UINT bufferCount,
                                              UINT width, UINT height, DXGI_FORMAT format, UINT flags) {
    if (g_initialized.load()) {
        ImGui_ImplDX11_InvalidateDeviceObjects();
    }
    HRESULT hr = oResizeBuffers(swapChain, bufferCount, width, height, format, flags);
    if (g_initialized.load() && g_device && g_context) {
        ImGui_ImplDX11_CreateDeviceObjects();
    }
    return hr;
}

DWORD WINAPI HookThread(LPVOID) {
    LogLine("HookThread: start");
    auto st = kiero::init(kiero::RenderType::D3D11);
    LogF("HookThread: kiero::init = %d", (int)st);
    if (st != kiero::Status::Success) {
        LogLine("HookThread: kiero init FAILED -> abort");
        return 1;
    }

    // D3D11 vtable indices: Present=8, ResizeBuffers=13
    st = kiero::bind(8, reinterpret_cast<void**>(&oPresent), reinterpret_cast<void*>(HookedPresent));
    LogF("HookThread: kiero::bind(8, Present) = %d  original=0x%llX",
         (int)st, (unsigned long long)oPresent);
    if (st != kiero::Status::Success) {
        LogLine("HookThread: Present bind FAILED -> abort");
        return 2;
    }

    st = kiero::bind(13, reinterpret_cast<void**>(&oResizeBuffers), reinterpret_cast<void*>(HookedResizeBuffers));
    LogF("HookThread: kiero::bind(13, ResizeBuffers) = %d", (int)st);

    OverlayInit();
    LogLine("HookThread: ready, waiting for Present");
    return 0;
}

void HookShutdown() {
    g_shutdown.store(true);
    if (!g_initialized.load()) return;

    if (g_hwnd && oWndProc)
        SetWindowLongPtr(g_hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(oWndProc));

    ImGui_ImplDX11_Shutdown();
    ImGui_ImplWin32_Shutdown();
    ImGui::DestroyContext();

    kiero::unbind(8);
    kiero::unbind(13);
    kiero::shutdown();
    g_initialized.store(false);
}

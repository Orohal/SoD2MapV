cmake_minimum_required(VERSION 3.20)
project(SOD2InGameOverlay LANGUAGES CXX C RC)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)

# Kiero: D3D11 hook via MinHook (both are compile-time switches in kiero.h)
# UNICODE/_UNICODE must be set too: -municode maps GetModuleHandle -> GetModuleHandleW,
# and kiero.h's KIERO_TEXT() must expand to wide strings to match.
add_compile_definitions(KIERO_INCLUDE_D3D11=1 KIERO_USE_MINHOOK=1 UNICODE _UNICODE)

set(VENDOR "${CMAKE_CURRENT_SOURCE_DIR}/vendor")
set(IMGUI_DIR "${VENDOR}/imgui")
set(KIERO_DIR "${VENDOR}/kiero")
set(MINHOOK_DIR "${VENDOR}/MinHook")

foreach(d "${IMGUI_DIR}/imgui.h" "${KIERO_DIR}/kiero.h" "${MINHOOK_DIR}/include/MinHook.h")
    if(NOT EXISTS "${d}")
        message(FATAL_ERROR "Missing dependency: ${d}. Run build.ps1 first.")
    endif()
endforeach()

# --- ImGui sources ---
set(IMGUI_SOURCES
    ${IMGUI_DIR}/imgui.cpp
    ${IMGUI_DIR}/imgui_draw.cpp
    ${IMGUI_DIR}/imgui_tables.cpp
    ${IMGUI_DIR}/imgui_widgets.cpp
    ${IMGUI_DIR}/backends/imgui_impl_win32.cpp
    ${IMGUI_DIR}/backends/imgui_impl_dx11.cpp
)

# --- MinHook sources ---
set(MINHOOK_SOURCES
    ${MINHOOK_DIR}/src/hook.c
    ${MINHOOK_DIR}/src/buffer.c
    ${MINHOOK_DIR}/src/trampoline.c
    ${MINHOOK_DIR}/src/hde/hde32.c
    ${MINHOOK_DIR}/src/hde/hde64.c
)

# --- Overlay DLL (injected into the game) ---
add_library(SOD2Overlay SHARED
    src/dllmain.cpp
    src/hook.cpp
    src/game.cpp
    src/overlay.cpp
    src/log.cpp
    ${KIERO_DIR}/kiero.cpp
    ${IMGUI_SOURCES}
    ${MINHOOK_SOURCES}
)

target_include_directories(SOD2Overlay PRIVATE
    src
    ${VENDOR}                 # kiero includes "minhook/include/MinHook.h" (case-insensitive on Windows)
    ${KIERO_DIR}
    ${IMGUI_DIR}
    ${IMGUI_DIR}/backends
    ${MINHOOK_DIR}/include
    ${MINHOOK_DIR}/src/hde
)

set_target_properties(SOD2Overlay PROPERTIES PREFIX "")  # MinGW would emit libSOD2Overlay.dll

target_link_libraries(SOD2Overlay PRIVATE d3d11 dxgi dwmapi d3dcompiler)

# MinGW: ImGui's DX11 backend calls D3DCompile; ensure the import lib is on the link line
# even when CMake's plain name lookup resolves to a versioned lib only.
if(MINGW)
    target_link_options(SOD2Overlay PRIVATE "-L" "D:/Tools/msys64/ucrt64/lib" "-ld3dcompiler")
endif()

# --- Injector EXE (manifest forces admin elevation on double-click) ---
add_executable(Sod2Injector WIN32
    src/injector.cpp
    src/injector.rc
)

if(MINGW)
    target_compile_options(SOD2Overlay PRIVATE -municode -finput-charset=UTF-8)
    target_link_options(SOD2Overlay PRIVATE -municode -static)
    target_compile_options(Sod2Injector PRIVATE -municode -finput-charset=UTF-8)
    target_link_options(Sod2Injector PRIVATE -municode -static)
endif()

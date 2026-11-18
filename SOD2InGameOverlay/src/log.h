#pragma once
#include <cstdint>

// Writes to %TEMP%\sod2_overlay.log (append). Safe to call from any thread.
void LogLine(const char* line);
void LogF(const char* fmt, ...);

template <typename T> void LogPtr(const char* label, T ptr);

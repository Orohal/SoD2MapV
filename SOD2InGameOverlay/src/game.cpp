#include "game.h"
#include "log.h"
#include <Windows.h>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <unordered_map>

uintptr_t Game::ModuleBase() { return reinterpret_cast<uintptr_t>(GetModuleHandle(nullptr)); }

static bool Plausible(uintptr_t p) { return p >= 0x10000 && p < 0x7FFFFFFEFFFFULL; }
static bool CanRead(uintptr_t addr, size_t size) {
    if (!Plausible(addr)) return false;
    MEMORY_BASIC_INFORMATION mbi{};
    if (!VirtualQuery(reinterpret_cast<LPCVOID>(addr), &mbi, sizeof(mbi))) return false;
    if (mbi.State != MEM_COMMIT) return false;
    DWORD pr = mbi.Protect;
    if (!(pr & (PAGE_READONLY|PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY))) return false;
    uintptr_t start = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
    return (addr >= start) && (addr + size <= start + mbi.RegionSize);
}
static uintptr_t SafePtr(uintptr_t addr) {
    return CanRead(addr, sizeof(uintptr_t)) ? *reinterpret_cast<uintptr_t*>(addr) : 0;
}

// Cross-thread state (ScanThread writes, render thread reads) MUST be atomic.
// 'volatile' is not a synchronisation primitive in C++ and caused a data race.
static std::atomic<uintptr_t> g_coordAddr{ 0 };
static std::atomic<bool>       g_scanDone{ true };   // true = idle/ready, false = scan running
static std::atomic<bool>       g_hasResult{ false };

// NOTE: g_candidates / g_hotRegions / g_lastGood below are ScanThread-only.
// Only the three atomics above (plus the locked address) are published to the
// render thread, so no further locking is required.

// Top candidate addresses tracked simultaneously; the live one keeps changing.
struct TrackAddr { uintptr_t addr; int cnt; };
static TrackAddr g_candidates[16];
static int g_candidateCount = 0;

// Hot regions from the first full scan, for fast periodic re-scans.
struct Region { uintptr_t base; size_t size; };
static Region g_hotRegions[64];
static int g_hotCount = 0;
static FVector g_lastGood{};
static bool g_haveLast = false;

// Read the seed triple from C:\Users\Public\sod2_seed.txt
static bool ReadSeed(FVector& seed) {
    FILE* f = nullptr;
    fopen_s(&f, "C:\\Users\\Public\\sod2_seed.txt", "r");
    if (!f) return false;
    float x, y, z;
    int n = fscanf_s(f, "%f %f %f", &x, &y, &z);
    fclose(f);
    if (n != 3) return false;
    seed = { x, y, z };
    return true;
}

struct Hit { uintptr_t addr; FVector v; };
static bool FVecNear(const FVector& v, const FVector& seed, float tol) {
    return std::fabs(v.X - seed.X) <= tol &&
           std::fabs(v.Y - seed.Y) <= tol &&
           std::fabs(v.Z - seed.Z) <= tol;
}

// Scan committed readable memory for float triples near the seed. Group hits by
// value; prefer small clusters (2..8 references = player components), penalise
// suspiciously round values (nav/grid points). Port of LiveCoords.FindPlayerCluster.
static void SeedScan(const FVector& seed, float tol, bool fullScan) {
    LogF("SeedScan: seed=(%.2f,%.2f,%.2f) tol=%.1f full=%d", seed.X, seed.Y, seed.Z, tol, fullScan?1:0);
    g_candidateCount = 0;  // reset candidates for this scan
    const int MAXHITS = 20000;
    static Hit hits[MAXHITS];
    int hitCount = 0;

    const size_t chunk = 1 << 20;
    const size_t overlap = 32;

    // P04: read each 4 KiB page through ReadProcessMemory instead of
    // dereferencing it. RPM fails cleanly if the game frees or reprotects a
    // page between VirtualQuery and the read (TOCTOU) — no access violation
    // inside the game. This replaces both the validated and the "fast" path.
    auto scanRegion = [&](uintptr_t start, uintptr_t end) {
        constexpr uintptr_t PAGE = 0x1000;
        alignas(16) unsigned char buf[PAGE];
        for (uintptr_t pos = start; pos + 12 <= end && hitCount < MAXHITS; pos += PAGE) {
            uintptr_t hi = std::min<uintptr_t>(end, pos + PAGE);
            SIZE_T want = static_cast<SIZE_T>(hi - pos);
            SIZE_T got = 0;
            if (!ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<LPCVOID>(pos),
                                   buf, want, &got) || got < 12) continue;
            for (size_t off = 0; off + 12 <= got && hitCount < MAXHITS; off += 4) {
                FVector v{};
                std::memcpy(&v, buf + off, sizeof(v));
                if (!std::isfinite(v.X) || !std::isfinite(v.Y) || !std::isfinite(v.Z)) continue;
                if (FVecNear(v, seed, tol)) {
                    hits[hitCount++] = { pos + off, v };
                    if (hitCount >= MAXHITS) break;
                }
            }
        }
    };

    if (fullScan) {
        g_hotCount = 0;
        MEMORY_BASIC_INFORMATION mbi{};
        uintptr_t addr = 0;
        while (VirtualQuery(reinterpret_cast<LPCVOID>(addr), &mbi, sizeof(mbi)) == sizeof(mbi)) {
            uintptr_t start = reinterpret_cast<uintptr_t>(mbi.BaseAddress);
            uintptr_t regionSize = mbi.RegionSize;
            uintptr_t end = start + regionSize;
            if (regionSize == 0) break;
            if (mbi.State == MEM_COMMIT &&
                !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)) &&
                (mbi.Protect & (PAGE_READONLY|PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY))) {
                scanRegion(start, end);   // RPM validates every page as it reads
                // Remember large writable heap regions for fast re-scans.
                // Use region properties, not hit count (which hits MAXHITS early).
                bool writable = (mbi.Protect & (PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)) != 0;
                if (start >= 0x18000000000ULL && regionSize >= 0x100000 && writable && g_hotCount < 64)
                    g_hotRegions[g_hotCount++] = { start, regionSize };
            }
            if (end <= addr) break;
            addr = end;
        }
        LogF("SeedScan: hot regions=%d", g_hotCount);
    } else {
        for (int r = 0; r < g_hotCount; r++)
            scanRegion(g_hotRegions[r].base, g_hotRegions[r].base + g_hotRegions[r].size);  // cached regions -> re-validated by RPM
    }

    LogF("SeedScan: raw hits=%d", hitCount);
    if (hitCount == 0) return;

    // P14c: group by quantized value (0.1 cm) via hash map instead of the
    // previous O(hits * groups) linear scan (up to 20k * 4096 comparisons).
    struct Group { int count = 0; uintptr_t addr = 0; FVector v{}; };
    std::unordered_map<uint64_t, Group> groups;
    groups.reserve(4096);
    for (int i = 0; i < hitCount; i++) {
        int ix = static_cast<int>(std::lround(hits[i].v.X * 10));
        int iy = static_cast<int>(std::lround(hits[i].v.Y * 10));
        int iz = static_cast<int>(std::lround(hits[i].v.Z * 10));
        uint64_t key = (static_cast<uint64_t>(static_cast<uint32_t>(ix)) << 42)
                     ^ (static_cast<uint64_t>(static_cast<uint32_t>(iy)) << 21)
                     ^ static_cast<uint32_t>(iz);
        Group& g = groups[key];
        if (g.count == 0)
            g = { 0, hits[i].addr, { ix / 10.0f, iy / 10.0f, iz / 10.0f } };
        g.count++;
        g.addr = hits[i].addr; // last address seen for this value
    }

    auto roundness = [](const FVector& v) -> int {
        int p = 0;
        if (std::fabs(std::fmod(v.X, 100.0f)) < 1.5f) p += 2;
        if (std::fabs(std::fmod(v.Y, 100.0f)) < 1.5f) p += 2;
        if (std::fabs(std::fmod(v.Z, 100.0f)) < 1.5f) p += 2;
        return p;
    };

    // Only dynamic heap (>= 0x18000000000). Collect the strongest-referenced
    // clusters as candidates: the live player coordinate is shared by many
    // components (capsule/mesh/controller) and thus has a high count. Static
    // breadcrumbs have low counts and go stale -> excluded.
    for (const auto& kv : groups) {
        const Group& grp = kv.second;
        if (grp.addr < 0x18000000000ULL) continue;
        float dx = grp.v.X - seed.X;
        float dy = grp.v.Y - seed.Y;
        float dz = grp.v.Z - seed.Z;
        float dist = std::sqrt(dx*dx + dy*dy + dz*dz);
        if (dist > 30000.0f) continue; // 300 m: save may lag behind live pos after char rotation
        // Insert sorted by count descending into top candidates.
        if (g_candidateCount < 16) {
            int k = g_candidateCount++;
            while (k > 0 && g_candidates[k-1].cnt < grp.count) {
                g_candidates[k] = g_candidates[k-1]; k--;
            }
            g_candidates[k] = { grp.addr, grp.count };
        } else if (grp.count > g_candidates[15].cnt) {
            g_candidates[15] = { grp.addr, grp.count };
        }
    }

    // Choose the candidate that MOVED from the previous value (delta > 5cm):
    // the live player coordinate changes with input; the stale breadcrumb/UI
    // value stays exactly equal to the seed and is therefore skipped.
    uintptr_t chosen = 0;
    float bestDist = 1e30f;
    FVector chosenVal{};
    for (int i = 0; i < g_candidateCount; i++) {
        uintptr_t a = g_candidates[i].addr;
        if (!CanRead(a, sizeof(FVector))) continue;
        FVector v = *reinterpret_cast<const FVector*>(a);
        if (!std::isfinite(v.X) || !std::isfinite(v.Y) || !std::isfinite(v.Z)) continue;
        if (std::fabs(v.X) + std::fabs(v.Y) + std::fabs(v.Z) < 1.0f) continue;
        float dx = v.X - seed.X, dy = v.Y - seed.Y, dz = v.Z - seed.Z;
        float d = std::sqrt(dx*dx + dy*dy + dz*dz);

        if (fullScan) {
            // First scan: pick closest to the save seed (player is near the save).
            if (d < bestDist) { bestDist = d; chosen = a; chosenVal = v; }
        } else {
            // Re-scan: skip the exact-old value (stale), pick the nearest
            // candidate within the search window.
            if (d < 5.0f || d > 30000.0f) continue;
            if (d < bestDist) { bestDist = d; chosen = a; chosenVal = v; }
        }
    }
    if (chosen) {
        g_coordAddr = chosen;
        g_lastGood = chosenVal; g_haveLast = true;
        g_hasResult = true;
        LogF("SeedScan: LOCKED addr=0x%llX dist=%.1f val=(%.1f,%.1f,%.1f)",
             (unsigned long long)chosen, bestDist, chosenVal.X, chosenVal.Y, chosenVal.Z);
    } else {
        LogF("SeedScan: no mover found (keeping previous lock)");
    }
}

// ON-DEMAND scan. No watcher, no timestamp polling, no automatic re-scan.
// SoD2 has anti-reading measures; a continuous scanner both triggers those
// and burns CPU. The user requests a scan explicitly ("Scan jetzt" button).
static DWORD WINAPI ScanThread(LPVOID) {
    FVector seed{};
    if (!ReadSeed(seed)) {
        LogF("Scan: could not read seed file");
        g_scanDone = true;
        return 0;
    }
    LogF("Scan: requested, seed=(%.1f,%.1f,%.1f)", seed.X, seed.Y, seed.Z);
    g_hasResult = false;
    SeedScan(seed, 10000.0f, true);
    g_scanDone = true;
    LogF("Scan: finished");
    return 0;
}

void Game::RequestScan() {
    if (!g_scanDone) return;           // already scanning
    g_scanDone = false;
    HANDLE h = CreateThread(nullptr, 0, ScanThread, nullptr, 0, nullptr);
    if (h) CloseHandle(h);
}

bool Game::IsScanning() { return !g_scanDone; }
bool Game::HasResult()  { return g_hasResult; }

PlayerState Game::Read() {
    PlayerState out{};

    // Only display the locked value. No polling, no re-scan, no feedback loop.
    // P04: ReadProcessMemory also protects this read against a freed hot region.
    const uintptr_t addr = g_coordAddr.load();
    if (addr) {
        FVector v{};
        SIZE_T got = 0;
        if (ReadProcessMemory(GetCurrentProcess(), reinterpret_cast<LPCVOID>(addr),
                              &v, sizeof(v), &got) &&
            got == sizeof(v) &&
            std::isfinite(v.X) && std::isfinite(v.Y) && std::isfinite(v.Z) &&
            std::fabs(v.X) < 500000 && std::fabs(v.Y) < 500000 &&
            (std::fabs(v.X) + std::fabs(v.Y) + std::fabs(v.Z)) > 1.0f) {
            out.Location = v;
            out.root = addr;
            out.valid = true;
        }
    }

    return out;
}

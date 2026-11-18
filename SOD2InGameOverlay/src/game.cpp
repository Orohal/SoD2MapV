#include "game.h"
#include "log.h"
#include <Windows.h>
#include <cmath>
#include <cstdio>
#include <cstdlib>

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

// Live coordinate address found by the seed scan. Once locked, we poll it.
static volatile uintptr_t g_coordAddr = 0;
static volatile bool g_scanDone = false;

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

    auto scanRegion = [&](uintptr_t start, uintptr_t end) {
        for (uintptr_t pos = start; pos + 12 <= end && hitCount < MAXHITS;) {
            uintptr_t want = (chunk < end - pos) ? chunk : (end - pos);
            for (uintptr_t p = pos; p + 12 <= pos + want && hitCount < MAXHITS; p += 4) {
                FVector v = *reinterpret_cast<const FVector*>(p);
                if (!std::isfinite(v.X) || !std::isfinite(v.Y) || !std::isfinite(v.Z)) continue;
                if (FVecNear(v, seed, tol)) {
                    hits[hitCount++] = { p, v };
                    if (hitCount >= MAXHITS) break;
                }
            }
            pos += (want > overlap) ? (want - overlap) : want;
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
                scanRegion(start, end);
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
            scanRegion(g_hotRegions[r].base, g_hotRegions[r].base + g_hotRegions[r].size);
    }

    LogF("SeedScan: raw hits=%d", hitCount);
    if (hitCount == 0) return;

    // Group by rounded value (to 0.1 cm).
    struct Group { int count; uintptr_t addr; FVector v; };
    static Group groups[4096];
    int groupCount = 0;
    for (int i = 0; i < hitCount; i++) {
        float rx = std::round(hits[i].v.X * 10) / 10;
        float ry = std::round(hits[i].v.Y * 10) / 10;
        float rz = std::round(hits[i].v.Z * 10) / 10;
        int gi = -1;
        for (int g = 0; g < groupCount; g++) {
            if (groups[g].v.X == rx && groups[g].v.Y == ry && groups[g].v.Z == rz) { gi = g; break; }
        }
        if (gi < 0 && groupCount < 4096) {
            gi = groupCount++;
            groups[gi] = { 0, hits[i].addr, { rx, ry, rz } };
        }
        if (gi >= 0) {
            groups[gi].count++;
            groups[gi].addr = hits[i].addr; // last address seen for this value
        }
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
    for (int g = 0; g < groupCount; g++) {
        if (groups[g].addr < 0x18000000000ULL) continue;
        float dx = groups[g].v.X - seed.X;
        float dy = groups[g].v.Y - seed.Y;
        float dz = groups[g].v.Z - seed.Z;
        float dist = std::sqrt(dx*dx + dy*dy + dz*dz);
        if (dist > 5000.0f) continue; // near seed only
        // Insert sorted by count descending into top candidates.
        if (g_candidateCount < 16) {
            int k = g_candidateCount++;
            while (k > 0 && g_candidates[k-1].cnt < groups[g].count) {
                g_candidates[k] = g_candidates[k-1]; k--;
            }
            g_candidates[k] = { groups[g].addr, groups[g].count };
        } else if (groups[g].count > g_candidates[15].cnt) {
            g_candidates[15] = { groups[g].addr, groups[g].count };
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
            // Re-scan: skip the exact-old value (stale), pick the nearest MOVER
            // within a plausible movement radius (<= 10000 cm in 2.5 s).
            if (d < 5.0f || d > 10000.0f) continue;
            if (d < bestDist) { bestDist = d; chosen = a; chosenVal = v; }
        }
    }
    if (chosen) {
        g_coordAddr = chosen;
        g_lastGood = chosenVal; g_haveLast = true;
        LogF("SeedScan: LOCKED addr=0x%llX dist=%.1f val=(%.1f,%.1f,%.1f)",
             (unsigned long long)chosen, bestDist, chosenVal.X, chosenVal.Y, chosenVal.Z);
    } else {
        LogF("SeedScan: no mover found (keeping previous lock)");
    }
}

// Background thread: ONE full scan. The result is a one-time measurement;
// no periodic re-scan (that cost 363% CPU and killed the render thread).
static DWORD WINAPI ScanThread(LPVOID) {
    FVector seed{};
    if (!ReadSeed(seed)) {
        LogF("SeedScan: could not read seed file");
        g_scanDone = true;
        return 0;
    }
    SeedScan(seed, 3000.0f, true);
    LogF("ScanThread: done");
    g_scanDone = true;
    return 0;
}

PlayerState Game::Read() {
    PlayerState out{};

    if (!g_scanDone) {
        static bool started = false;
        if (!started) {
            started = true;
            HANDLE h = CreateThread(nullptr, 0, ScanThread, nullptr, 0, nullptr);
            if (h) CloseHandle(h);
        }
    }

    if (g_coordAddr && CanRead(g_coordAddr, sizeof(FVector))) {
        out.Location = *reinterpret_cast<const FVector*>(g_coordAddr);
        if (std::isfinite(out.Location.X) && std::fabs(out.Location.X) < 500000) {
            out.root = g_coordAddr;
            out.valid = true;
        }
    }

    return out;
}

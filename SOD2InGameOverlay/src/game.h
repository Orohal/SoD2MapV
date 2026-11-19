#pragma once
#include <cstdint>

struct FVector { float X, Y, Z; };

struct PlayerState {
    FVector Location{};
    FVector Velocity{};
    bool valid = false;
    bool hasVelocity = false;
    uintptr_t world = 0, gameInstance = 0, localPlayer = 0;
    uintptr_t controller = 0, pawn = 0, root = 0, movement = 0;
};

namespace Game {
    // Verified from this exact binary (SHA-256 ed16abe5...), see
    // tools/LiveCoords/SOD2_coordinate_report.md for the full evidence.
    constexpr uintptr_t RVA_GWORLD            = 0x45492D0;
    constexpr uintptr_t OFF_WORLD_GI          = 0x140;
    constexpr uintptr_t OFF_GI_LOCALPLAYERS   = 0x38;   // TArray data
    constexpr uintptr_t OFF_GI_LOCALPLAYERS_N = 0x40;   // TArray num
    constexpr uintptr_t OFF_LOCALPLAYER_PC    = 0x30;
    constexpr uintptr_t OFF_PC_PAWN           = 0x3D8;  // AcknowledgedPawn
    constexpr uintptr_t OFF_PAWN_ROOT         = 0x1B0;  // AActor::RootComponent
    constexpr uintptr_t OFF_ROOT_LOCATION     = 0x170;  // ComponentToWorld translation
    constexpr uintptr_t OFF_CHARACTER_MOVEMENT= 0x3D8;  // ACharacter::CharacterMovement (or 0x3B8)
    constexpr uintptr_t OFF_CMC_VELOCITY      = 0x10C;

    uintptr_t ModuleBase();
    PlayerState Read();

    // On-demand scan: only called when the user clicks "Scan jetzt".
    // No background polling, no watcher, no automatic re-scan.
    void RequestScan();
    bool IsScanning();
    bool HasResult();
}

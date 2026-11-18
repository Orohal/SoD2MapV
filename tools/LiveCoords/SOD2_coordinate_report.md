# State of Decay 2 — Static Reverse-Engineering Report: Player World Coordinates
Target: `D:\Games\State of Decay 2\StateOfDecay2\Binaries\Win64\StateOfDecay2-Win64-Shipping.exe`
SHA-256: `ed16abe5f950443f16c93dfd6267cea2469b7769f647183d430cbd01b8d69f5a`
PE32+ x86-64, ImageBase `0x140000000`, 76,998,672 bytes, `.pdata` = 259,728 function ranges.

All offsets below were recovered from the binary's OWN Unreal class-registration code (pattern:
`lea r9,[rip -> property-name]` … `mov r9d, IMM` … `call 0x1411d28a0`), NOT assumed from generic UE4.
All addresses verified by instruction-level disassembly (Capstone) of this exact file.

## 1. CONFIRMED PROPERTY OFFSETS

### UCharacterMovementComponent — registration 0x142183BBF–0x14218937E
| Property | Offset | Property | Offset |
|---|---|---|---|
| CharacterOwner | 0x190 | MovementMode | 0x1B0 |
| CustomMovementMode | 0x1B1 | GravityScale | 0x198 |
| MaxStepHeight | 0x19C | JumpZVelocity | 0x1A0 |
| WalkableFloorAngle | 0x1A8 | WalkableFloorZ | 0x1AC |
| MaxWalkSpeed | 0x1D4 | MaxWalkSpeedCrouched | 0x1D8 |
| MaxSwimSpeed | 0x1DC | MaxFlySpeed | 0x1E0 |
| MaxCustomMovementSpeed | 0x1E4 | MaxAcceleration | 0x1E8 |
| BrakingFrictionFactor | 0x1EC | BrakingFriction | 0x1F0 |
| BrakingDecelerationWalking | 0x1F8 | RotationRate | 0x228 |
| Acceleration | 0x280 | LastUpdateLocation | 0x28C |
| LastUpdateRotation | 0x2A0 | LastUpdateVelocity | 0x2B0 |
| MaxSimulationIterations | 0x2EC | CurrentFloor | 0x328 |
| PostPhysicsTickFunction | 0x4C0 | CurrentRootMotion | 0x528 |

### UMovementComponent — registration 0x14218FD60–0x14218FFFF
| Property | Offset |
|---|---|
| UpdatedComponent | 0xF8 |
| UpdatedPrimitive | 0x100 |
| Velocity | 0x10C |  <-- FVector X/Y/Z (inherited by UCharacterMovementComponent)

### AActor — registration 0x1420C7332–0x1420CA9C4
| Property | Offset | Property | Offset |
|---|---|---|---|
| RemoteRole | 0xD8 | Owner | 0xE0 |
| ReplicatedMovement | 0xE8 | ... | |
| Children | 0x1A0 | **RootComponent** | **0x1B0** |
| ControllingMatineeActors | 0x1B8 | ... | |
| InstanceComponents | 0x348 | | |

### ACharacter — registration 0x14216E80B–0x14216E922
| Property | Offset |
|---|---|
| Mesh | 0x3D0 |
| **CharacterMovement** | **0x3D8** |

### USceneComponent — registration 0x1420E8CFF–0x1420EA25E
| Property | Offset |
|---|---|
| ComponentVelocity | 0x138 |
| RelativeScale3D | 0x14C |
| **ComponentToWorld** (FTransform) | **0x160** (Rotation 0x160, Translation 0x170, Scale3D 0x180) |
| RelativeLocation | 0x1B0 |
| RelativeRotation | 0x1BC |

### UWorld — registrations 0x1422FF098–0x14230019E and 0x14230021F–0x14230032B
| Property | Offset |
|---|---|
| **PersistentLevel** | **0x30** |
| GameState | 0x58 |
| CurrentLevel | 0x138 |
| **OwningGameInstance** | **0x140** |
| Levels | 0x128 |

### UGameInstance — registration 0x14222E269–0x14222E4C9
| Property | Offset |
|---|---|
| **LocalPlayers** (TArray) | **0x38** |

### ULocalPlayer — registration 0x1423C7D80–0x1423C7E95
| Property | Offset |
|---|---|
| **PlayerController** | **0x30** |
| CurrentNetSpeed | 0x38 |

### APlayerController — registration 0x1420DDDD6–0x1420DE051
| Property | Offset |
|---|---|
| Player | 0x3C8 |
| **AcknowledgedPawn** | **0x3D8** |
| MyHUD | 0x3F0 |
| PlayerCameraManager | 0x3F8 |

## 2. GLOBAL ROOT — GWorld
**`GWorld` global pointer variable: VA `0x1445492D0`** (RVA `0x45492D0`, file offset `0x45480D0`, section `.data`).

Evidence:
- `0x140174081: mov qword ptr [rip+...], rdi` — writes a UWorld* into `0x1445492D0` (UWorld context setup; also sets `[rdi+0x138]`).
- `0x14017D4B3: mov rax,[rip -> 0x1445492D0]; ... mov rcx,[rax+0x138]` — reads world, dereferences CurrentLevel.
- `0x1401885B0: mov rax,[rip -> 0x1445492D0]; mov rcx,[rax+0x138]` — same.
- `0x140177FA3: mov [rip -> 0x1445492D0], rax` — additional write/refresh.

## 3. COMPLETE VERIFIED POINTER CHAIN
```
UWorld* world = *(UWorld**)(moduleBase + 0x45492D0)
UGameInstance* gi = *(void**)((char*)world + 0x140)
ULocalPlayer* lp = TArray_data(gi + 0x38)[0]        // Data ptr at +0x00, Num at +0x08
APlayerController* pc = *(void**)((char*)lp + 0x30)
AActor* pawn = *(void**)((char*)pc + 0x3D8)         // AcknowledgedPawn
USceneComponent* root = *(void**)((char*)pawn + 0x1B0)
float X = *(float*)((char*)root + 0x170)
float Y = *(float*)((char*)root + 0x174)
float Z = *(float*)((char*)root + 0x178)
```

Velocity path (alternative / additional):
```
ACharacter* ch = pawn
UCharacterMovementComponent* cmc = *(void**)((char*)ch + 0x3D8)
float VX = *(float*)((char*)cmc + 0x10C)
float VY = *(float*)((char*)cmc + 0x110)
float VZ = *(float*)((char*)cmc + 0x114)
USceneComponent* updated = *(void**)((char*)cmc + 0xF8)   // same as RootComponent typically
```

## 4. INSTRUCTION-LEVEL EVIDENCE FOR COORDINATE ACCESS (multiple independent confirmations)
`AActor::RootComponent=0x1B0` and `SceneComponent+0x170 = world translation` verified in dozens of functions:
```
0x1407069EF: mov rcx,[rax+0x1B0]        ; RootComponent
0x140706A03: movaps xmm1,[rcx+0x170]    ; X/Y/Z (+W)
0x140706A0D: movss [rbp-0x10],xmm1      ; X
0x140706A1A: shufps ...; movss [rbp-0xC], xmm0  ; Y
0x140706A1A: movss [rbp-8], xmm1        ; Z

0x141C34E46: mov rax,[rsi+0xF8]         ; UMovementComponent::UpdatedComponent
0x141C34E57: movaps xmm1,[rax+0x170]    ; component world translation
0x141C34E78: movaps xmm0,[rax+0x160]    ; component world rotation

0x14015A079: mov rax,[rbx+0x1B0]
0x14015A085: movaps xmm1,[rax+0x170]    ; GetActorLocation-style inlined accessor
```
FVector extraction order confirmed: `[+0x170]=X, [+0x174]=Y, [+0x178]=Z` (shufps 0x55/0xAA pattern).

## 5. FUNCTION ADDRESSES (all confirmed)
| Function / structure | VA | RVA | File offset |
|---|---|---|---|
| UCharacterMovementComponent registration | 0x142183BBF | 0x2183BBF | 0x21831BF |
| UMovementComponent registration | 0x14218FD60 | 0x218FD60 | 0x218F360 |
| AActor registration | 0x1420C7332 | 0x20C7332 | 0x20C6932 |
| ACharacter registration | 0x14216E80B | 0x216E80B | 0x216DE0B |
| USceneComponent registration | 0x1420E8CFF | 0x20E8CFF | 0x20E82FF |
| APlayerController registration | 0x1420DDDD6 | 0x20DDDD6 | 0x20DD3D6 |
| UWorld registration | 0x1422FF098 | 0x22FF098 | 0x22FE698 |
| UGameInstance registration | 0x14222E269 | 0x222E269 | 0x222D869 |
| ULocalPlayer registration | 0x1423C7D80 | 0x23C7D80 | 0x23C7380 |
| GWorld global variable | 0x1445492D0 | 0x45492D0 | 0x45480D0 |
| GWorld write (init) | 0x140174081 | 0x174081 | 0x173681 |
| GWorld read + CurrentLevel | 0x14017D4B3 / 0x1401885B0 | 0x17D4B3 / 0x1885B0 | 0x17CAB3 / 0x187BB0 |
| K2_GetActorLocation UFunction registration | 0x142105BD9 | 0x2105BD9 | 0x21051D9 |
| RootComponent→location example (TetherComp) | 0x1407069EF | 0x7069EF | 0x705FEF |
| CMC functions w/ SimulatedTick | 0x141C34E02 / 0x141C34FED | 0x1C34E02 | 0x1C34402 / 0x1C345ED |
| CMC StartNewPhysics candidate | 0x141C376D0 | 0x1C376D0 | 0x1C36CD0 |
| CMC PerformMovement candidate | 0x141C25F8B | 0x1C25F8B | 0x1C2558B |

## 6. CONFIDENCE
- CONFIRMED: all property offsets (from registration code), GWorld address, RootComponent/Location access pattern, chain structure.
- HIGH (needs runtime sanity check): LocalPlayers[0] presence in single-player; AcknowledgedPawn population; ACharacter::CharacterMovement=0x3D8 (paired with Mesh in one registration; a second `CharacterMovement` property exists at 0x3B8 in a different SOD2 class — validate which class your pawn is).
- NOT statically determinable: exact runtime values, whether a given pawn is the local player (validate via controller==local PC), camera/view matrix for screen projection.

## 7. DLL FEASIBILITY
- Read-only: `OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, ...)`, `ReadProcessMemory` only. No writes, no hooks, no patches.
- Process detection: Toolhelp32 snapshot on `StateOfDecay2-Win64-Shipping.exe`, or FindWindow on the game window.
- ASLR: compute module base at runtime, add RVAs above (never use absolute VAs directly).
- Pointer chain: validate each dereference non-null and within a plausible range; re-resolve each frame (objects can be recreated on map travel).
- Update rate: 30–60 Hz (16–33 ms) via `Sleep`/waitable timer; cache module base.
- Coordinate conversion: UE4 units are centimetres, +X forward, +Y right, +Z up. World→screen requires camera view-projection (PlayerCameraManager +0x3F8 → view camera cache); not resolved in this static pass.

## 8. RUNTIME VALIDATION CHECKLIST
1. Confirm `*(void**)(base+0x45492D0)` is a valid UWorld (e.g. `[w+0x30]` PersistentLevel non-null, `[w+0x140]` GameInstance non-null).
2. Confirm `LocalPlayers.Num >= 1` and `PlayerController` non-null.
3. Confirm `AcknowledgedPawn` non-null and its `RootComponent+0x170` changes when the player moves.
4. Confirm velocity at `pawn->CharacterMovement(0x3D8)+0x10C` matches observed movement.
5. Validate ACharacter::CharacterMovement offset (0x3D8 vs 0x3B8) by checking which yields a plausible movement component.

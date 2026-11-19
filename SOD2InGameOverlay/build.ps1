$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vendor = Join-Path $root 'vendor'
New-Item -ItemType Directory -Force -Path $vendor | Out-Null

# P26: vendors are cloned at HEAD (--depth 1); pin by verifying the expected
# version marker after checkout so an upstream drift fails loudly instead of
# silently changing the build.
$repos = @(
    @{ Name = 'imgui';   Url = 'https://github.com/ocornut/imgui.git'; Marker = 'imgui.h';          Expected = 'IMGUI_VERSION_NUM   19303' },
    @{ Name = 'kiero';   Url = 'https://github.com/Rebzzel/kiero.git'; Marker = 'kiero.h';          Expected = 'KIERO_VERSION "1.2.12"' },
    @{ Name = 'MinHook'; Url = 'https://github.com/TsudaKageyu/minhook.git'; Marker = 'include/MinHook.h'; Expected = '' }
)
foreach ($r in $repos) {
    $dest = Join-Path $vendor $r.Name
    if (-not (Test-Path (Join-Path $dest $r.Marker))) {
        Write-Host "Klone $($r.Name) ..."
        git clone --depth 1 $r.Url $dest
        if ($LASTEXITCODE -ne 0) { throw "git clone $($r.Name) fehlgeschlagen." }
    }
    if (-not [string]::IsNullOrEmpty($r.Expected)) {
        $markerPath = Join-Path $dest $r.Marker
        $content = Get-Content $markerPath -Raw
        if (-not $content.Contains($r.Expected)) {
            throw "Vendor $($r.Name) hat eine unerwartete Version (erwartet: $($r.Expected)). Bitte build.ps1 aktualisieren."
        }
    }
}

# P26: make the toolchain path configurable, keep the old path as a default.
$mingwBin = $env:SOD2_MINGW_BIN
if ([string]::IsNullOrWhiteSpace($mingwBin)) { $mingwBin = 'D:\Tools\msys64\ucrt64\bin' }
if (-not (Test-Path $mingwBin)) {
    throw "MinGW nicht gefunden: $mingwBin - Umgebungsvariable SOD2_MINGW_BIN setzen."
}
$env:PATH = "$mingwBin;$env:PATH"

$build = Join-Path $root 'build'
cmake -S $root -B $build -G "MinGW Makefiles" `
    -DCMAKE_CXX_COMPILER="$mingwBin/g++.exe" `
    -DCMAKE_C_COMPILER="$mingwBin/gcc.exe"
if ($LASTEXITCODE -ne 0) { throw "cmake configure fehlgeschlagen." }
cmake --build $build --parallel
if ($LASTEXITCODE -ne 0) { throw "build fehlgeschlagen." }

Write-Host ""
Write-Host "Fertig: $build\SOD2Overlay.dll  +  $build\Sod2Injector.exe"

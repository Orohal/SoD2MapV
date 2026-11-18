$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$vendor = Join-Path $root 'vendor'
New-Item -ItemType Directory -Force -Path $vendor | Out-Null

$repos = @(
    @{ Name = 'imgui';   Url = 'https://github.com/ocornut/imgui.git'; Marker = 'imgui.h' },
    @{ Name = 'kiero';   Url = 'https://github.com/Rebzzel/kiero.git'; Marker = 'kiero.h' },
    @{ Name = 'MinHook'; Url = 'https://github.com/TsudaKageyu/minhook.git'; Marker = 'include/MinHook.h' }
)
foreach ($r in $repos) {
    $dest = Join-Path $vendor $r.Name
    if (-not (Test-Path (Join-Path $dest $r.Marker))) {
        Write-Host "Klone $($r.Name) ..."
        git clone --depth 1 $r.Url $dest
        if ($LASTEXITCODE -ne 0) { throw "git clone $($r.Name) fehlgeschlagen." }
    }
}

$mingwBin = 'D:\Tools\msys64\ucrt64\bin'
if (Test-Path $mingwBin) { $env:PATH = "$mingwBin;$env:PATH" }

$build = Join-Path $root 'build'
cmake -S $root -B $build -G "MinGW Makefiles" `
    -DCMAKE_CXX_COMPILER="$mingwBin/g++.exe" `
    -DCMAKE_C_COMPILER="$mingwBin/gcc.exe"
if ($LASTEXITCODE -ne 0) { throw "cmake configure fehlgeschlagen." }
cmake --build $build --parallel
if ($LASTEXITCODE -ne 0) { throw "build fehlgeschlagen." }

Write-Host ""
Write-Host "Fertig: $build\SOD2Overlay.dll  +  $build\Sod2Injector.exe"

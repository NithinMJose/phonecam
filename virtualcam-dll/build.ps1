$ErrorActionPreference = "Stop"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "Visual Studio / Build Tools not found. Install Visual Studio BuildTools 2022 with Desktop development with C++."
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw "MSVC C++ x64 build tools not found in Visual Studio installation."
}

$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
    throw "vcvars64.bat not found at $vcvars"
}

$outDir = Join-Path $PSScriptRoot "build\Release"
New-Item -ItemType Directory -Force $outDir | Out-Null

$objPath = Join-Path $outDir "PhoneCamVirtualCam.obj"
$dllPath = Join-Path $outDir "PhoneCamVirtualCam.dll"
$oldDllPath = Join-Path $outDir "PhoneCamVirtualCam.dll.old"
Remove-Item $oldDllPath -Force -ErrorAction SilentlyContinue
if (Test-Path $dllPath) {
    try {
        Remove-Item $dllPath -Force -ErrorAction Stop
    } catch {
        Move-Item $dllPath $oldDllPath -Force
    }
}
$cmd = "`"$vcvars`" && cl /nologo /O2 /MT /EHsc /std:c++20 /DUNICODE /D_UNICODE /LD `"$PSScriptRoot\PhoneCamVirtualCam.cpp`" /Fo:`"$objPath`" /Fe:`"$dllPath`" /link /DEF:`"$PSScriptRoot\PhoneCamVirtualCam.def`" mfplat.lib mf.lib mfuuid.lib ole32.lib advapi32.lib"
cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) {
    throw "C++ build failed with exit code $LASTEXITCODE"
}

& icacls.exe $dllPath /grant "*S-1-5-19:(RX)" /grant "*S-1-15-2-1:(RX)" | Out-Null

Write-Output "Built: $outDir\PhoneCamVirtualCam.dll"


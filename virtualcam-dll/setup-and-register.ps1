# Run this script in an Administrator PowerShell window to:
# 1) Install Visual Studio BuildTools 2022 (C++ + Windows 11 SDK) if missing
# 2) Build PhoneCamVirtualCam.dll (x64 Release)
# 3) Register the COM DLL with regsvr32

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Please run this script from an Administrator PowerShell window."
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$hasVc = $false
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($vsPath) { $hasVc = $true }
}

if (-not $hasVc) {
    Write-Host "Installing Visual Studio BuildTools 2022 (C++ workload + Windows 11 SDK)..." -ForegroundColor Cyan
    winget install --id Microsoft.VisualStudio.2022.BuildTools --exact --accept-package-agreements --accept-source-agreements `
        --override "--wait --passive --norestart --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
    if ($LASTEXITCODE -ne 0) {
        throw "Visual Studio BuildTools installation failed with exit code $LASTEXITCODE"
    }
}

Write-Host "Building PhoneCamVirtualCam.dll..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "build.ps1")

$dllPath = Join-Path $PSScriptRoot "build\Release\PhoneCamVirtualCam.dll"
if (-not (Test-Path $dllPath)) {
    throw "Expected DLL not found at $dllPath"
}

Write-Host "Granting FrameServer & AppContainer read/execute on DLL..." -ForegroundColor Cyan
& icacls.exe $dllPath /grant "*S-1-5-19:(RX)" /grant "*S-1-15-2-1:(RX)" | Out-Null

Write-Host "Registering COM DLL: $dllPath" -ForegroundColor Cyan
& regsvr32.exe /s $dllPath
if ($LASTEXITCODE -ne 0) {
    throw "regsvr32 failed with exit code $LASTEXITCODE"
}

Write-Host "Done! PhoneCamVirtualCam.dll is built and registered." -ForegroundColor Green

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$stagingApp = Join-Path $PSScriptRoot "staging\app"
$outputDir = Join-Path $PSScriptRoot "output"

New-Item -ItemType Directory -Force $outputDir | Out-Null
if (Test-Path $stagingApp) { Remove-Item $stagingApp -Recurse -Force }

Write-Host "[1/4] Building C++ Virtual Camera DLL..." -ForegroundColor Cyan
& (Join-Path $root "virtualcam-dll\build.ps1")

Write-Host "[2/4] Publishing self-contained Windows PhoneCam.exe (win-x64)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "windows-app\PhoneCam\PhoneCam.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $stagingApp
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[3/4] Building Android APK..." -ForegroundColor Cyan
Push-Location (Join-Path $root "android")
try {
    & .\gradlew.bat assembleDebug --no-daemon
    if ($LASTEXITCODE -ne 0) { throw "Gradle build failed" }
} finally {
    Pop-Location
}
$apkSrc = Join-Path $root "android\app\build\outputs\apk\debug\app-debug.apk"
$apkDst = Join-Path $outputDir "PhoneCam.apk"
Copy-Item $apkSrc $apkDst -Force

Write-Host "[4/4] Compiling Inno Setup installer..." -ForegroundColor Cyan
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install via: winget install JRSoftware.InnoSetup"
}

& $iscc (Join-Path $PSScriptRoot "PhoneCam.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "  Windows Installer : $(Join-Path $outputDir 'PhoneCamSetup.exe')"
Write-Host "  Android APK       : $apkDst"


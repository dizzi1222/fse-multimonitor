[CmdletBinding()]
param(
    [string]$Version = "1.1.0",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\FseMultiMonitor.csproj"
$appDir = Join-Path $root "artifacts\app"

if (-not $SkipPublish) {
    Write-Host "Publishing self-contained app to $appDir ..."
    dotnet publish $project -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:NuGetAudit=false -o $appDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup"
}

$iss = Join-Path $PSScriptRoot "FseMultiMonitor.iss"
Write-Host "Compiling installer with $iscc ..."
& $iscc $iss "/DMyAppVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

$output = Join-Path $PSScriptRoot "Output\FseMultiMonitor-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $output)) { throw "Installer not found: $output" }

Write-Host ""
Write-Host "Installer ready: $output"
Get-Item -LiteralPath $output | Select-Object FullName, Length, LastWriteTime

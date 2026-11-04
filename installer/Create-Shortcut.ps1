[CmdletBinding()]
param(
    [string]$Target = "",
    [string]$Arguments = "select",
    [string]$Name = "FSE Multi-Monitor",
    [string[]]$Destinations = @()
)

$ErrorActionPreference = "Stop"

if (-not $Target) {
    $candidates = @(
        (Join-Path $PSScriptRoot "..\publish\FseMultiMonitor.exe"),
        (Join-Path $PSScriptRoot "..\artifacts\app\FseMultiMonitor.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\FseMultiMonitor\FseMultiMonitor.exe")
    )
    $Target = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Target -or -not (Test-Path -LiteralPath $Target)) {
    throw "FseMultiMonitor.exe not found. Build it first or pass -Target."
}
$Target = (Resolve-Path -LiteralPath $Target).Path

if ($Destinations.Count -eq 0) {
    $Destinations = @(
        (Join-Path $PSScriptRoot "..\publish"),
        [Environment]::GetFolderPath("Desktop")
    )
}

$shell = New-Object -ComObject WScript.Shell
foreach ($dir in $Destinations) {
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $path = Join-Path $dir "$Name.lnk"
    $shortcut = $shell.CreateShortcut($path)
    $shortcut.TargetPath = $Target
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = Split-Path -Parent $Target
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Description = "View windows on a second display while Xbox Mode (FSE) is active"
    $shortcut.Save()
    Write-Host "Created: $path"
}

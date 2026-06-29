<#
.SYNOPSIS
  Build the Lookup plugin (Release) and copy it into Flow Launcher's plugins folder.

.EXAMPLE
  ./build.ps1
  ./build.ps1 -SkipCopy        # build only
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipCopy
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "Lookup\Lookup.csproj"

Write-Host "Building $proj ($Configuration)..." -ForegroundColor Cyan
dotnet build $proj -c $Configuration

$outDir = Join-Path $root "Lookup\bin\$Configuration"
if (-not (Test-Path $outDir)) {
    throw "Build output not found at $outDir"
}

if ($SkipCopy) {
    Write-Host "Build output: $outDir" -ForegroundColor Green
    return
}

$dest = Join-Path $env:APPDATA "FlowLauncher\Plugins\Lookup"
Write-Host "Copying to $dest..." -ForegroundColor Cyan
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item -Path (Join-Path $outDir "*") -Destination $dest -Recurse -Force

Write-Host "Installed to $dest" -ForegroundColor Green
Write-Host "Restart Flow Launcher, then type:  lu datasets" -ForegroundColor Yellow

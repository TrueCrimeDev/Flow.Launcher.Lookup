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

# The live plugin folder holds user-owned files (config.json, real datasets in data\)
# that must survive a reinstall — stash them before wiping the folder.
$stash = $null
if (Test-Path $dest) {
    $stash = Join-Path $env:TEMP ("LookupUpgrade_" + [IO.Path]::GetRandomFileName())
    New-Item -ItemType Directory -Path $stash | Out-Null
    if (Test-Path (Join-Path $dest "config.json")) {
        Copy-Item (Join-Path $dest "config.json") $stash
    }
    if (Test-Path (Join-Path $dest "data")) {
        Copy-Item (Join-Path $dest "data") (Join-Path $stash "data") -Recurse
    }
    Remove-Item $dest -Recurse -Force
}

New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item -Path (Join-Path $outDir "*") -Destination $dest -Recurse -Force

if ($stash) {
    # Restore config.json always; data files only when the new build does not ship one
    # of the same name — shipped samples stay fresh, user-added datasets survive.
    if (Test-Path (Join-Path $stash "config.json")) {
        Copy-Item (Join-Path $stash "config.json") $dest -Force
    }
    $stashData = Join-Path $stash "data"
    if (Test-Path $stashData) {
        $destData = Join-Path $dest "data"
        New-Item -ItemType Directory -Path $destData -Force | Out-Null
        Get-ChildItem $stashData -File | ForEach-Object {
            $target = Join-Path $destData $_.Name
            if (-not (Test-Path $target)) { Copy-Item $_.FullName $target }
        }
    }
    Remove-Item $stash -Recurse -Force
    Write-Host "Preserved existing config.json and user-added data\ files." -ForegroundColor Yellow
}

Write-Host "Installed to $dest" -ForegroundColor Green
Write-Host "Restart Flow Launcher, then type:  lu datasets" -ForegroundColor Yellow

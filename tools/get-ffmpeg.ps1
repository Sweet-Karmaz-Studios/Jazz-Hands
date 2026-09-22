<#
.SYNOPSIS
Downloads the pinned FFmpeg shared build into third_party/ffmpeg and verifies its SHA-256.

.DESCRIPTION
Jazz Hands links against FFmpeg 8.1 through FFmpeg.AutoGen 8.1.x. The pin below is a
dated BtbN autobuild tag, not 'latest', so a clean clone always gets the same binaries.
Docs/CODECS.md records the build and its hash. Changing the pin is a phase-level decision.

.PARAMETER Force
Re-download and overwrite an existing third_party/ffmpeg.
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Tag    = 'autobuild-2026-09-22-13-18'
$Asset  = 'ffmpeg-n8.1.3-win64-gpl-shared-8.1.zip'
$Sha256 = '5652C185366A1300C731DF2AB76A3D2C1BB15B143C5D251B65FDF4335850E6C2'

$Dest   = Join-Path $PSScriptRoot '..\third_party\ffmpeg'
$Sentinel = Join-Path $Dest 'bin\avcodec-62.dll'

if ((Test-Path $Sentinel) -and -not $Force) {
    Write-Host "FFmpeg present at $Dest"
    exit 0
}

$Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$Tag/$Asset"
$Zip = Join-Path $env:TEMP $Asset

if (-not (Test-Path $Zip)) {
    Write-Host "Downloading $Url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $Zip
}

$hash = (Get-FileHash $Zip -Algorithm SHA256).Hash
if ($hash -ne $Sha256) {
    Remove-Item $Zip -Force
    throw "SHA-256 mismatch for $Asset. Expected $Sha256, got $hash. The pin in this script and Docs/CODECS.md must agree with the asset."
}

$Extract = Join-Path $env:TEMP 'jazz-ffmpeg-extract'
if (Test-Path $Extract) { Remove-Item $Extract -Recurse -Force }
Expand-Archive $Zip -DestinationPath $Extract -Force

$inner = Get-ChildItem $Extract -Directory | Select-Object -First 1
if (-not $inner) { throw "Archive $Asset did not contain the expected top-level folder." }

if (Test-Path $Dest) { Remove-Item $Dest -Recurse -Force }
New-Item -ItemType Directory -Force $Dest | Out-Null
Copy-Item "$($inner.FullName)\*" $Dest -Recurse -Force
Remove-Item $Extract -Recurse -Force

if (-not (Test-Path $Sentinel)) { throw "Expected $Sentinel after extraction; the build layout changed." }

$ver = & (Join-Path $Dest 'bin\ffmpeg.exe') -hide_banner -version 2>&1 | Select-Object -First 1
Write-Host "FFmpeg installed to $Dest"
Write-Host $ver

<#
.SYNOPSIS
  Publishes Jazz Hands for release: the editor, jazz and jazz-mcp in one folder, the portable
  zip, and the per-user installer.

.DESCRIPTION
  Phase 34. Everything goes to artifacts\release\<version>:

    files\                       JazzHands.exe, jazz.exe, jazz-mcp.exe, jazz-plugin-host.exe,
                                 self-contained, ReadyToRun, untrimmed (WPF and the command registry are not trim safe), with
                                 ffmpeg\ (the FFmpeg DLLs and ffmpeg.exe for the fallback exporter),
                                 Assets\, LICENSE.txt, LICENSES.md, README.md
    JazzHands-<v>-win-x64.msi    the installer (WiX 5, per user; installer\JazzHands.wxs)
    JazzHands-<v>-portable-win-x64.zip   the same files and portable.txt: no Windows registration
    SHA256SUMS.txt               a hash of each

  The version is Directory.Build.props' VersionPrefix unless -Version says otherwise. Nothing is
  signed. Needs tools/get-ffmpeg.ps1 to have run. A prerelease: -Version 1.0.0-preview.1.

    powershell -ExecutionPolicy Bypass -File tools\publish.ps1
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -SkipInstaller
#>
param(
    [string] $Version = '',
    [switch] $SkipInstaller,
    [switch] $SkipZip
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if ($Version -eq '') {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ } | Select-Object -First 1)
}

# MSI versions are numeric (major.minor.build): a prerelease such as 1.0.0-preview.1 installs as
# 1.0.0, and the installer allows same-version upgrades, so the next preview or the release
# replaces it.
$msiVersion = ($Version -split '[-+]')[0]

$release = Join-Path $root "artifacts\release\$Version"
$files = Join-Path $release 'files'
if (Test-Path $release) { Remove-Item -Recurse -Force $release }
New-Item -ItemType Directory -Force $files | Out-Null

$ffmpeg = Join-Path $root 'third_party\ffmpeg'
if (-not (Test-Path (Join-Path $ffmpeg 'bin\avcodec-62.dll'))) {
    throw 'third_party\ffmpeg has no DLLs. Run tools\get-ffmpeg.ps1 first.'
}

function Publish([string] $project) {
    Write-Host "Publishing $project..."
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & dotnet publish (Join-Path $root $project) -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:PublishTrimmed=false `
        -p:DebugType=embedded -p:Version=$Version -p:PublishProfile= -o $files 2>&1 |
        Where-Object { $_ -match 'error|warn' -and $_ -notmatch ' 0 (Warning|Error)' } | Write-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code -ne 0) { throw "dotnet publish $project failed ($code)." }
}

Publish 'src\JazzHands.App\JazzHands.App.csproj'
Publish 'src\JazzHands.Cli\JazzHands.Cli.csproj'
Publish 'src\JazzHands.Mcp\JazzHands.Mcp.csproj'
Publish 'src\JazzHands.PluginHost\JazzHands.PluginHost.csproj'
Publish 'src\JazzHands.DialogHost\JazzHands.DialogHost.csproj'

# FFmpeg beside the executables, where FfmpegLoader looks first; ffmpeg.exe for the fallback exporter.
$ffmpegOut = Join-Path $files 'ffmpeg'
New-Item -ItemType Directory -Force $ffmpegOut | Out-Null
Copy-Item (Join-Path $ffmpeg 'bin\*.dll') $ffmpegOut
Copy-Item (Join-Path $ffmpeg 'bin\ffmpeg.exe') $ffmpegOut
Copy-Item (Join-Path $ffmpeg 'LICENSE.txt') (Join-Path $ffmpegOut 'LICENSE.txt')

Copy-Item (Join-Path $root 'LICENSE.txt') $files
Copy-Item (Join-Path $root 'LICENSES.md') $files
Copy-Item (Join-Path $root 'README.md') $files
Get-ChildItem $files -Filter *.pdb | Remove-Item

foreach ($exe in 'JazzHands.exe', 'jazz.exe', 'jazz-mcp.exe', 'jazz-plugin-host.exe', 'jazz-dialog.exe') {
    if (-not (Test-Path (Join-Path $files $exe))) { throw "$exe is missing from the published files." }
}

# A quick look that the published jazz runs and finds its FFmpeg, without a window.
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$banner = & (Join-Path $files 'jazz.exe') version --json 2>$null | Out-String
$ErrorActionPreference = $previous
if ($LASTEXITCODE -ne 0 -or $banner -notmatch $Version) { throw "The published jazz.exe did not report version $Version." }
Write-Host "jazz version: $Version"

$assets = @()

if (-not $SkipZip) {
    $portable = Join-Path $release 'portable'
    Copy-Item $files $portable -Recurse
    Set-Content -Encoding utf8 (Join-Path $portable 'portable.txt') @(
        'This copy of Jazz Hands is portable: it does not register with Windows.',
        'No .jazz file association, no Explorer verbs, no jazzhands: links, no start with Windows,',
        'no jump list, and jazz is not on the PATH. Settings and the cache are still kept in',
        '%APPDATA%\JazzHands and %LOCALAPPDATA%\JazzHands. Delete this file to have it register',
        'itself the next time the editor starts, or use the installer.')
    $zip = Join-Path $release "JazzHands-$Version-portable-win-x64.zip"
    Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item -Recurse -Force $portable
    $assets += $zip
    Write-Host "Portable zip: $zip"
}

if (-not $SkipInstaller) {
    $msi = Join-Path $release "JazzHands-$Version-win-x64.msi"
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & wix build (Join-Path $root 'installer\JazzHands.wxs') `
        -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext `
        -arch x64 -d "Version=$msiVersion" -d "Files=$files" -d "Assets=$(Join-Path $root 'src\JazzHands.App\Assets')" `
        -out $msi 2>&1 | Write-Host
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($code -ne 0) { throw "wix build failed ($code)." }
    Remove-Item (Join-Path $release '*.wixpdb') -ErrorAction SilentlyContinue
    $assets += $msi
    Write-Host "Installer: $msi"
}

$sums = foreach ($asset in $assets) {
    $hash = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $asset -Leaf)"
}
Set-Content -Encoding ascii (Join-Path $release 'SHA256SUMS.txt') $sums
Write-Host "Done: $release"

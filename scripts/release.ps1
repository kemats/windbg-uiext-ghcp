#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root 'artifacts/package'
$release = Join-Path $root 'artifacts/release'
$staging = Join-Path $release 'staging'
if ($Version.Contains('-')) {
    foreach ($identifier in $Version.Split('-', 2)[1].Split('.')) {
        if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers must not have leading zeros.' }
    }
}
foreach ($relative in @('WinDbgChatView.dll', 'WinDbgCopilot.Contracts.dll',
    'WinDbgCopilotChat/ui/WinDbgCopilot.UI.dll', 'WinDbgCopilotChat/core/ChatCore.dll')) {
    $path = Join-Path $package $relative
    if (!(Test-Path $path -PathType Leaf)) { throw "Missing package file: $relative. Run build.ps1 first." }
    $productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion
    if ($productVersion.Split('+', 2)[0] -cne $Version) { throw "Version mismatch in $relative. Build with -Version $Version first." }
}
$cli = Join-Path $package 'WinDbgCopilotChat/core/runtimes/win-x64/native/copilot.exe'
if (!(Test-Path $cli -PathType Leaf)) { throw 'Packaged Copilot CLI is missing.' }
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item (Join-Path $staging 'artifacts') -ItemType Directory -Force | Out-Null
Copy-Item $package (Join-Path $staging 'artifacts/package') -Recurse
New-Item (Join-Path $staging 'scripts') -ItemType Directory | Out-Null
foreach ($name in @('install.ps1', 'uninstall.ps1')) {
    Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $staging 'scripts')
}
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md', 'docs')) {
    Copy-Item (Join-Path $root $name) $staging -Recurse
}
if (Test-Path (Join-Path $root 'LICENSE') -PathType Leaf) { Copy-Item (Join-Path $root 'LICENSE') $staging }
$metadata = [ordered]@{
    version = $Version
    architecture = 'win-x64'
    copilotCliSha256 = (Get-FileHash $cli -Algorithm SHA256).Hash.ToLowerInvariant()
}
$metadata | ConvertTo-Json | Set-Content (Join-Path $staging 'release.json') -Encoding utf8NoBOM
$archive = Join-Path $release "WinDbgCopilotChat-$Version-win-x64.zip"
if (Test-Path $archive) { Remove-Item $archive -Force }
try {
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $archive)
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
} finally { Remove-Item $staging -Recurse -Force }
Write-Host "Archive: $archive"
Write-Host 'Unsigned local package only; no installation or publication was performed.'
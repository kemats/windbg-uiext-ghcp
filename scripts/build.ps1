#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$CopilotCliBinaryPath,
    [switch]$SkipWebBuild,
    [switch]$SkipWebRestore,
    [switch]$NoRestore,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant(),
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version = '0.0.0-dev'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$coreProject = Join-Path $root 'src/ChatCore/ChatCore.csproj'
$hostProject = Join-Path $root 'src/Bootstrap/Bootstrap.csproj'
$uiProject = Join-Path $root 'src/WinDbgChatView/WinDbgChatView.csproj'
$rid = "win-$Architecture"
$properties = @("-p:Configuration=$Configuration", "-p:Version=$Version")
if ($CopilotCliBinaryPath) {
    $binary = (Resolve-Path $CopilotCliBinaryPath).Path
    $properties += "-p:CopilotCliBinaryPath=$binary"
}
if (!$SkipWebBuild) {
    Push-Location (Join-Path $root 'web')
    try {
        if (!$SkipWebRestore -or !(Test-Path 'node_modules' -PathType Container)) {
            npm.cmd ci --no-audit --no-fund
        }
        npm.cmd test
        npm.cmd run build
    } finally { Pop-Location }
}
if (!(Test-Path (Join-Path $root 'web/dist/index.html'))) { throw 'Build the web assets first.' }
[string[]]$restoreArguments = if ($NoRestore) { @('--no-restore') } else { @() }
dotnet test (Join-Path $root 'tests/ChatCore.Tests/ChatCore.Tests.csproj') @properties @restoreArguments -p:CopilotSkipCliDownload=true
# Tests intentionally omit the CLI. Build distributable outputs afterwards so the SDK target restores it.
dotnet build $coreProject @properties @restoreArguments
dotnet build $hostProject @properties @restoreArguments
$corePath = (dotnet msbuild $coreProject @properties -getProperty:TargetPath).Trim()
$hostPath = (dotnet msbuild $hostProject @properties -getProperty:TargetPath).Trim()
$uiPath = (dotnet msbuild $uiProject @properties -getProperty:TargetPath).Trim()
$coreOutput = Split-Path $corePath
$hostOutput = Split-Path $hostPath
$uiOutput = Split-Path $uiPath
$package = Join-Path $root 'artifacts/package'
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
$payload = Join-Path $package 'WinDbgCopilotChat'
New-Item (Join-Path $payload 'core') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $coreOutput '*') (Join-Path $payload 'core') -Recurse
Copy-Item (Join-Path $root 'web/dist') (Join-Path $payload 'web') -Recurse
foreach ($name in @('WinDbgChatView.dll', 'WinDbgCopilot.Contracts.dll')) {
    Copy-Item (Join-Path $hostOutput $name) $package
}
$ui = Join-Path $payload 'ui'
New-Item $ui -ItemType Directory -Force | Out-Null
foreach ($name in @('WinDbgCopilot.UI.dll', 'WinDbgCopilot.UI.deps.json', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll')) {
    Copy-Item (Join-Path $uiOutput $name) $ui
}
if ($Configuration -eq 'Debug') {
    foreach ($name in @('WinDbgChatView.pdb', 'WinDbgCopilot.Contracts.pdb')) {
        Copy-Item (Join-Path $hostOutput $name) $package
    }
    Copy-Item (Join-Path $uiOutput 'WinDbgCopilot.UI.pdb') $ui
}
$native = Join-Path $ui "runtimes/$rid/native"
New-Item $native -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $uiOutput "runtimes/$rid/native/WebView2Loader.dll") $native
Get-ChildItem $package -File -Filter '*.dll' | ForEach-Object {
    if ($_.Name -notin @('WinDbgChatView.dll', 'WinDbgCopilot.Contracts.dll')) { throw "Unexpected root assembly: $($_.Name)" }
    [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName) | Out-Null
}
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $payload
Copy-Item (Join-Path $root 'LICENSE') $payload
$licenses = Join-Path $payload 'third-party-licenses'
node (Join-Path $PSScriptRoot 'collect-web-licenses.mjs') $licenses
Get-ChildItem (Join-Path $root 'third-party-licenses') -File | Copy-Item -Destination $licenses
$cli = Join-Path $payload "core/runtimes/$rid/native/copilot.exe"
if (!(Test-Path $cli)) { throw "Packaged Copilot CLI for $rid is missing." }
if (Get-ChildItem $package -Recurse -File | Where-Object { $_.Name -like 'DbgX*.dll' -or $_.Name -in @('Fluent.dll', 'ControlzEx.dll') }) {
    throw 'Host-provided DbgX, Fluent and ControlzEx assemblies must not be packaged.'
}
foreach ($license in @('LICENSE', 'third-party-licenses/GitHub-Copilot-CLI-LICENSE.md',
    'third-party-licenses/Microsoft-WebView2-LICENSE.txt', 'third-party-licenses/Microsoft-WebView2-NOTICE.txt',
    'third-party-licenses/DotNet-MIT-LICENSES.txt', 'third-party-licenses/README.md')) {
    if (!(Test-Path (Join-Path $payload $license) -PathType Leaf) -or (Get-Item (Join-Path $payload $license)).Length -eq 0) {
        throw "Required license file is missing: $license"
    }
}
Write-Host "Package: $package"
Write-Host 'No files have been installed into WinDbg.'
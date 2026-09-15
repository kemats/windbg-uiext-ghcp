#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'dbg/UIExtensions'))
$ErrorActionPreference = 'Stop'
if (Get-Process -Name WinDbgX,DbgX.Shell -ErrorAction SilentlyContinue) { throw 'Close WinDbg before installing.' }
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/package'
if (!(Test-Path (Join-Path $source 'WinDbgChatView.dll'))) { throw 'Run scripts/build.ps1 first.' }
if (!(Test-Path (Join-Path $source 'WinDbgCopilotChat/ui/WinDbgCopilot.UI.dll'))) { throw 'Rebuild the isolated UI package first.' }
Get-ChildItem $source -File -Filter '*.dll' | ForEach-Object {
    if ($_.Name -notin @('WinDbgChatView.dll', 'WinDbgCopilot.Contracts.dll')) { throw "Unexpected root assembly: $($_.Name). Rebuild first." }
}
$runtimeRoot = Join-Path $source 'WinDbgCopilotChat/core/runtimes'
$packageRids = @(Get-ChildItem $runtimeRoot -Directory -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -in @('win-x64', 'win-arm64') -and (Test-Path (Join-Path $_.FullName 'native/copilot.exe') -PathType Leaf)
} | Select-Object -ExpandProperty Name)
if ($packageRids.Count -eq 1) {
    $osRid = "win-$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())"
    if ($packageRids[0] -ne $osRid) {
        Write-Warning "Package architecture $($packageRids[0]) does not match OS architecture $osRid. The extension may not work correctly."
    }
}
if ($PSCmdlet.ShouldProcess($Destination, 'Install WinDbg Copilot Chat')) {
    New-Item $Destination -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $source '*') $Destination -Recurse -Force
    Write-Host 'Installed. Restart WinDbg and open Copilot > Chat.'
}
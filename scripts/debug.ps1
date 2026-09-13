#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$SkipWebBuild,
    [switch]$RestoreWebDependencies,
    [switch]$NoRestore,
    [ValidateSet('x64', 'arm64')]
    [string]$Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (Get-Process -Name WinDbg,WinDbgX,DbgX.Shell -ErrorAction SilentlyContinue) {
    throw 'Close WinDbg before F5, or use the attach-only debug configuration.'
}
$root = Split-Path $PSScriptRoot -Parent
if ($PSCmdlet.ShouldProcess('WinDbg Copilot Chat', 'Build Debug, install the extension, and launch WinDbgX.exe')) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration Debug -Architecture $Architecture -SkipWebBuild:$SkipWebBuild -SkipWebRestore:(!$RestoreWebDependencies) -NoRestore:$NoRestore
    & (Join-Path $PSScriptRoot 'install.ps1')
    Start-Process -FilePath 'WinDbgX.exe' -WorkingDirectory $root
    Write-Host 'WinDbg launched. VS Code will attach to DbgX.Shell.exe. Open Copilot > Chat after attachment.'
}
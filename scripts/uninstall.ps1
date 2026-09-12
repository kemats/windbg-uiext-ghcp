[CmdletBinding(SupportsShouldProcess)]
param([string]$Destination = (Join-Path $env:LOCALAPPDATA 'dbg/UIExtensions'))
$ErrorActionPreference = 'Stop'
if (Get-Process -Name WinDbgX,DbgX.Shell -ErrorAction SilentlyContinue) { throw 'Close WinDbg before uninstalling.' }
foreach ($name in @('WinDbgChatView.dll', 'WinDbgCopilot.Contracts.dll', 'WinDbgCopilotChat')) {
    $path = Join-Path $Destination $name
    if ((Test-Path $path) -and $PSCmdlet.ShouldProcess($path, 'Remove WinDbg Copilot Chat file')) {
        Remove-Item $path -Recurse -Force
    }
}
Write-Host 'Unrelated shared binaries and local Copilot session data were retained.'
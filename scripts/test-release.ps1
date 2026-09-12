#requires -Version 7.4
[CmdletBinding()]
param([string]$Version = '0.0.0-dev')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$releaseScript = Join-Path $PSScriptRoot 'release.ps1'
foreach ($invalid in @('../escape', '01.2.3', '1.2', '1.2.3-01')) {
    $rejected = $false
    try { & $releaseScript -Version $invalid } catch { $rejected = $true }
    if (!$rejected) { throw "Invalid version accepted: $invalid" }
}
$mismatch = $false
try { & $releaseScript -Version '65534.65534.65534' } catch {
    if ($_.Exception.Message -notlike 'Version mismatch*') { throw }
    $mismatch = $true
}
if (!$mismatch) { throw 'Mismatched assembly version was accepted.' }
$archive = Join-Path $root "artifacts/release/WinDbgCopilotChat-$Version-win-x64.zip"
$checksum = (Get-Content "$archive.sha256" -Raw).Trim().Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
if ($checksum.Count -ne 2 -or $checksum[1] -cne [System.IO.Path]::GetFileName($archive) -or
    $checksum[0] -ine (Get-FileHash $archive -Algorithm SHA256).Hash) { throw 'Invalid archive checksum.' }
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($required in @('scripts/install.ps1', 'scripts/uninstall.ps1', 'README.md', 'docs/design.md',
        'THIRD-PARTY-NOTICES.md', 'release.json', 'artifacts/package/WinDbgChatView.dll',
        'artifacts/package/WinDbgCopilot.Contracts.dll', 'artifacts/package/WinDbgCopilotChat/web/index.html',
        'artifacts/package/WinDbgCopilotChat/ui/WinDbgCopilot.UI.dll',
        'artifacts/package/WinDbgCopilotChat/core/runtimes/win-x64/native/copilot.exe',
        'artifacts/package/WinDbgCopilotChat/LICENSE',
        'artifacts/package/WinDbgCopilotChat/third-party-licenses/GitHub-Copilot-CLI-LICENSE.md',
        'artifacts/package/WinDbgCopilotChat/third-party-licenses/Microsoft-WebView2-LICENSE.txt',
        'artifacts/package/WinDbgCopilotChat/third-party-licenses/Microsoft-WebView2-NOTICE.txt',
        'artifacts/package/WinDbgCopilotChat/third-party-licenses/DotNet-MIT-LICENSES.txt',
        'artifacts/package/WinDbgCopilotChat/third-party-licenses/README.md')) {
        if ($required -cnotin $entries) { throw "Missing archive entry: $required" }
    }
    foreach ($entry in $entries) {
        if ($entry -match '(^|/)\.\.(/|$)|(^|/)(DbgX[^/]*|Fluent|ControlzEx)\.dll$|(^|/)(\.git|node_modules|chat-history)/') {
            throw "Unexpected archive entry: $entry"
        }
        if ($entry -match '^artifacts/package/[^/]+\.dll$' -and
            $entry -notin @('artifacts/package/WinDbgChatView.dll', 'artifacts/package/WinDbgCopilot.Contracts.dll')) {
            throw "Unexpected root DLL: $entry"
        }
    }
    $metadataEntry = $zip.Entries | Where-Object FullName -eq 'release.json'
    $reader = [System.IO.StreamReader]::new($metadataEntry.Open())
    try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($metadata.version -cne $Version -or $metadata.architecture -ne 'win-x64') { throw 'Invalid release metadata.' }
} finally { $zip.Dispose() }
Write-Host 'Release checks passed: invalid versions, assembly mismatch, checksum, metadata and archive layout.'
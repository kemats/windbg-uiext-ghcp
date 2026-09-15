# Releases

## One-time repository setup

1. Select and commit the rights holder's source license as `LICENSE`. No license is inferred by this sample.
2. Complete the redistribution review and replace the dependency inventory with all required notices/license texts, including bundled frontend libraries, fonts, WebView2, SDK and CLI. Ensure required notices are copied into the package by `scripts/build.ps1`.
3. Create a GitHub environment named `release`. Configure required reviewers and deployment restrictions for version tags. After licensing and compatibility review, set its environment variable `PUBLIC_RELEASE_APPROVED` to `true`. This is a human approval gate, not a license scanner; reset it when the review is no longer valid.
4. Protect `main` with the Build and Release workflow's build job. Protect creation/deletion of `v*` tags with a repository ruleset and limit release-environment deployment to authorized maintainers. If the default branch differs, update the workflow's branch filter.

The workflow deliberately fails tag publication without a nonempty source license or the approval variable. Review each release through the protected environment, including the [real-host checklist](validation.md). GitHub environment protection availability depends on repository visibility and plan; do not assume an automatically created environment is protected.

## Candidate verification

Use PowerShell 7.4+, .NET 10 SDK, Node.js 24 and Microsoft Edge on Windows x64 or ARM64. Run the following on each architecture being released; `-Architecture` defaults to the PowerShell process architecture:

```powershell
dotnet restore windbg-uiext-ghcp.slnx --locked-mode
./scripts/build.ps1 -Version 1.2.3 -NoRestore
npm --prefix web run lint
npm --prefix web run test:e2e
./scripts/release.ps1 -Version 1.2.3
./scripts/test-release.ps1 -Version 1.2.3
```

Playwright starts and stops a preview server automatically. Locally it can reuse an existing server on port 4173; ensure that server serves the current production build. CI never reuses one. Stop preview/dev servers before `build.ps1` reinstalls frontend dependencies on Windows.

Commit all npm/NuGet lockfiles with dependency changes. GitHub-hosted CI explicitly restores from nuget.org in locked mode and uses `npm ci`; it needs no corporate feeds or Copilot credentials. Local builds retain the developer's configured feeds. On managed devices, use approved feeds through local configuration or the restore command's `--source`/`--configfile`, then build with `-NoRestore`. Do not disable TLS validation or bypass organization restrictions, and do not commit internal endpoints or credentials.

The CLI is downloaded separately using the version declared by the SDK package, not an unpinned latest npm package. NuGet's `--no-restore` does not disable this download. For managed environments, supply `-CopilotCliBinaryPath` for a CLI obtained through an approved channel. Validate its actual version/compatibility during candidate review; the previously observed package/binary version discrepancy is not resolved by CI passing.

`build.ps1 -Version` stamps the extension assemblies. `release.ps1` rejects a version different from those assemblies and creates an unsigned architecture-specific Windows ZIP plus a SHA-256 sidecar in `artifacts/release`. The ZIP preserves `scripts/` and `artifacts/package/`, so the existing installer works after extraction. `release.json` records the package version, architecture and bundled CLI hash. Hashes detect corruption, not publisher identity; this workflow does not sign assemblies or provide a reproducible-build guarantee.

## Publish by tag

After merging and completing candidate review, create and push a version tag on the reviewed commit:

```powershell
git tag -a v1.2.3 -m "WinDbg Copilot Chat 1.2.3"
git push origin v1.2.3
```

Use `v1.2.3-rc.1` for a prerelease. Tags must start with lowercase `v`, contain three numeric components without leading zeros and may include a prerelease suffix. Numeric prerelease identifiers cannot have leading zeros. Build metadata (`+...`) is not accepted. Keep assembly version components within .NET version limits.

Every tag rebuilds and tests that exact commit on Windows x64 and ARM64. Only after native/frontend tests, lint, Edge tests and archive validation pass are both architecture-specific assets handed to the release job. It verifies SHA-256 and uses the short-lived `GITHUB_TOKEN` with `contents: write` to create a release for the existing tag. Other jobs have read-only repository permission; PRs never publish. No PAT, Copilot login, actual debugger, installation, signing service or Azure deployment is involved.

Prerelease tags create GitHub prereleases and are not marked latest. Normal version tags create ordinary releases with generated notes. Inspect release notes for changes and compatibility caveats. Existing releases are not overwritten: to correct a published binary, issue a new patch version. A failed unpublished tag run can be rerun after fixing environment approval, but changing code requires a new reviewed commit/tag.

## Download and install

Download both the ZIP and its `.sha256` sidecar from the same release. Verify the ZIP hash before removing any security metadata:

```powershell
Get-FileHash ./WinDbgCopilotChat-1.2.3-win-x64.zip -Algorithm SHA256
Get-Content ./WinDbgCopilotChat-1.2.3-win-x64.zip.sha256
```

Compare the complete hash values. If the ZIP is trusted, check its Windows **Properties** before extraction. When the **Unblock** checkbox appears, select it and apply the change. The equivalent PowerShell command is:

```powershell
Unblock-File ./WinDbgCopilotChat-1.2.3-win-x64.zip
```

This removes the ZIP's `Zone.Identifier` alternate data stream before extraction and prevents the Internet zone identifier from being propagated to its contents. Do not unblock a file merely to bypass an unexpected warning; first confirm that it came from the expected GitHub release and that its SHA-256 matches. If the ZIP was already extracted, remove the extracted directory, unblock the original ZIP, and extract it again rather than recursively unblocking unknown files.

Extract the ZIP into a new directory, close WinDbg and run:

```powershell
pwsh -File ./scripts/install.ps1 -WhatIf
pwsh -File ./scripts/install.ps1
```

PowerShell 7.4+ is required; Windows PowerShell 5.1 is not supported. Do not flatten `artifacts/package` or copy the archive root into `UIExtensions`. The installer only copies package payloads. Keep the extracted installer/uninstaller for maintenance. Local history and credentials are not deleted by uninstall.
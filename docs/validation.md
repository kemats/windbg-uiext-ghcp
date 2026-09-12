# Validation

Automated tests exercise this extension and its package boundaries. They do not establish compatibility with every WinDbg version or replace an authenticated, real-host smoke test. Ordinary tests must not authenticate, submit prompts to Copilot, open debug targets, or modify the WinDbg installation.

## Automated checks

Run on Windows x64 or ARM64 with the prerequisites in the [README](../README.md). The default package architecture matches the PowerShell process; use `-Architecture x64` or `-Architecture arm64` explicitly when needed.

```powershell
./scripts/build.ps1
npm --prefix web run lint
```

The build installs locked frontend dependencies, runs frontend unit tests, builds the web assets, builds the native projects, runs native tests, and stages the package without installing it. To reuse already-built web assets, use `-SkipWebBuild`. Stop existing development servers before `npm ci` on Windows; they can hold native dependency files open.

For browser tests, Playwright serves the production web build automatically:

```powershell
npm --prefix web run test:e2e
```

Release packaging is checked with `scripts/release.ps1` and `scripts/test-release.ps1`, using the same version as the build. See [Releases](releasing.md) for CI and tag publication.

Playwright uses installed Microsoft Edge at 320, 420 and 1280 pixel widths. Browser tests use synthetic bridge responses, attachments and speech APIs, not real authentication or debugger sessions.

| Area | Automated coverage | Remaining boundary |
| --- | --- | --- |
| Host integration | MEF export metadata, titled tool-window construction, assembly isolation | Actual docking, layout restore, host assembly versions |
| Debugger operations | Serialized execution, delayed completion, full output, current stopped-target check and cancellation | Real command behavior after active-target changes and interleaved manual output |
| Permissions | Execution and sharing approvals, denial, mode changes and approval-free metadata | SDK/CLI tool restrictions in an authenticated session |
| Sessions | Local history lifecycle, title precedence, SDK event identity, automatic startup connection and retry | Real SDK resume, generated titles, authentication and model availability |
| Authentication console | Simulated CLI stdout/stderr, argument handling, success/failure exit codes | Device authorization, browser selection, clipboard availability |
| Frontend | Model/history controls, pending states, IME, attachments, safe Markdown/Mermaid, responsive layout | Native WebView2 clipboard, focus, DPI and accessibility |
| Usage and speech | Missing-value handling, pricing conversion, local-voice selection and playback identity | Provider billing data and installed voice behavior |
| Packaging | Root assembly allowlist and exclusion of host binaries | Redistribution permissions and compatibility smoke test |

Test results belong to each CI run, not a cumulative pass-count claim in source control. Investigate intermittent failures rather than masking them with unconditional retries. In particular, assertions about brief streaming states should use controlled events instead of depending on wall-clock timing.

## Real-host release checklist

Use a disposable target and non-sensitive prompts/attachments. Record the candidate version, WinDbg version, Windows version, CLI version and results in the release review. Do not attach credentials, device codes, customer traces or unreviewed chat history.

- Install with WinDbg closed; verify startup, the Copilot tab, docking/floating, layout restore, close/reopen, theme, DPI, focus and Japanese IME.
- Open the pane with a saved login: one automatic connection, a fresh Ask session and the previous model. Test invalid login, manual retry, account switching and CLI cancellation.
- Verify device-code instructions are visible, browser authorization completes and no additional InPrivate window is launched. A clipboard-copy warning does not alone establish authentication failure.
- Confirm only the two debugger tools are available. Target metadata is automatic; Ask mode requires command execution and output-sharing consent. Test denials and changes between Ask and Approve all.
- Run a non-destructive command; verify output completeness, one activity per invocation and correct completion timing. Switch targets during approval and execution, and confirm WinDbg reports any command failure without cancelling the whole chat turn. Cancellation must not be described as an engine interrupt.
- Check a clicked command link executes only on the current target, with output in WinDbg rather than a new AI message. Rendering, hovering and opening history must not execute links.
- Test SDK history resume, rename, deletion and model switching. Old transcript content must not be treated as evidence about a newly loaded target.
- Test file selection, drop and paste, attachment rejection/retry and preview. Confirm real provider receipt only with explicitly approved test data.
- Verify WebView navigation/resource restrictions and error recovery. Check local read-aloud start/stop and unavailable-voice behavior; API-reported locality is not an independent privacy certification.
- Compare reported usage/model information with the provider. Missing costs must remain unknown or partial, not zero.
- Review source licensing, dependency notices and redistribution terms before publishing binaries. DbgX, Fluent.Ribbon and ControlzEx must be supplied by the host, not redistributed by this package.

## Publication boundary

Use only public package contracts, public documentation and reproducible extension tests as design evidence. Do not retain private host implementation details, machine-specific research paths, retired planning prompts or live authentication output. A publicly downloadable SDK does not imply a stable or supported WinDbg UI-extension contract; re-run the real-host checklist when changing the target SDK or host version.
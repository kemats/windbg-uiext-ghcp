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
| Tools and MCP | JSON/JSONC configuration, variables, invalid settings, session catalog/host exclusions, SDK-provided shell names, stale selection removal, selection hook denial/cancellation, picker groups/search/connections, long-description expansion and busy state | Real SDK live filtering/initialized metadata, model-specific built-ins, MCP canonical names, transport/authentication, reconnect and process cleanup |
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
- With fresh tool preferences, confirm only the two debugger tools are selected. Target metadata is automatic; Ask mode requires command execution and output-sharing consent. Test denials and changes between Ask and Approve all.
- In the tool picker, enable an SDK built-in, run a harmless approved call, then disable it and verify it is unavailable on the next turn in the same conversation. Test denying an SDK permission request, cancellation and Approve all. Confirm no unselected tool executes.
- Verify the unsupported `skill`, `ask_user`, `agent` and `task` built-ins are absent, including when older preferences selected them. Confirm shell names match initialized session metadata, not the generic server catalog or an OS-based guess. With disposable files, verify initialized read/search/edit and shell tools; switch models and confirm the catalog and active allowlist update. Metadata presence is not proof that shell executables, network or permissions are available. Check long descriptions remain compact and keyboard-accessible in narrow panes.
- Load trusted stdio and HTTP MCP test configurations. Loading alone must not start new servers. Test native trust denial, connection, individual tool selection, grouped selection and disconnect. Verify status for failed/needs-auth servers without exposing credentials. Legacy SSE and VS Code input/secret storage are not implemented.
- For OAuth HTTP MCP, verify Sign in is unavailable for disabled/stdio servers and during active turns. Deny each native prompt, retry, complete browser sign-in, and verify metadata refreshes automatically without selecting new tools. Complete sign-in during a response and verify discovery waits until it finishes. Check manual Reload tools recovery and selected-tool approval behavior. Test browser cancellation, expired/revoked tokens, tenant consent failures, and reconnect after restarting WinDbg. Confirm tokens stay SDK-owned in the OS keychain, not in bridge messages/configuration. A local mock MCP probe verified connected-event discovery and allowlist restoration without tool execution. The Azure DevOps probe verified only needs-auth and the Microsoft authorization URL without opening a browser or accessing organization data; full sign-in and refresh remain manual checks. Disconnect must not be described as sign-out or revocation.
- Reload an edited MCP configuration in both empty and submitted conversations. Verify session ID, transcript, model and approval mode are retained; tool selection survives new/resumed chats and restart. Test malformed/missing files, connection failure and successful reload recovery. Verify disabled servers stop and configuration changes are rejected during a response. Do not use sensitive tools until live filtering and cleanup are verified.
- With a staged Windows package and .NET 10, run `dotnet run --file tests/McpStartupProbe.cs --configuration Debug -- delayed` from the repository root. Repeat with `stable`, `empty`, and `denied`. This local HTTP MCP probe uses the packaged CLI with isolated temporary storage and no user authentication/model calls/tool execution. It checks initial empty-cache recovery to 150 unselected tools, ordinary startup, genuinely empty lists, HTTP 403 diagnostics, and allowlist restoration. It is separate from the unit test project because it requires the staged CLI.
- On an enabled HTTP MCP server showing `connected` with no tools, verify **Sign in again** is available. Deny the native confirmation and confirm no credentials change; accept it and choose the intended browser account. Verify completion refreshes the catalog. Test disabled/stdio servers, active responses, initiation failure, and browser cancellation. The SDK clears only the target server's cached OAuth token, not browser cookies or configured headers; persistent tokens may be shared with other sessions. Real Azure DevOps consent, wrong-account replacement and authenticated tools remain manual validation requirements.
- SDK 1.0.13 / runtime 1.0.83: run the MCP probes in Release or Debug to verify startup protocol compatibility, not only compilation. Initial Sign in and Switch account must use the installed official CLI, not the SDK runtime alias. Verify CLI present/missing, installation denial preserving any current chat, missing winget, installer failure/cancellation, successful per-user installation and subsequent discovery, then browser sign-in and reconnect. Initial sign-in must work without a session after automatic connection fails; after authentication the menu should show Switch account. Installer arguments/discovery are unit-tested and initial UI cancellation/retry/success are mocked in Playwright; actual installation, agreement prompts and browser authorization require manual validation.
- Run a non-destructive command; verify output completeness, one activity per invocation and correct completion timing. Switch targets during approval and execution, and confirm WinDbg reports any command failure without cancelling the whole chat turn. Cancellation must not be described as an engine interrupt.
- Launch WinDbg normally and as administrator. Confirm the administrator warning appears only for the elevated process, can be dismissed, and stays dismissed after starting a new chat in the same pane.
- Check a clicked command link executes only on the current target, with output in WinDbg rather than a new AI message. Rendering, hovering and opening history must not execute links.
- Test SDK history resume, rename, deletion and model switching. Old transcript content must not be treated as evidence about a newly loaded target.
- Test file selection, drop and paste, attachment rejection/retry and preview. Confirm real provider receipt only with explicitly approved test data.
- Verify WebView navigation/resource restrictions and error recovery. Check local read-aloud start/stop and unavailable-voice behavior; API-reported locality is not an independent privacy certification.
- Compare reported usage/model information with the provider. Missing costs must remain unknown or partial, not zero.
- Review source licensing, dependency notices and redistribution terms before publishing binaries. DbgX, Fluent.Ribbon and ControlzEx must be supplied by the host, not redistributed by this package.

## Publication boundary

Use only public package contracts, public documentation and reproducible extension tests as design evidence. Do not retain private host implementation details, machine-specific research paths, retired planning prompts or live authentication output. A publicly downloadable SDK does not imply a stable or supported WinDbg UI-extension contract; re-run the real-host checklist when changing the target SDK or host version.
# WinDbg Copilot Chat

A [WinDbg](http://aka.ms/windbg) UI extension for [GitHub Copilot](https://github.com/features/copilot). You can interact with Copilot directly within WinDbg through this extension, and it provides autonomous troubleshooting capabilities for multiple debug targets.

This extension is based on a minimal WPF/MEF bootstrap, with a bundled [React UI](https://react.dev/) running in an isolated [WebView2](http://aka.ms/webview2) control, and a separately isolated C# core that integrates with the [GitHub Copilot SDK](https://github.com/github/copilot-sdk).

> [!IMPORTANT]
> This project is a personal learning experiment involving creating UI extensions for DbgX and integrating the GitHub Copilot SDK, and it is neither a supported product nor an official implementation sample for either SDK. Also, WinDbg's UI extension APIs are not fully documented, and very little information is found in the XML documentation included with the [DbgX NuGet package](https://www.nuget.org/packages/Microsoft.Debugging.Platform.DbgX/) and https://github.com/kevingosse/windbg-extensions. Use this extension at your own risk, and review source code if you have any concerns. If WinDbg becomes unstable or not launching after installing this extension or updating WinDbg, remove the extension and restart WinDbg.

The project's original source is licensed under the [MIT License](LICENSE). Dependencies remain under their own terms; see [Third-party dependencies](THIRD-PARTY-NOTICES.md). Binary packages include the applicable license texts under `WinDbgCopilotChat/third-party-licenses`.

## Screenshot

![WinDbg UI Extension for GitHub Copilot Chat](./docs/images/screenshot.png)

## Runtime requirements

- WinDbg x64 or ARM64 (version 1.2606.22001.0 or later).
- WebView2 Evergreen Runtime installed.
- GitHub Copilot access and CLI authentication. Credentials stay in the CLI, never in the JavaScript bridge.

## Build requirements

- PowerShell 7.4+.
- .NET 10 SDK.
- Node.js 22.12+ or 24+.

## Install a release

Download the Windows ZIP matching WinDbg's architecture and its SHA-256 sidecar from GitHub Releases, then verify the hash. Before extracting the trusted ZIP, open its Windows **Properties** and select **Unblock** if that option appears; alternatively run `Unblock-File` on the ZIP. This removes its Internet zone identifier before extraction so it is not propagated to every extracted file. Then extract it, close WinDbg, and run `./scripts/install.ps1 -WhatIf` followed by `./scripts/install.ps1` from the extracted directory. Do not flatten the archive. Release binaries are unsigned; review [release verification and installation](docs/releasing.md).

## Build and install

```powershell
./scripts/build.ps1
./scripts/install.ps1 -WhatIf
./scripts/install.ps1
```

Close WinDbg before installation. Open **Copilot > Chat** after restarting. The installer writes only this extension's two root assemblies and its `WinDbgCopilotChat` directory; it does not move or remove shared files owned by other extensions. Uninstall with `./scripts/uninstall.ps1`; unrelated shared files and local session data are retained.

Only `WinDbgChatView.dll` and `WinDbgCopilot.Contracts.dll` belong at the package root. `WinDbgCopilotChat/ui` contains the UI, private WebView2 assemblies and native loader; `core` and `web` are sibling folders. Do not flatten these folders or copy the entire `artifacts` directory. This layout isolates extension dependencies from host-provided assemblies. See [Design](docs/design.md) for the boundaries and regression-test rationale.

The package is written to `artifacts/package`. DbgX/native debugger binaries are not included. If MSBuild's CLI download fails, retrieve the SDK's required package through npm with normal TLS validation:

```powershell
$cliVersion = (dotnet msbuild ./src/ChatCore/ChatCore.csproj -getProperty:CopilotCliVersion).Trim()
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$cliPackage = "@github/copilot-win32-$architecture"
npm install --prefix artifacts/cli --ignore-scripts --no-audit --no-fund "$cliPackage@$cliVersion"
./scripts/build.ps1 -CopilotCliBinaryPath "./artifacts/cli/node_modules/$cliPackage/copilot.exe"
```

Stop any Vite preview/dev server before building: `npm ci` replaces Windows native modules that a running server can lock. For host-only changes, `-SkipWebBuild` reuses existing `web/dist` assets. Verify that the downloaded CLI is compatible with the referenced SDK; package metadata and the executable's reported version may not always be identical.

On organization-managed devices, use the organization's approved NuGet/npm feeds. The repository does not override local feed policy. After configuring your approved NuGet source, run `dotnet restore windbg-uiext-ghcp.slnx --locked-mode`, then `./scripts/build.ps1 -NoRestore`. An explicit `--source` or `--configfile` can be supplied to the restore command without committing organization-specific endpoints or credentials. `-NoRestore` skips only NuGet restoration; frontend installation and the SDK's separate CLI download still follow their own configuration. Supply `-CopilotCliBinaryPath` for a CLI acquired through an approved channel.

## Authentication

The extension uses GitHub Copilot SDK **1.0.13** with bundled runtime **1.0.83**. The SDK runtime is not the interactive Copilot CLI. Both initial sign-in and account switching use an installed official Copilot CLI. If it is missing, either action offers a confirmed per-user installation through `winget install --id GitHub.Copilot --exact --source winget --scope user`. Windows App Installer provides winget; managed devices can use their approved CLI installation instead. Installation agreements remain interactive. Declining installation preserves any current chat, and sign-in can be retried. Restart WinDbg if the newly installed CLI is not yet discoverable.

Opening the chat pane automatically attempts one connection using saved authentication. No manual Connect click is needed when that login is valid. If no usable login exists, open the GitHub account menu and choose **Sign in**; an existing session is not required. The menu shows **Switch account** when authenticated. A failed connection leaves Connect available for retry; it does not repeatedly reconnect, install software, or launch browser authorization automatically. A successful connection starts a fresh session with the previous model preference.

If the CLI reports a clipboard-copy warning, enter the displayed device code manually. That warning alone does not establish authentication failure. The extension preserves CLI instructions and diagnostics; it does not read clipboard contents or capture authentication output.

The extension uses `%LOCALAPPDATA%\WinDbgCopilotChat\runtime` as its isolated Copilot home/config directory. Select the GitHub account button in the chat header, then **Sign in** or **Switch account**. After confirmation and locating/installing the official CLI, any current chat is saved and disconnected, and a Windows PowerShell console runs the installed CLI's `login --device-code`. CLI output and diagnostics are forwarded through `Out-Host` so authorization instructions are displayed by the console host rather than relying on WinDbg's inherited standard-output handles. Browser authorization is handled by the CLI; the extension does not open an additional browser or InPrivate window.

Use the device code displayed in the PowerShell sign-in window when GitHub requests it. Follow the CLI's browser sign-in instructions and choose **Use a different account** on GitHub's account-selection page to sign in with another account. Device codes and tokens stay outside the chat bridge. Closing the browser alone does not cancel the CLI: close/cancel the sign-in console to abort the pending login. Automated console tests use a simulated CLI; end-to-end authorization requires a manual check.

After authorization, the extension reconnects, reloads account/models and starts a fresh chat in Ask every time mode. Failed/cancelled CLI sign-in leaves Connect and Sign in available for retry. Existing local history is retained, not automatically submitted to the new account. The history directory is shared across accounts on this Windows profile; switching accounts is not a history-isolation boundary. Actual account replacement remains a manual check.

Do not paste tokens into chat. Sign-in and SDK child processes ignore inherited `COPILOT_GITHUB_TOKEN`, `GH_TOKEN` and `GITHUB_TOKEN` overrides so they use the selected CLI login. No tokens cross the JavaScript bridge. Credential deletion is not provided. Real browser authorization and account replacement remain a manual check.

## Chat and session information

User messages are right-aligned and assistant responses are left-aligned without You/Copilot labels. Hover or keyboard-focus a response to reveal its turn start time, reported models and credits, alongside copy and read/stop controls. Touch devices show these controls without hover. Empty tool-only responses are hidden.

The compact header shows the GitHub login returned by the same SDK client used for chat, session history and new chat, without a duplicate window title or Ready label. Model and approval controls sit inside the composer. Drag the input's upper separator, or focus it and press Up/Down, to resize the input; the height is retained locally. Error and automatic-approval banners can be dismissed without changing approval mode. The information icon below the lower-right corner opens Session Info upward, showing session identity and reported cost/context usage. Connection/progress/cancellation states remain visible when relevant. Session Info does not invent separate tool-result token counts or provide a compaction command.

The current session name shares the first header row with the account, history and new-chat controls. History and new-chat actions remain right-aligned even before connecting. Narrow panes show the account as an icon with its login in the tooltip, leaving room for the title. Use the title's pencil icon to rename it, Enter/check to save, or Escape/X to cancel. SDK `session.title_changed` events supply generated names when emitted; before that, the first user prompt (or attachment name) is used as a fallback. Manual names are retained in local history and take precedence over later generated titles, including after resume. No extra title-generation prompt is sent by the extension.

Credits are accumulated from `assistant.usage` events using `CopilotUsage.TotalNanoAiu / 1_000_000_000`, matching [VS Code's credit conversion](https://github.com/microsoft/vscode/blob/main/extensions/copilot/src/platform/chat/common/chatQuotaServiceImpl.ts). The SDK's model cost multiplier is not a credit amount. Missing/negative costs display as **Not reported**; known subtotals with missing calls display **partial**. These are reported session costs, not an account billing statement. Compaction costs are not included; usage arriving after a turn closes contributes only to the session subtotal. Live telemetry delivery and attribution still require real-host verification. New chat resets the session metrics, not the account identity.

## Models and attachments

The searchable model picker switches the **current session**, using the SDK's `SetModelAsync`: history, session ID and approval mode are retained, and the selected model takes effect for the next message. Switching is disabled during a response. New chat inherits the selected model; restored sessions retain their saved model, and otherwise `auto` is explicitly selected. The switch can reset the prompt cache and increase cost. If switching fails, the picker continues to show the last reported model.

Hover a model or use the search field's arrow keys to view its details. Enter or click selects it. Details include SDK-reported token prices, context/prompt limits, image support and available/default thinking-effort levels. Thinking effort is informational, not configurable in this UI. Prices use `TokenPrices.Price / BatchSize * 1_000_000` to show credits per million tokens for the standard tier; missing values are **Not reported**, never inferred from a model name or billing multiplier. The model catalog is loaded when connecting; account policy and runtime availability still apply.

Add attachments with the paperclip, drag files into the chat, or paste files/images into the input. Ordinary text paste stays ordinary message text. Only explicit browser file/clipboard payloads are read; the bridge accepts bytes, not arbitrary local paths. Files are listed before sending and can be removed. Selecting or pasting alone does not send them to Copilot; **Send** submits the message and remaining attachments. Attachment-only sends are supported.

- Supported: text, logs and source files in UTF-8 or BOM-marked UTF-16; PNG, JPEG, GIF and WebP images.
- Limits: 5 files, 8 MiB combined, 256 KiB per text file and 4 MiB per image. PDF, Office documents, archives, executables and dump files are not supported.
- Text contents are included as explicitly labeled untrusted file data; images use the SDK's inline base64 `AttachmentBlob`. Known non-vision models reject images. No temporary files or added filesystem tools are used.
- Attached images have thumbnails before and after sending; click to enlarge, then close or press Escape. Image bytes are retained in UI history for previews. Native pre-send validation failures preserve the draft; a provider failure after acceptance may require reattaching files. New chat clears pending attachments.
- Actual Windows/WebView2 drop and clipboard behavior, provider image acceptance, model switching and live model prices remain real-host checks. Clipboard files must be exposed by the WebView2 paste event; the file picker is the fallback for unsupported clipboard formats. Browser tests use synthetic files and do not read the OS clipboard or contact Copilot.

## Tools and MCP servers

The wrench in the composer opens a searchable tool picker with WinDbg, SDK Built-In and MCP server groups. Check individual tools or a group's checkbox; partially selected groups show a mixed state. Selection is shared across this extension's chats and saved under `%LOCALAPPDATA%\WinDbgCopilotChat\tools.json`. The initial selection is only `debugger_command` and `debugger_target`. The built-in list comes from the installed Copilot SDK/CLI, not VS Code's extensions or its tool-selection settings.

The SDK executes built-ins inside its bundled CLI server process even when no CLI terminal UI is open ([SDK architecture](https://github.com/github/copilot-sdk#architecture)). The picker uses the current session's initialized tool metadata rather than the generic model-tool catalog, and refreshes it after model changes. This reflects runtime/model configuration, not a guarantee that every invocation will succeed: executable dependencies, permissions and network access still apply. Unavailable or unsupported selections are removed from the active allowlist. Descriptions are limited to two lines in the list; the information icon opens a scrollable full description without changing the checkbox.

| Built-in | This extension's policy |
| --- | --- |
| `view`, `grep`, `glob` | Keep when initialized by the session. File reading, content search and filename matching can work on Windows; `glob` is the filename-pattern tool. |
| `str_replace_editor` and other editing tools | Keep when initialized by the session. Model-specific names may vary. File edits occur outside the debugger and require appropriate approval. |
| Shell tools (`powershell`/`bash` and their read/stop/list helpers) | Keep the names actually initialized by the session. Do not infer a tool name from the OS, rename Bash tools to PowerShell, or add tools missing from the SDK response. Executable/environment requirements still apply. |
| `skill` | Hide: this extension explicitly disables skill loading. |
| `ask_user` | Hide: no `OnUserInputRequest` handler or question/answer UI is registered. Approval buttons are not a substitute for that handler. |
| `agent`, `task` | Hide for now: the SDK supports agents, but this host does not yet integrate child-agent identity, approvals and cancellation. This is not an SDK or Windows limitation. |

These name exclusions apply only to built-ins, not similarly named tools from explicitly configured MCP servers. The metadata/initialization RPCs are marked experimental (`GHCP001`); revalidate them when upgrading the SDK. SDK 1.0.13 / runtime 1.0.83 passed the local MCP startup, delayed-catalog recovery, empty-list and access-denied probes with allowlist restoration. These checks do not execute real built-in tools or authenticate to Azure DevOps. The observations below describe the earlier 1.0.4 baseline.

In a prompt-free Windows probe using SDK 1.0.4 and CLI 1.0.65, the generic `server.tools.list` returned Bash names, whereas initialized session metadata returned `powershell`, `read_powershell`, `stop_powershell` and `list_powershell`. Thus a missing PowerShell entry in the generic catalog does not establish that the runtime lacks it. Discovery must use source-qualified wildcards (`builtin:*`, `mcp:*`, `custom:*`, constructed with `ToolSet`), not a bare `*`. The probe verified live discovery and restoration to a restricted allowlist without authentication or tool execution; it does not establish compatibility for every model/runtime. This issue does not require an SDK upgrade.

Changes preserve the current conversation, session ID, model and approval mode and apply to the next message. Finish or cancel an active response before changing settings. Tool checkboxes update the SDK's live `availableTools` list. Connecting/disconnecting servers or reloading their configuration reconnects the SDK session; empty chats are recreated with the same ID because they have no replayable SDK history. A failed update blocks sending until tool settings are successfully reloaded.

Use **Open mcp.json** to open the current configuration with the Windows shell's associated application, then save your edits and use **Reload tools** to apply them. Missing files are created with an empty `servers` object; existing files are never overwritten, even if their JSON is invalid. On first use the extension looks for `%LOCALAPPDATA%\WinDbgCopilotChat\mcp.json`, then `%APPDATA%\Code\User\mcp.json`. A saved file choice takes precedence. If no configuration was found, Open creates `%LOCALAPPDATA%\WinDbgCopilotChat\mcp.json`. Configure a Windows `.json` file association with your editor if needed. Opening the file does not reload it or connect servers. Reading a file does not connect new servers: enable each server's connection checkbox and confirm the native trust prompt, then select its discovered tools. Reconnecting previously enabled servers, including at startup or reload, does not repeat that prompt. Only load configurations you trust; changes to a trusted file can change which programs are launched.

Both VS Code's `servers` object and the SDK's `mcpServers` object are accepted. JSON comments and trailing commas are supported. Supported transports are `stdio` (`local` is an alias) and `http`. Stdio accepts `command`, `args`, `env` and `cwd`/`workingDirectory`; relative working directories resolve against the configuration file's directory. HTTP accepts `url` and `headers`. Both accept `tools` and a positive `timeout` in milliseconds. Omitted `tools` is sent explicitly as `["*"]`: with SDK 1.0.4 / CLI 1.0.65, leaving it null produced `not_configured` in an isolated Microsoft Learn MCP probe, while the explicit wildcard connected and exposed its three tools. Explicit empty or restricted tool lists are preserved; discovered tools still require selection in the picker before use. `${env:NAME}` and `${userHome}` are resolved in the native process. The file is read locally; its command, environment and header values are not sent to the web bridge.

```json
{
	"servers": {
		"local-tools": {
			"type": "stdio",
			"command": "node",
			"args": ["C:/tools/example-mcp/server.js"]
		},
		"remote-tools": {
			"type": "http",
			"url": "https://example.com/mcp",
			"headers": { "Authorization": "Bearer ${env:EXAMPLE_MCP_TOKEN}" }
		}
	}
}
```

Replace these example servers with real trusted installations. VS Code `${input:...}`, `${workspaceFolder}`, `${config:...}`, `envFile`, legacy `sse` and VS Code secret storage are not implemented. Use explicit paths and environment-provided headers where appropriate. Environment changes require restarting WinDbg. There is no automatic file watching or VS Code extension-tool import.

For HTTP MCP servers requiring OAuth, enable the connection and use the server's **Sign in** icon. Confirm the native prompt and authorization site, finish signing in in your browser, then select the tools you need. When the SDK reports a transition to `connected`, the tool list refreshes automatically, deferred until the response finishes if one is running. Discovery also queries each connected MCP server's tool list; if it disagrees with the SDK's initialized catalog, the affected connection is disabled/enabled once and the catalog rebuilt. A server returning no tools or rejecting tool discovery produces a visible diagnostic. Use **Reload tools** to retry if needed. The SDK owns OAuth discovery, the loopback callback and token handling; the extension opts into the SDK's persistent OS keychain storage so reconnecting sessions can reuse authentication. Tokens and authorization URLs are never sent to the web bridge or stored in `mcp.json`. Existing VS Code credentials aren't imported. Closing the browser doesn't authenticate; retry Sign in if necessary after the SDK flow times out. Disconnecting a server disables its use, but doesn't revoke or delete its saved credentials.

To replace incorrect OAuth credentials, use **Sign in again** on the enabled HTTP server, including when it shows `connected` with no tools. After native confirmation, the SDK clears that server's cached OAuth token and starts a new authorization flow. This may affect other sessions using the same token. It does not clear browser cookies, sign out of the identity provider, or remove explicit authorization headers from `mcp.json`. Choose the intended account in the browser; credentials supplied by configuration must be changed in that configuration. There is no standalone sign-out/revocation operation.

Azure DevOps remote MCP accepts `{"url":"https://mcp.dev.azure.com/{organization}","type":"http"}`. Its organization must be connected to Microsoft Entra ID; standalone personal Microsoft account organizations aren't supported ([requirements](https://learn.microsoft.com/azure/devops/mcp-server/remote-mcp-server)). An unauthenticated SDK 1.0.4 probe confirmed `needs-auth` and an authorization URL at `https://login.microsoftonline.com`; completing sign-in, tenant consent, token refresh and authenticated tools still require a real-user validation. Other providers may require their own registered OAuth client and aren't covered by this verification.

In Ask mode, enabled built-in/MCP calls require approval of their arguments and result sharing before execution; SDK permission requests can require an additional approval. Unlike debugger output, these tools do not have a separate post-execution sharing gate: their approved results go directly to Copilot. Approve all also applies to these tools. Starting an MCP server may execute code or contact services before any tool call is made. Neither selection nor approvals sandbox a server or revoke effects of already-started work. Verify real SDK filtering, transport/authentication and session reconnection with trusted test servers before using sensitive data.

## Activity and links

SDK-exposed `assistant.reasoning`, reasoning deltas and `assistant.intent` progress appear with a brain icon; tools use a terminal icon. In-progress blocks start expanded and collapse when finished. All blocks can be reopened and copied; reasoning supports explicit read/stop, but tools have no playback button. Unfinished tools are marked Interrupted, not Completed, when a turn ends without a completion event. No hidden reasoning is inferred or generated. Speech never autoplays streaming chunks.

Each tool invocation has one activity keyed by the SDK's `ToolCallId`. Start, local output, output-sharing approval and completion update that same activity. During sharing approval it remains pending rather than creating a separate completed result. Withheld output remains locally visible in that activity but is not returned to the SDK. Delayed duplicate start/progress events do not reopen completed calls.

Debugger output is captured until `ExecuteCommandAsync` returns and is passed to the SDK in full after approval. The extension no longer applies the 64,000-character capture or 100,000-character tool-display limits; any SDK-side truncation/context handling is left to the SDK. Large tool output does not consume the extension's ordinary-message size budget. Output remains memory-resident and local history still has its 64 MiB save limit; save failure is reported without silently trimming output. Cancellation does not interrupt WinDbg: the extension waits for the running command to return before ending the turn. Changing the active target does not cancel the chat turn; the extension checks for a stopped target immediately before execution and otherwise relies on WinDbg to report command failure.

`debugger_target` obtains the current target type, running state and effective/actual processor architectures from public `IDbgTargetState` properties without executing commands. This read-only metadata is automatically returned to Copilot without execution or sharing approval in either mode. Cancellation and target-freshness checks remain enforced. `debugger_command` retains execution and output-sharing approvals in Ask every time mode. Neither tool can select another target.

HTTP/HTTPS links in responses open the default browser only when clicked. Other external schemes are blocked. The assistant can offer command links using `[Run stack](windbg-command:k)` or percent-encoded commands such as `[Verbose stack](windbg-command:kv%2020)`. Clicking is authorization to execute directly in WinDbg on the current stopped target, without another chat confirmation. Results stay in WinDbg's command window, not in the chat transcript or AI context. Commands can have side effects: review the command before clicking, especially for old-session links. Rendering, reopening history or hovering never executes a command. AI debugger-command calls still obey Ask every time / Approve all separately.

## Saved sessions

The history button opens a searchable list of this extension's saved sessions. Rename a session with the pencil, select one to resume, or delete an inactive session with the trash button and confirmation. To delete the current session, first open another or create a new chat. Deletion removes both SDK session data and the extension's history file; it does not revoke data already sent to Copilot or delete original attachments.

History uses the SDK's `ResumeSessionAsync` and `DeleteSessionAsync` for submitted conversations. Every connection starts a new session, retaining the latest saved model but not automatically resuming its conversation. Select an older session explicitly from history to resume it. Empty/local-only sessions are opened through session creation instead: the CLI does not create a replayable event log until a message is submitted, so resuming an empty session can fail with Session not found. Resuming resets execution mode to **Ask every time**, never replays commands and never reattaches an old debugger target. Old results remain historical evidence, not facts about a newly loaded target. Model and displayed transcript/usage metadata are restored.

UI history is saved under `%LOCALAPPDATA%\WinDbgCopilotChat\runtime\chat-history` at turn completion and session changes. Files contain conversation/activity text, local tool output (including withheld output), and attached image data; they are not additionally encrypted by the extension. SDK session storage can also contain attached text and approved results. Treat this directory as sensitive. Saves replace files atomically; malformed records are skipped by the list. A UI history file is limited to 64 MiB; failed saves produce an error, not silent success. Unexpected process termination during a turn may lose that turn's UI history. Browser demo history is synthetic and in-memory only.

The chat view opts into WinDbg's public `ToolWindowView.IsWindowPersisted` layout mechanism. A dedicated **Copilot** ribbon tab contains a native Fluent **Chat** button; its icon inherits the button foreground and the host supplies theme styling. Bootstrap compiles against the Fluent.Ribbon dependency recorded by this revision; no Fluent/ControlzEx copy is packaged. Actual restart/layout restoration and ribbon appearance require a real-host check.

## Development and tests

### F5 in VS Code

For build/install/launch without attaching a debugger, select **WinDbg: build, install, launch only** and press **F5**. This uses VS Code's built-in terminal launcher with debugging and child-process auto-attach disabled, and runs the same `scripts/debug.ps1` workflow. No C# debugger is needed for this configuration. Alternatively, use **Tasks: Run Task > WinDbg: build, install, launch**. The existing **launch and attach** and **attach only** configurations remain available for managed debugging.

Install the recommended Microsoft C# extension (`ms-dotnettools.csharp`), PowerShell 7.4+ and the build prerequisites above. Enable WinDbg's `WinDbgX.exe` app execution alias in Windows Settings and ensure `%LOCALAPPDATA%\Microsoft\WindowsApps` is on PATH. The alias is `WinDbgX.exe`, not `windbg.exe`. Close WinDbg before F5. F5 reuses existing `web/node_modules` while still running web tests and rebuilding web assets; it runs `npm ci` automatically only when that directory is absent. After changing package dependencies, stop any Vite/dev/test watcher and run `./scripts/debug.ps1 -RestoreWebDependencies` to reinstall them. This also repairs a partial `node_modules` left by a failed `npm ci`. A normal `scripts/build.ps1` still performs a clean dependency install; `-SkipWebRestore` opts into reuse without skipping web tests/build.

Select **WinDbg: build, install, launch and attach** in Run and Debug and press **F5**. Its pre-launch task builds/tests the web and .NET projects in Debug configuration, stages symbols, installs into the default `%LOCALAPPDATA%\dbg\UIExtensions`, then shell-launches `WinDbgX.exe`. Build or installation failures stop the sequence. Running WinDbg instances are not terminated automatically.

After the task completes, the C# debugger attaches by name to `DbgX.Shell.exe`, not to the shell/App Execution Alias launcher. Once attached, open **Copilot > Chat** to load the extension; breakpoints in its C# code bind as the assemblies load. Code that ran before attachment cannot be stopped retroactively. If your WinDbg uses another host executable name, change `processName` in `.vscode/launch.json`, or use **WinDbg: attach only** and select the host process (`DbgX.Shell` / `WinDbgX`), not Copilot CLI or WebView2. Attach-only is also the fallback if shell activation has not finished when the initial attach runs; it does not rebuild, install or launch. Disconnecting VS Code's debugger does not close WinDbg; close it before the next F5 build. These configurations debug managed extension code, not the debug target inside WinDbg or React code in WebView2.

The same workflow can be run from PowerShell with `./scripts/debug.ps1`. Use `-WhatIf` to preview without building/installing/launching, or `-SkipWebBuild -NoRestore` to reuse already-built web assets and restored NuGet dependencies during native-only iterations. `-Architecture x64` / `arm64` selects the package architecture and must match the WinDbg host. `scripts/build.ps1` still defaults to Release outside this F5 workflow; use `-Configuration Debug` for a manual debug package. Debug packages include the bootstrap, contracts, UI and core PDBs. No SDK upgrade is needed for F5 support.

```powershell
dotnet test tests/ChatCore.Tests/ChatCore.Tests.csproj -p:CopilotSkipCliDownload=true
cd web
npm ci
npm test
npm run build
npm run dev
```

`CopilotSkipCliDownload=true` prevents the SDK's CLI download during tests; it does not skip NuGet package restoration. After an explicit approved-feed restore, add `--no-restore` to the test command.

An ordinary browser uses synthetic demo data only. It cannot access a real debugger. Development uses Vite's loopback server; production uses only bundled assets under `https://copilot-chat.invalid/index.html` inside WebView2.

For browser tests, run `npm run test:e2e` after building. Playwright manages its preview server and can reuse a local server on port 4173. Tests use installed Microsoft Edge at 320, 420, and 1280 pixel widths. Speech tests use explicit fakes, not an actual audio device.

CI runs on pull requests and pushes to `main`. Version tags such as `v1.2.3` or `v1.2.3-rc.1` run the same checks and publish a ZIP/checksum through the protected `release` environment after licensing and host-review approval. See [Releases](docs/releasing.md) for required repository setup and tag commands. No installation or Copilot authentication occurs in CI.

## Safety and scope

- **Ask every time** is the new/resumed-session default and confirms AI debugger-command execution and result sharing separately. Read-only `debugger_target` metadata needs no approval and is returned automatically. **Approve all** authorizes command execution and outbound results, including pending command approvals. Returning to Ask every time restores confirmation at subsequent command approval boundaries. An explicit command-link click authorizes direct WinDbg execution without another confirmation; results are not sent to Copilot.
- Review commands too: a debugger command can itself write files, run extensions, launch shell/code, or cause network activity. This is not a debugger sandbox or a data-loss-prevention boundary. Closing a warning banner does not revoke automatic approval.
- Cancellation prevents further tool work and output release. It does **not** forcibly interrupt an already executing WinDbg command. Stop/break it in WinDbg if needed.
- Commands are serialized on the debugger synchronization context. A one-use approval remains bound to the displayed command or output, but not to a particular active target. Review the active target before approving. Other manual debugger operations may interleave with output; this must be verified in the real host.
- Only `debugger_command` and `debugger_target` are selected by default. Additional SDK tools and explicitly configured MCP servers are opt-in through the picker and can access resources outside WinDbg. Automatic MCP discovery, file hooks, skills and custom instructions remain disabled. Unselected tool calls are denied by the execution hook, including `report_intent`. CLI capability isolation needs live verification.
- Markdown raw HTML and automatic external images are disabled. Only explicitly attached raster images are previewed. Mermaid uses strict mode and sanitized SVG. The WebView itself cannot navigate externally, download files, use host objects or obtain browser permission grants; explicit HTTP(S) clicks are handed to the default browser.
- Read aloud uses only voices advertised `localService=true`, with no remote/default fallback. That flag is runtime-reported, not an independent privacy guarantee. No autoplay or streaming speech.
- Chat text, explicitly submitted attachments and approved output go to Copilot. The CLI and UI history retain session data under the isolated home; WebView2 has its own local profile. Closing/hiding the pane cancels the active turn; saved sessions can be resumed from history.

See [Design](docs/design.md) for implementation decisions, [Validation](docs/validation.md) for test boundaries and real-host checks, and [Third-party dependencies](THIRD-PARTY-NOTICES.md) for dependency licensing and redistribution terms.
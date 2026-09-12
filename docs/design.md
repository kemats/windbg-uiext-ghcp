# Design

This document describes the implemented extension, not a roadmap. Its evidence is the checked-in code, public package contracts and reproducible tests. DbgX package availability does not establish a Microsoft support or API stability guarantee.

## Ownership and dependency boundaries

| Component | Owns | Must not own |
| --- | --- | --- |
| Bootstrap | MEF exports, titled/persisted tool window, native ribbon, UI activation | SDK sessions or WebView2 dependencies in the host load context |
| WinDbgChatView | WebView2 bridge, theme, authentication console, debugger adapter | Hidden host-member introspection or a second chat state machine |
| ChatCore | SDK session lifecycle, approvals, tool activity, usage, history | WPF or DbgX types |
| Contracts | Cross-context interfaces and versioned bridge DTOs | SDK or UI dependency types |
| web | React rendering, composer, rich content and explicit local speech | Credentials or direct debugger access |

The bootstrap and shared contracts are the only root assemblies. UI/WebView2 and SDK dependencies live in separate directories and separate `AssemblyLoadContext` instances. Sharing contract, WPF and DbgX assemblies preserves type identity across the host boundary; loading a second copy of a contract would make otherwise identical-looking types incompatible. Tests check those identities and that bootstrap does not reference the UI/WebView2 assemblies. The contexts are non-collectible: disposing chat resources is not a promise to unload assemblies. Restart WinDbg after replacing binaries.

The tool-window and ribbon exports are separate MEF parts because both metadata attributes contribute `Name`; combining them produces duplicate metadata. Tests compose the exports through a `TypeCatalog`, not only direct constructors. Both the normal view and load-error view use a titled `ToolWindowView`. Failed UI activation is not cached, allowing a subsequent open to retry. Persisted export identifiers must remain stable across upgrades.

## Transport and trust

Production web assets are mapped to `https://copilot-chat.invalid/index.html` inside WebView2. There is no application HTTP listener. This reduces the deployment surface but does not eliminate the need to validate messages: the native bridge checks the exact document source, protocol version, request identity, session, payload sizes and replayed request IDs. Native state remains authoritative.

The WebView blocks external navigation, frames, downloads and unused permissions. Markdown disables raw HTML and remote images; only explicit raster attachments are previewed. Mermaid uses strict mode and sanitized SVG. HTML labels are disabled because sanitization removes `foreignObject`; enabling them can silently remove diagram labels. Browser tests assert label visibility as well as rejecting unsafe content. Links are inert until clicked.

The browser demo uses synthetic state. It cannot discover a live debugger or authenticate. Development Vite servers are not part of the installed extension.

## Debugger operations and consent

All engine operations are serialized through the adapter semaphore and posted to `IDbgEngineSynchronizationContextSource.SyncContext`. A WPF dispatcher is not a substitute for the debugger context. `IDbgConsole.ExecuteCommandAsync` completion, rather than an SDK notification, determines when output capture ends and the execution gate can be released.

| Operation | Consent | Result destination |
| --- | --- | --- |
| AI `debugger_target` | Automatic read-only property query; no debugger command | Copilot |
| AI `debugger_command`, Ask mode | Separate execution and output-sharing approvals | Local activity first; Copilot only after sharing approval |
| AI `debugger_command`, Approve all | Explicit mode authorizes execution and sharing, including pending approvals | Local activity and Copilot |
| Clicked command link | The click authorizes current-target execution without another prompt | WinDbg only |

Approvals are one-use and tied to the displayed command or output. Immediately before execution, the adapter confirms that a stopped target exists; WinDbg then owns command behavior if the active target changes. The extension deliberately does not subscribe to target lifecycle events or cancel the chat turn when the active target changes. Real-host command failure behavior and manual-console output interleaving require smoke tests.

Cancelling chat denies pending approvals and suppresses stale results. It does not interrupt an already-running debugger command. The adapter retains its gate until that command returns, and turn cleanup awaits outstanding tool tasks. Releasing either early could mix output or execute conflicting work.

SDK callbacks and progress events can arrive in different orders. `CopilotTool.DefineTool` binds `ToolInvocation` outside model-supplied arguments; its `ToolCallId` identifies a single activity across start, local output, sharing and completion. Late events must not duplicate or reopen completed activities. Tests invoke the SDK function with simulated context and verify withheld output is absent from the returned SDK result.

Debugger commands are not sandboxed: they may write files, load extensions, launch code or cause network activity. Denying output sharing cannot undo effects of a command already executed. Full output is memory-resident; there is no extension-side truncation, and SDK context limits still apply.

## Authentication and session lifecycle

The UI requests one connection after startup, including under React effect replay. It uses saved CLI authentication and never starts interactive OAuth automatically. A failure requires manual retry. Each successful connection starts a fresh session with the previous model; old conversations are resumed only through History. New and resumed sessions default to Ask mode.

The SDK child process inherits the Windows environment except the three token overrides intentionally cleared for CLI-selected login. Replacing the entire environment can break native process initialization. Authentication runs the packaged CLI in a visible PowerShell console, with the executable path passed as an environment value rather than interpolated script text. Converting forwarded output items to text avoids PowerShell error-record decoration while preserving diagnostics and exit codes. Tests cover paths with spaces/metacharacters and simulated success/failure; no OAuth output enters the bridge or test logs.

The extension keeps a UI history record separately from SDK session storage. An empty local record does not imply an SDK session can be resumed; submitted conversations use resume, while empty ones use creation. SDK title events update automatic titles, but manual names take precedence. Switching accounts does not isolate history on the same Windows profile.

History includes local tool output even when sharing was denied, plus attachment preview bytes. It is not additionally encrypted. Atomic replacement and a 64 MiB limit protect file integrity, not confidentiality. Save failures are reported without trimming data; an interrupted turn may not have been saved. Uninstall retains local history and credentials.

## Rendering and reported data

Usage and model prices come from SDK telemetry. A model cost multiplier is not a credit charge; absent data stays unknown or partial. Displayed session totals are not an account billing statement. Late usage belongs to the session subtotal rather than inventing a completed-turn attribution.

Speech uses explicitly selected voices advertised as `localService=true`, without remote fallback or autoplay. Playback identity guards reject late callbacks from prior utterances. Runtime-reported locality does not independently prove network behavior; mock tests cannot establish installed Japanese voice availability.

## Maintenance

- Keep packaging layout and shared assembly identities covered when changing dependencies.
- Test event permutations and delayed debugger completion, not only the successful synchronous path.
- Keep authentication, debug-target access and installation out of ordinary CI.
- Revalidate host compatibility when updating DbgX, WebView2 or Fluent.Ribbon; do not copy host implementation details into tests.
- Keep release evidence with each candidate; see [Validation](validation.md). Preserve useful rationale here instead of retaining superseded implementation plans.
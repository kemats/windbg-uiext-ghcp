# Usage

This guide covers operating WinDbg Copilot Chat. See the [README](../README.md) for installation and [Design](design.md) for implementation rationale.

## Authentication

Opening the chat pane makes one connection attempt with saved authentication. If no usable login exists, open the GitHub account menu and select **Sign in**. When authenticated, the same menu offers **Switch account**. A failed connection leaves **Connect** available for retry and does not repeatedly launch authentication.

Sign-in uses an installed official GitHub Copilot CLI. The SDK's bundled runtime is not an interactive CLI. If the official CLI is missing, the extension can offer a confirmed per-user installation through WinGet. Managed devices can use an organization-approved installation instead. Restart WinDbg if a newly installed CLI is not yet discoverable.

Authentication runs in a visible PowerShell console using the CLI's device-code flow. Follow the instructions in that window and use **Use a different account** on GitHub's account-selection page when switching accounts. Closing only the browser does not cancel the CLI; close or cancel the console to abort. A clipboard-copy warning does not by itself mean authentication failed.

The extension keeps its Copilot home under `%LOCALAPPDATA%\WinDbgCopilotChat\runtime`. Device codes and tokens do not cross the JavaScript bridge. Child processes ignore inherited `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, and `GITHUB_TOKEN` overrides so the selected CLI login is used. Do not paste tokens into chat.

Switching accounts starts a fresh Ask session but retains local history. The history directory is shared by accounts on the same Windows profile, so account switching is not a history-isolation boundary. The extension does not provide credential deletion.

## Logging

Extension, chat runtime, and Copilot SDK logs are routed to WinDbg diagnostics. The default minimum level is `Information`. Set `WINDBG_COPILOT_LOG_LEVEL` before starting WinDbg to `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, or `None`, then restart WinDbg.

Use `Debug` when troubleshooting MCP. It records configuration sources, server and tool counts, status transitions, reconnects, refresh scheduling, and session recreation or resumption. It does not record MCP commands, environment values, headers, credentials, authorization URLs, tool arguments, or tool results.

## Chats and sessions

The header provides account, history, session-title, and new-chat controls. Rename a session with the pencil icon. Generated titles are used when available; a manual name takes precedence. New chat retains the selected model but resets session usage and starts in **Ask every time** mode.

The history panel can search, rename, resume, and delete saved sessions. To delete the current session, first open another session or create a new chat. Resuming never reruns old commands or reattaches an old debugger target. Treat old results as historical evidence, not facts about the currently loaded target.

UI history is stored under `%LOCALAPPDATA%\WinDbgCopilotChat\runtime\chat-history`. It can include conversation text, attached image data, local tool output, and output withheld from Copilot. It is not additionally encrypted. A history record is limited to 64 MiB; failed saves are reported rather than silently trimmed. Uninstalling retains local history and credentials.

The session information panel shows SDK-reported model, context usage, and cost. Values can be missing or partial and are not an account billing statement. Compaction costs are not included.

## Responses, links, and speech

Completed responses expose copy, browser-report, Immersive Reader, and read/stop controls. Browser reports are served only on `127.0.0.1` at random URLs, remain in memory for up to one hour, and are cleared when WinDbg exits. The URL can expose sensitive chat content while it remains live.

HTTP and HTTPS links open in the default browser only when clicked. Links using `windbg-command:` execute the displayed command directly against the current stopped target. The click is the authorization: there is no additional chat confirmation, and output remains in WinDbg rather than being added to AI context. Review command links before using them.

Read aloud never autoplays. It uses voices exposed by WebView2; a voice reported as non-local may send text to the operating-system voice provider.

## Models and attachments

Changing the model affects the current session's next message while preserving its history, ID, and approval mode. It can reset the prompt cache and increase cost. Model availability, prices, limits, and image support are SDK-reported and account-policy dependent.

Use the paperclip, drag and drop, or paste files into the composer. Selecting a file does not send it; **Send** submits the message and remaining attachments.

- Supported text: UTF-8 or BOM-marked UTF-16 logs, source, and text files.
- Supported images: PNG, JPEG, GIF, and WebP.
- Limits: 5 files, 8 MiB combined, 256 KiB per text file, and 4 MiB per image.
- Unsupported: PDF, Office documents, archives, executables, and dump files.

Text is submitted as explicitly labeled untrusted file data. Images use inline base64 data and are retained in UI history for previews. No temporary files or arbitrary local paths are passed through the web bridge.

## Tools and MCP servers

The wrench in the composer opens the tool picker. Only `debugger_command` and `debugger_target` are selected initially. Additional SDK built-ins and explicitly configured MCP tools are opt-in. Selection is shared across this extension's chats and saved in `%LOCALAPPDATA%\WinDbgCopilotChat\tools.json`.

`debugger_target` reads current target metadata without executing a debugger command and returns it automatically. In **Ask every time** mode, `debugger_command` requires separate execution and output-sharing approvals. Other enabled tools require approval before execution and send approved results directly to Copilot. **Approve all** authorizes these operations until the mode is changed.

Use **Open mcp.json** to edit the active configuration, then **Reload tools**. The extension checks `%LOCALAPPDATA%\WinDbgCopilotChat\mcp.json` first and `%APPDATA%\Code\User\mcp.json` second unless a choice was previously saved. Missing files are created with an empty `servers` object; existing files are never overwritten. Opening a file does not reload it or connect servers.

Both VS Code's `servers` object and the SDK's `mcpServers` object are accepted. JSON comments and trailing commas are supported. Available transports are `stdio` (`local` is an alias) and `http`:

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

Stdio servers accept `command`, `args`, `env`, and `cwd`/`workingDirectory`; HTTP servers accept `url` and `headers`. Both accept `tools` and a positive `timeout`. `${env:NAME}` and `${userHome}` are resolved in the native process. VS Code inputs, workspace/configuration variables, secret storage, `envFile`, and legacy SSE are not supported.

Enable each server and confirm the native trust prompt before selecting its tools. Starting a server can run code or contact a service before a tool call occurs. Only load configurations you trust. Environment changes require restarting WinDbg.

For OAuth-capable HTTP servers, enable the connection and use **Sign in**. The SDK owns browser authorization, callback handling, and persistent OS-keychain token storage. Tokens and authorization URLs are not sent to the web bridge or stored in `mcp.json`. Disconnecting does not revoke credentials. **Sign in again** clears the SDK's cached token for that server after confirmation, but does not clear browser cookies or configured authorization headers.

## Safety and data handling

- Debugger commands are not sandboxed. They can write files, load extensions, launch code, or use the network.
- Cancellation prevents further tool work and output release, but does not forcibly interrupt a WinDbg command already running.
- Review the active target before approving. Approval is tied to the displayed operation, not to a particular target.
- Chat text, submitted attachments, and approved output are sent to Copilot.
- Additional SDK tools and MCP servers can access resources outside WinDbg and are not a data-loss-prevention boundary.
- Raw Markdown HTML and automatic external images are disabled. The WebView cannot navigate externally or receive browser permission grants.

For the precise trust boundaries and lifecycle behavior, see [Design](design.md). For behaviors that still require manual host verification, see [Validation](validation.md).
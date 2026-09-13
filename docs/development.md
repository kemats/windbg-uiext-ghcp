# Development

This guide covers local builds, installation, debugging, and tests. See the [README](../README.md) for the supported runtime and build prerequisites.

## Build and package

From PowerShell at the repository root:

```powershell
./scripts/build.ps1
```

The script restores locked frontend dependencies, runs frontend tests, builds web assets, builds and tests the .NET projects, and stages `artifacts/package`. DbgX and native debugger binaries are not included.

Close WinDbg before installing:

```powershell
./scripts/install.ps1 -WhatIf
./scripts/install.ps1
```

Only `WinDbgChatView.dll` and `WinDbgCopilot.Contracts.dll` belong at the package root. Private UI and WebView2 dependencies are under `WinDbgCopilotChat/ui`; `core` and `web` are sibling directories. Do not flatten the package or copy the entire `artifacts` directory. See [Design](design.md) for the assembly-isolation rationale.

Stop Vite preview, development, and test watchers before a clean build. On Windows, `npm ci` can fail when a running process locks a native module. For host-only changes, `-SkipWebBuild` reuses existing `web/dist` assets.

## Dependency acquisition

If the SDK's Copilot runtime download fails, obtain the required architecture-specific npm package with normal TLS validation and pass its executable explicitly:

```powershell
$cliVersion = (dotnet msbuild ./src/ChatCore/ChatCore.csproj -getProperty:CopilotCliVersion).Trim()
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$cliPackage = "@github/copilot-win32-$architecture"
npm install --prefix artifacts/cli --ignore-scripts --no-audit --no-fund "$cliPackage@$cliVersion"
./scripts/build.ps1 -CopilotCliBinaryPath "./artifacts/cli/node_modules/$cliPackage/copilot.exe"
```

Verify that the downloaded runtime is compatible with the referenced SDK. Package metadata and the executable's reported version are not always identical.

On managed devices, use approved NuGet and npm feeds. The repository does not override local feed policy. An approved NuGet restore can be separated from the build:

```powershell
dotnet restore windbg-uiext-ghcp.slnx --locked-mode
./scripts/build.ps1 -NoRestore
```

An explicit `--source` or `--configfile` can be supplied without committing organization-specific endpoints or credentials. `-NoRestore` skips only NuGet restoration; frontend installation and the SDK runtime download retain their own configuration. Use `-CopilotCliBinaryPath` for a runtime acquired through another approved channel.

## F5 in VS Code

Install the recommended Microsoft C# extension (`ms-dotnettools.csharp`). Enable the `WinDbgX.exe` app execution alias in Windows Settings and ensure `%LOCALAPPDATA%\Microsoft\WindowsApps` is on `PATH`. Close WinDbg before starting a build-and-install workflow.

Select **WinDbg: build, install, launch only** and press **F5** to build, test, install, and launch without attaching a debugger. The equivalent task is **Tasks: Run Task > WinDbg: build, install, launch**.

Select **WinDbg: build, install, launch and attach** to attach the managed debugger after launch. The debugger attaches to `DbgX.Shell.exe`, not the app-execution-alias launcher. Open **Copilot > Chat** after attachment so extension breakpoints bind as assemblies load. Use **WinDbg: attach only** if launch activation has not completed or if WinDbg is already running.

Disconnecting the VS Code debugger does not close WinDbg. Close WinDbg before the next build. These configurations debug managed extension code, not the target being debugged by WinDbg or the React application inside WebView2.

The same launch-only workflow is available from PowerShell:

```powershell
./scripts/debug.ps1
```

Use `-WhatIf` to preview. Use `-SkipWebBuild -NoRestore` for native-only iterations with existing assets and restored packages. `-Architecture x64` or `arm64` must match the WinDbg host. Run `./scripts/debug.ps1 -RestoreWebDependencies` after dependency changes or to repair an incomplete `web/node_modules` directory.

## Focused tests

```powershell
dotnet test tests/ChatCore.Tests/ChatCore.Tests.csproj -p:CopilotSkipCliDownload=true
npm --prefix web test
npm --prefix web run build
npm --prefix web run test:e2e
```

`CopilotSkipCliDownload=true` avoids the SDK runtime download during tests but does not skip NuGet restore. Add `--no-restore` after a separate approved-feed restore.

Playwright manages its preview server and tests installed Microsoft Edge at 320, 420, and 1280 pixel widths. Browser tests use synthetic data and cannot access a debugger, authenticate, submit prompts to Copilot, or inspect the OS clipboard. `npm --prefix web run dev` starts the synthetic browser demo.

CI runs for pull requests and pushes to `main`. Version tags run the same checks and publish through the protected release environment. See [Releases](releasing.md) for repository setup and publication commands, and [Validation](validation.md) for coverage boundaries and the real-host checklist.
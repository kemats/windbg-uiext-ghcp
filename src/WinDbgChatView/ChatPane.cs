using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Contracts;
using DbgX.Interfaces;
using DbgX.Interfaces.Services;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WinDbgChatView;

public sealed class ChatPane : Grid
{
    private const string Origin = "https://copilot-chat.invalid";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };
    private readonly DebuggerAdapter _debugger;
    private readonly IDbgThemeService _theme;
    private readonly IChatLogSink _log;
    private readonly WebView2 _browser = new();
    private readonly BrowserReportHost _reportHost = new();
    private readonly bool _isElevated = IsProcessElevated();
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = [];
    private readonly DispatcherTimer _themeTimer;
    private IChatRuntime? _runtime;
    private CopilotAssemblyLoadContext? _loadContext;
    private bool _initialized;
    private bool _connecting;
    private long _sequence;
    private string? _lastTheme;

    public ChatPane(IDbgEngineSynchronizationContextSource engineContext, IDbgConsole console,
        IDbgOutputEvents output, IDbgTargetState target, IDbgThemeService theme, IChatLogSink log)
    {
        _debugger = new DebuggerAdapter(engineContext, console, output, target);
        _theme = theme;
        _log = log;
        Children.Add(_browser);
        Loaded += OnLoaded;
        _themeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => SendTheme(), Dispatcher);
        Unloaded += async (_, _) =>
        {
            _themeTimer.Stop();
            try { if (_runtime is not null) await _runtime.CancelAsync(); }
            catch (Exception exception) { _log.Log(ChatLogLevel.Debug, nameof(ChatPane), "Cancellation during unload failed", exception); }
        };
        Application.Current.Exit += async (_, _) =>
        {
            _reportHost.Dispose();
            _browser.Dispose();
            if (_runtime is not null) await _runtime.DisposeAsync();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        _themeTimer.Start();
        if (_initialized) return;
        _initialized = true;
        try
        {
            var root = Path.GetDirectoryName(typeof(ChatPane).Assembly.Location)!;
            var assets = Path.GetFullPath(Path.Combine(root, "..", "web"));
            if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("Bundled web assets are missing. Run scripts/build.ps1.");
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDbgCopilotChat", "webview");
            CoreWebView2Environment.SetLoaderDllFolderPath(RuntimeAssets.NativeDirectory(root));
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _browser.EnsureCoreWebView2Async(environment);
            var core = _browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.SetVirtualHostNameToFolderMapping("copilot-chat.invalid", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            core.PermissionRequested += (_, request) => request.State = CoreWebView2PermissionState.Deny;
            core.NewWindowRequested += (_, request) => request.Handled = true;
            core.DownloadStarting += (_, request) => request.Cancel = true;
            core.NavigationStarting += (_, request) => request.Cancel = request.Uri != Origin + "/index.html";
            core.FrameNavigationStarting += (_, request) => request.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, request) =>
            {
                if (!Uri.TryCreate(request.Request.Uri, UriKind.Absolute, out var uri) || uri.GetLeftPart(UriPartial.Authority) != Origin)
                    request.Response = core.Environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "");
            };
            core.WebMessageReceived += OnMessage;
            core.Navigate(Origin + "/index.html");
        }
        catch (Exception exception)
        {
            _log.Log(ChatLogLevel.Error, nameof(ChatPane), "WebView initialization failed", exception);
            Children.Clear();
            Children.Add(new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
        }
    }

    private sealed record BridgeRequest(int Version, string Type, string RequestId, string? SessionId,
        string? Text, string? Model, string? Mode, string? ApprovalId, bool? Approved,
        ChatAttachment[]? Attachments, string? HistoryId, string[]? Tools, string[]? Servers, string[]? Diagrams);

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (args.Source != Origin + "/index.html") return;
        try
        {
            var json = args.WebMessageAsJson;
            if (json.Length > 12 * 1024 * 1024) throw new InvalidOperationException("Message too large.");
            var request = JsonSerializer.Deserialize<BridgeRequest>(json, Json) ?? throw new InvalidOperationException("Invalid message.");
            if (request.Version != 1 || !Guid.TryParse(request.RequestId, out _) || !_seen.Add(request.RequestId)) return;
            _seenOrder.Enqueue(request.RequestId);
            while (_seenOrder.Count > 2048) _seen.Remove(_seenOrder.Dequeue());
            if (request.Type == "ready")
            {
                SendTheme(true);
                Post("host", new { elevated = _isElevated });
                if (_runtime is not null) SendSnapshot(_runtime.Snapshot);
                else Post("disconnected", new { message = "Sign in with Copilot CLI, then connect." });
                return;
            }
            if (request.Type == "connect") { await ConnectAsync(); return; }
            if (request.Type == "signIn") { await SignInAsync(); return; }
            if (request.Type == "openUrl")
            {
                var url = request.Text ?? "";
                if (url.Length > 8192 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new ArgumentException("Only HTTP and HTTPS links can be opened.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return;
            }
            var runtime = _runtime ?? throw new InvalidOperationException("Connect to Copilot first.");
            if (request.SessionId != runtime.Snapshot.SessionId) return;
            var mcpPath = runtime.Snapshot.ToolSettings?.ConfigPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDbgCopilotChat", "mcp.json");
            switch (request.Type)
            {
                case "send": await runtime.SendAsync(request.Text ?? "", request.Attachments); break;
                case "cancel": await runtime.CancelAsync(); break;
                case "new": await runtime.NewChatAsync(request.Model); break;
                case "resume": await runtime.ResumeAsync(request.HistoryId ?? ""); Post("sessionChanged", new { }); break;
                case "rename": await runtime.RenameAsync(request.HistoryId ?? "", request.Text ?? ""); break;
                case "delete": await runtime.DeleteAsync(request.HistoryId ?? ""); break;
                case "command": await runtime.RunCommandAsync(request.Text ?? ""); break;
                case "openReport":
                case "openReportReader":
                    var readerMode = request.Type == "openReportReader";
                    var reportHtml = CreateReportHtml(request.Text ?? "", request.Diagrams, readerMode);
                    var reportStart = readerMode
                        ? _reportHost.CreateImmersiveReaderStartInfo(reportHtml, FindEdgeExecutable()
                            ?? throw new InvalidOperationException("Microsoft Edge is required to open Immersive Reader."))
                        : _reportHost.CreateStartInfo(reportHtml);
                    using (System.Diagnostics.Process.Start(reportStart)) { }
                    break;
                case "tools":
                    var servers = request.Servers ?? [];
                    var enabled = runtime.Snapshot.ToolSettings?.Servers.Where(server => server.Enabled).Select(server => server.Name).ToHashSet() ?? [];
                    if (servers.Any(name => !enabled.Contains(name)) && MessageBox.Show(
                        "Connecting MCP servers can launch local programs or contact remote services. Only connect servers whose configuration you trust. Continue?",
                        "Connect MCP servers", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    { Post("toolsChanged", new { }); break; }
                    await runtime.SetToolsAsync(request.Tools ?? [], servers);
                    Post("toolsChanged", new { });
                    break;
                case "reloadTools":
                    await runtime.LoadMcpAsync(File.Exists(mcpPath) ? mcpPath : null);
                    Post("toolsChanged", new { });
                    break;
                case "authenticateMcp":
                case "reauthenticateMcp":
                    var serverName = request.Text ?? "";
                    var forceReauth = request.Type == "reauthenticateMcp";
                    if (runtime.Snapshot.Busy) throw new InvalidOperationException("Wait for the current operation before signing in.");
                    var authServer = runtime.Snapshot.ToolSettings?.Servers.FirstOrDefault(server => server.Name == serverName && server.Enabled &&
                        (forceReauth ? server.CanReauthenticate : server.CanAuthenticate));
                    if (authServer is null) throw new InvalidOperationException("Enable an HTTP MCP server before signing in.");
                    var signInPrompt = forceReauth
                        ? $"Clear the SDK's saved OAuth token for MCP server '{serverName}' and sign in again? This can affect other sessions using this server. Browser sign-in cookies are not cleared; choose the intended account in the browser."
                        : $"Sign in to MCP server '{serverName}' using your browser? After signing in, return here.";
                    if (MessageBox.Show(signInPrompt,
                        "MCP sign-in", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                    {
                        var authorizationUrl = await runtime.AuthenticateMcpAsync(serverName, forceReauth);
                        if (!string.IsNullOrEmpty(authorizationUrl))
                        {
                            var start = CreateMcpAuthenticationStartInfo(authorizationUrl);
                            if (MessageBox.Show($"Open the authorization site {new Uri(start.FileName).GetLeftPart(UriPartial.Authority)} for '{serverName}'?",
                                "Open MCP authorization", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                            { using (System.Diagnostics.Process.Start(start)) { } }
                        }
                    }
                    Post("toolsChanged", new { });
                    break;
                case "loadMcp":
                    using (System.Diagnostics.Process.Start(CreateMcpEditorStartInfo(mcpPath))) { }
                    Post("toolsChanged", new { });
                    break;
                case "model":
                    await runtime.SetModelAsync(request.Model ?? "");
                    Post("modelChanged", new { sessionId = request.SessionId });
                    break;
                case "mode":
                    if (!Enum.TryParse<ApprovalMode>(request.Mode, out var mode) || !Enum.IsDefined(mode)) throw new InvalidOperationException("Invalid mode.");
                    runtime.SetMode(mode);
                    break;
                case "approval":
                    var pending = runtime.Snapshot.Approvals.FirstOrDefault(item => item.Id == request.ApprovalId);
                    if (pending is not null) runtime.ResolveApproval(request.SessionId!, pending.Id, request.Approved == true);
                    break;
                default: throw new InvalidOperationException("Unsupported message type.");
            }
        }
        catch (Exception exception)
        {
            _log.Log(ChatLogLevel.Error, nameof(ChatPane), "Bridge request failed", exception);
            Post("error", new { message = exception.Message });
        }
    }

    internal static System.Diagnostics.ProcessStartInfo CreateMcpAuthenticationStartInfo(string url)
    {
        if (url.Length > 16384 || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("MCP authorization requires an HTTPS URL without embedded credentials.");
        return new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
    }

    internal static string CreateReportHtml(string markdown, string[]? diagrams = null, bool readerMode = false)
    {
        if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("The response is empty.");
        if (markdown.Length > 2 * 1024 * 1024) throw new ArgumentException("The response is too large to open as a report.");
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
        var document = Markdown.Parse(markdown, pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            var url = link.Url;
            if (url is null || url.StartsWith('#')) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                link.Url = null;
        }
        var content = Markdown.ToHtml(document, pipeline);
        var mermaidBlocks = document.Descendants<FencedCodeBlock>()
            .Where(block => string.Equals(block.Info, "mermaid", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (diagrams is not null)
        {
            if (diagrams.Length > 16) throw new ArgumentException("The report contains too many diagrams.");
            for (var index = 0; index < Math.Min(diagrams.Length, mermaidBlocks.Length); index++)
            {
                var source = mermaidBlocks[index].Lines.ToString().TrimEnd('\r', '\n');
                var renderedBlock = Markdown.ToHtml($"```mermaid\n{source}\n```", pipeline);
                var svg = ValidateReportSvg(diagrams[index]);
                var encodedSvg = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
                content = content.Replace(renderedBlock,
                    $"<figure class=\"diagram\"><img src=\"data:image/svg+xml;base64,{encodedSvg}\" alt=\"Mermaid diagram\"></figure>\n",
                    StringComparison.Ordinal);
            }
        }
        var language = markdown.Any(character => character is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff') ? "ja" : "en";
        if (readerMode) content = content.Replace("<pre><code", "<pre aria-hidden=\"true\"><code", StringComparison.Ordinal);
        return $$"""
            <!doctype html>
            <html lang="{{language}}">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:">
              <title>Copilot response</title>
              <style>
                :root { color-scheme: light dark; font-family: Georgia, 'Yu Mincho', serif; }
                body { margin: 0; background: Canvas; color: CanvasText; }
                main { width: min(920px, calc(100% - 48px)); margin: 48px auto 96px; }
                header { border-bottom: 1px solid GrayText; margin-bottom: 32px; padding-bottom: 16px; }
                header h1 { margin: 0; font: 600 28px/1.2 'Segoe UI', 'Yu Gothic UI', sans-serif; }
                time { color: GrayText; font: 14px/1.5 'Segoe UI', 'Yu Gothic UI', sans-serif; }
                article { font-size: 20px; line-height: 1.65; }
                article h1, article h2, article h3 { margin: 1.4em 0 .5em; line-height: 1.25; font-family: 'Segoe UI', 'Yu Gothic UI', sans-serif; }
                article h1 { font-size: 1.7em; } article h2 { font-size: 1.4em; } article h3 { font-size: 1.18em; }
                article p, article ul, article ol, article pre, article blockquote, article table { margin: 0 0 1em; }
                article li + li { margin-top: .3em; }
                article code { font: .85em/1.5 Consolas, monospace; }
                article pre { overflow: auto; padding: 16px; border: 1px solid GrayText; border-radius: 4px; }
                article table { width: 100%; border-collapse: collapse; font-family: 'Segoe UI', 'Yu Gothic UI', sans-serif; font-size: .9em; }
                article th, article td { padding: 8px 10px; border: 1px solid GrayText; text-align: left; }
                article blockquote { margin-left: 0; padding-left: 18px; border-left: 3px solid GrayText; }
                article a { color: LinkText; }
                article .diagram { margin: 1.5em 0; overflow-x: auto; }
                article .diagram img { display: block; max-width: 100%; height: auto; margin: auto; }
                @media (max-width: 600px) { main { width: min(100% - 28px, 920px); margin-top: 24px; } article { font-size: 18px; } }
                @media print { main { width: auto; margin: 0; } header { margin-bottom: 20px; } }
              </style>
            </head>
            <body><main><header><h1>Copilot response</h1><time>{{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}}</time></header><article id="immersive-reader-content" lang="{{language}}">{{content}}</article></main></body>
            </html>
            """;
    }

    private static string ValidateReportSvg(string svg)
    {
        if (string.IsNullOrWhiteSpace(svg) || svg.Length > 1024 * 1024) throw new ArgumentException("Invalid report diagram.");
        using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "svg" || root.Name.NamespaceName != "http://www.w3.org/2000/svg")
            throw new ArgumentException("Invalid report diagram.");
        var blockedElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a", "foreignObject", "iframe", "image", "script", "use" };
        foreach (var element in root.DescendantsAndSelf())
        {
            if (blockedElements.Contains(element.Name.LocalName)) throw new ArgumentException("Unsafe report diagram.");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attribute.Name.LocalName is "href" or "src"
                    || attribute.Value.Contains("javascript:", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Unsafe report diagram.");
            }
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    internal static System.Diagnostics.ProcessStartInfo CreateMcpEditorStartInfo(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(file, new { servers = new Dictionary<string, object>() }, new JsonSerializerOptions { WriteIndented = true });
        }
        return new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" };
    }

    internal static string? FindEdgeExecutable(string? programFilesX86 = null, string? programFiles = null, string? localAppData = null)
    {
        programFilesX86 ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[] { programFilesX86, programFiles, localAppData }
            .Select(root => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"))
            .FirstOrDefault(File.Exists);
    }

    private async Task SignInAsync()
    {
        if (_connecting || _runtime?.Snapshot.Busy == true) throw new InvalidOperationException("Wait for the current operation before signing in.");
        var switchingAccount = _runtime?.Snapshot.Account?.Authenticated == true;
        var prompt = switchingAccount
            ? "The current chat will be saved and disconnected. Follow the CLI sign-in flow in your browser. Select 'Use a different account' on GitHub to switch accounts. Continue?"
            : "Sign in to GitHub Copilot using the official CLI and your browser? If the CLI is missing, you will be asked whether to install it using winget.";
        if (MessageBox.Show(prompt,
            switchingAccount ? "Switch Copilot account" : "Sign in to GitHub Copilot", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Post("signInCancelled", new { });
            return;
        }
        _connecting = true;
        Post("connecting", new { });
        try
        {
            var signInCli = FindInstalledCommand(GetCommandSearchPath(), ["copilot.exe", "copilot.cmd"]);
            if (signInCli is null)
            {
                if (MessageBox.Show("GitHub Copilot CLI is required for account sign-in but was not found. Install the official GitHub.Copilot package using winget? A separate window will show installation progress and any agreement prompts.",
                    "Install GitHub Copilot CLI", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    Post("signInCancelled", new { });
                    return;
                }
                var winget = FindInstalledCommand(GetCommandSearchPath(), ["winget.exe"])
                    ?? throw new InvalidOperationException("winget was not found. Install Windows App Installer, or install GitHub Copilot CLI manually, then retry account sign-in.");
                using var installer = System.Diagnostics.Process.Start(CreateCopilotInstallStartInfo(winget))
                    ?? throw new InvalidOperationException("Could not start the Copilot CLI installer.");
                await installer.WaitForExitAsync();
                if (installer.ExitCode != 0) throw new InvalidOperationException($"Copilot CLI installation did not complete (exit code {installer.ExitCode}). Retry account sign-in after installing it.");
                signInCli = FindInstalledCommand(GetCommandSearchPath(), ["copilot.exe", "copilot.cmd"])
                    ?? throw new InvalidOperationException("Copilot CLI was installed but could not be located. Restart WinDbg to refresh PATH, then retry account sign-in.");
            }
            var runtime = _runtime;
            if (runtime is not null)
            {
                runtime.Changed -= SendSnapshot;
                await runtime.DisposeAsync();
                _runtime = null;
            }
            Post("disconnected", new { });
            Post("connecting", new { });
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDbgCopilotChat", "runtime");
            Directory.CreateDirectory(directory);
            var start = CreateSignInStartInfo(signInCli, directory);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start Copilot sign-in.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Copilot sign-in did not complete. Connect again or retry sign-in.");
        }
        finally { _connecting = false; }
        await ConnectAsync();
    }

    private static string GetCommandSearchPath() => string.Join(";",
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));

    internal static string? FindInstalledCommand(string searchPath, string[] names)
    {
        foreach (var entry in searchPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = Environment.ExpandEnvironmentVariables(entry.Trim('"'));
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    internal static System.Diagnostics.ProcessStartInfo CreateCopilotInstallStartInfo(string wingetPath)
    {
        var start = new System.Diagnostics.ProcessStartInfo(wingetPath) { UseShellExecute = true };
        foreach (var argument in new[] { "install", "--id", "GitHub.Copilot", "--exact", "--source", "winget", "--scope", "user" })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static System.Diagnostics.ProcessStartInfo CreateSignInStartInfo(string cliPath, string directory)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new System.Diagnostics.ProcessStartInfo(shell) { WorkingDirectory = directory, UseShellExecute = false };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("& $env:WINDBG_COPILOT_SIGNIN_CLI login --device-code 2>&1 | ForEach-Object { $_.ToString() } | Out-Host; exit $LASTEXITCODE");
        start.Environment["WINDBG_COPILOT_SIGNIN_CLI"] = cliPath;
        start.Environment["COPILOT_HOME"] = directory;
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }) start.Environment.Remove(name);
        return start;
    }

    private async Task ConnectAsync()
    {
        if (_connecting || _runtime is not null) return;
        _connecting = true;
        Post("connecting", new { });
        IChatRuntime? runtime = null;
        try
        {
            var root = Path.GetDirectoryName(typeof(ChatPane).Assembly.Location)!;
            var path = Path.GetFullPath(Path.Combine(root, "..", "core", "ChatCore.dll"));
            _loadContext ??= new CopilotAssemblyLoadContext(path);
            var assembly = _loadContext.LoadFromAssemblyPath(path);
            runtime = (IChatRuntime)Activator.CreateInstance(assembly.GetType("ChatCore.ChatRuntime", true)!, _log)!;
            await runtime.InitializeAsync(_debugger);
            _runtime = runtime;
            runtime.Changed += SendSnapshot;
            SendSnapshot(runtime.Snapshot);
        }
        catch (Exception exception)
        {
            _log.Log(ChatLogLevel.Error, nameof(ChatPane), "Copilot connection failed", exception);
            if (runtime is not null)
            {
                runtime.Changed -= SendSnapshot;
                await runtime.DisposeAsync();
            }
            throw;
        }
        finally { _connecting = false; }
    }

    private void SendSnapshot(ChatSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SendSnapshot(snapshot)); return; }
        if (_runtime is not null) Post("snapshot", snapshot);
    }

    internal static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (SystemException) { return false; }
    }

    private void SendTheme(bool force = false)
    {
        var theme = _theme.CurrentTheme.BaseTheme == BaseTheme.Dark ? "dark" : "light";
        if (!force && theme == _lastTheme) return;
        _lastTheme = theme;
        Post("theme", new { theme });
    }

    private void Post(string type, object payload)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Post(type, payload)); return; }
        if (_browser.CoreWebView2 is null) return;
        _browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { version = 1, sequence = ++_sequence, type, payload }, Json));
    }
}
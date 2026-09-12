using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Contracts;
using DbgX.Interfaces;
using DbgX.Interfaces.Services;
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
    private readonly WebView2 _browser = new();
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
        IDbgOutputEvents output, IDbgTargetState target, IDbgThemeService theme)
    {
        _debugger = new DebuggerAdapter(engineContext, console, output, target);
        _theme = theme;
        Children.Add(_browser);
        Loaded += OnLoaded;
        _themeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => SendTheme(), Dispatcher);
        Unloaded += async (_, _) =>
        {
            _themeTimer.Stop();
            try { if (_runtime is not null) await _runtime.CancelAsync(); } catch { }
        };
        Application.Current.Exit += async (_, _) =>
        {
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
            CoreWebView2Environment.SetLoaderDllFolderPath(Path.Combine(root, "runtimes", "win-x64", "native"));
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
            Children.Clear();
            Children.Add(new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12) });
        }
    }

    private sealed record BridgeRequest(int Version, string Type, string RequestId, string? SessionId,
        string? Text, string? Model, string? Mode, string? ApprovalId, bool? Approved,
        ChatAttachment[]? Attachments, string? HistoryId);

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
            switch (request.Type)
            {
                case "send": await runtime.SendAsync(request.Text ?? "", request.Attachments); break;
                case "cancel": await runtime.CancelAsync(); break;
                case "new": await runtime.NewChatAsync(request.Model); break;
                case "resume": await runtime.ResumeAsync(request.HistoryId ?? ""); Post("sessionChanged", new { }); break;
                case "rename": await runtime.RenameAsync(request.HistoryId ?? "", request.Text ?? ""); break;
                case "delete": await runtime.DeleteAsync(request.HistoryId ?? ""); break;
                case "command": await runtime.RunCommandAsync(request.Text ?? ""); break;
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
        catch (Exception exception) { Post("error", new { message = exception.Message }); }
    }

    private async Task SignInAsync()
    {
        if (_connecting || _runtime?.Snapshot.Busy == true) throw new InvalidOperationException("Wait for the current operation before switching accounts.");
        if (MessageBox.Show("The current chat will be saved and disconnected. Follow the CLI sign-in flow in your browser. Select 'Use a different account' on GitHub to switch accounts. Continue?",
            "Switch Copilot account", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Post("signInCancelled", new { });
            return;
        }
        _connecting = true;
        Post("connecting", new { });
        try
        {
            var runtime = _runtime;
            if (runtime is not null)
            {
                runtime.Changed -= SendSnapshot;
                await runtime.DisposeAsync();
                _runtime = null;
            }
            Post("disconnected", new { });
            Post("connecting", new { });
            var root = Path.GetDirectoryName(typeof(ChatPane).Assembly.Location)!;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDbgCopilotChat", "runtime");
            Directory.CreateDirectory(directory);
            var start = CreateSignInStartInfo(Path.GetFullPath(Path.Combine(root, "..", "core", "runtimes", "win-x64", "native", "copilot.exe")), directory);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start Copilot sign-in.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Copilot sign-in did not complete. Connect again or retry sign-in.");
        }
        finally { _connecting = false; }
        await ConnectAsync();
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
            runtime = (IChatRuntime)Activator.CreateInstance(assembly.GetType("ChatCore.ChatRuntime", true)!)!;
            await runtime.InitializeAsync(_debugger);
            _runtime = runtime;
            runtime.Changed += SendSnapshot;
            SendSnapshot(runtime.Snapshot);
        }
        catch
        {
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
        if (_runtime is not null) Post("snapshot", _runtime.Snapshot);
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
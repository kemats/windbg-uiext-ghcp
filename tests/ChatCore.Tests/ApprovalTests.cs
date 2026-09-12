using ChatCore;
using Contracts;
using Xunit;

public sealed class ApprovalTests
{
    private static readonly TargetInfo Target = new(true);

    public class InterfaceProxy : System.Reflection.DispatchProxy
    {
        public Func<System.Reflection.MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? arguments) => Handler(method!, arguments);
        public static T Create<T>(Func<System.Reflection.MethodInfo, object?[]?, object?> handler) where T : class
        {
            var proxy = Create<T, InterfaceProxy>();
            ((InterfaceProxy)(object)proxy).Handler = handler;
            return proxy;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DebuggerOutputWaitsForCommandAndPreservesFullText(bool cancel)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<DbgX.Interfaces.Services.TextOutputEventArgs>? capture = null;
        var running = DbgX.Interfaces.Structs.RunningState.Stopped;
        var context = InterfaceProxy.Create<DbgX.Interfaces.Services.IDbgEngineSynchronizationContextSource>((_, _) => new SynchronizationContext());
        var console = InterfaceProxy.Create<DbgX.Interfaces.Services.IDbgConsole>((method, _) =>
        {
            Assert.Equal("ExecuteCommandAsync", method.Name);
            entered.TrySetResult();
            return finished.Task;
        });
        var output = InterfaceProxy.Create<DbgX.Interfaces.Services.IDbgOutputEvents>((method, arguments) =>
        {
            if (method.Name == "add_OnDmlOutput") capture += (EventHandler<DbgX.Interfaces.Services.TextOutputEventArgs>)arguments![0]!;
            if (method.Name == "remove_OnDmlOutput") capture -= (EventHandler<DbgX.Interfaces.Services.TextOutputEventArgs>)arguments![0]!;
            return null;
        });
        var state = InterfaceProxy.Create<DbgX.Interfaces.Services.IDbgTargetState>((_, _) => running);
        var adapter = new WinDbgChatView.DebuggerAdapter(context, console, output, state);
        var target = await adapter.GetTargetAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var execution = adapter.ExecuteAsync("k", target, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            running = DbgX.Interfaces.Structs.RunningState.Running;
            running = DbgX.Interfaces.Structs.RunningState.Stopped;
            var fullOutput = new string('x', 150_000) + "Final line";
            capture!(null, new(fullOutput));
            if (cancel) cancellation.Cancel();
            Assert.False(execution.IsCompleted);
            finished.TrySetResult();
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            else Assert.Equal(fullOutput + Environment.NewLine, await execution);
            Assert.Null(capture);
            Assert.Equal(target, await adapter.GetTargetAsync(CancellationToken.None));
            running = DbgX.Interfaces.Structs.RunningState.NoTarget;
            Assert.False((await adapter.GetTargetAsync(CancellationToken.None)).Available);
        }
        finally { finished.TrySetResult(); }
    }

    [Fact]
    public void AttachmentsProduceInlineImagesAndUntrustedTextWithoutFilePaths()
    {
        var text = new ChatAttachment("trace.log", "text/plain", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello\n世界")));
        var png = new ChatAttachment("capture.png", "image/png", "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        var prepared = AttachmentInput.Prepare("Analyze", [text, png], true);
        Assert.Contains("untrusted data", prepared.Message.Prompt);
        Assert.Contains("trace.log", prepared.Message.Prompt);
        var image = Assert.IsType<GitHub.Copilot.AttachmentBlob>(Assert.Single(prepared.Message.Attachments!));
        Assert.Equal(png.Data, image.Data);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(2, prepared.Files.Length);
        Assert.Equal("Analyze the attached files.", AttachmentInput.Prepare("", [png], true).Message.Prompt);
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("", [png], false));
    }

    [Fact]
    public void SessionHistoryRoundTripsRenamesDeletesAndRejectsPaths()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copilot-history-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionStore(directory);
            var id = Guid.NewGuid().ToString("N");
            store.Save(new(new(id, "Test", DateTimeOffset.UtcNow), "model", [new("message", "reasoning", "SDK event", true)], null, []));
            Assert.Equal("SDK event", Assert.Single(new SessionStore(directory).Load(id).Messages).Text);
            Assert.False(store.Load(id).HasConversation);
            var conversation = store.Load(id) with { Messages = [new("user", "user", "Hello", true)] };
            Assert.True(conversation.HasConversation);
            store.Rename(id, "Renamed");
            Assert.Equal("Renamed", Assert.Single(store.List()).Title);
            Assert.True(new SessionStore(directory).Load(id).TitleManuallySet);
            Assert.Throws<ArgumentException>(() => store.Rename(id, "bad\nname"));
            Assert.Throws<ArgumentException>(() => store.Delete("../secret"));
            Assert.Throws<ArgumentException>(() => store.Load("../secret"));
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "broken");
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "{}");
            Assert.Single(store.List());
            store.Delete(id);
            Assert.Empty(store.List());
            Assert.Empty(System.IO.Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void AttachmentsRejectInvalidEncodingBinaryPathsAndOversizedData()
    {
        var text = new ChatAttachment("log.txt", "text/plain", Convert.ToBase64String("ok"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("", [], null));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", Enumerable.Repeat(text, 6).ToArray(), null));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", [text with { Name = "../secret" }], null));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", [text with { Data = "not base64" }], null));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", [text with { Data = "AA==" }], null));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", [text with { Data = "/w==" }], null));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", [text with { MimeType = "image/png" }], true));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", [text with { Data = Convert.ToBase64String(new byte[AttachmentInput.MaximumTextBytes + 1]) }], null));
        var imageBytes = new byte[AttachmentInput.MaximumImageBytes];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(imageBytes, 0);
        var image = new ChatAttachment("test.png", "image/png", Convert.ToBase64String(imageBytes));
        Assert.Throws<ArgumentException>(() => AttachmentInput.Prepare("prompt", [image, image, image], true));
        var utf16 = new ChatAttachment("utf16.log", "text/plain", Convert.ToBase64String([0xff, 0xfe, 0x41, 0x00]));
        Assert.Contains("A", AttachmentInput.Prepare("prompt", [utf16], null).Message.Prompt);
    }

    [Fact]
    public void UsageAccumulatesCallsOnceAndPreservesMissingBilling()
    {
        var usage = new UsageTracker();
        usage.BeginTurn("first", DateTimeOffset.UtcNow);
        var call = new GitHub.Copilot.AssistantUsageData
        {
            Model = "model-a", InputTokens = 100, OutputTokens = 20,
            CopilotUsage = new() { TotalNanoAiu = 1_500_000_000 }
        };
        usage.RecordUsage("event-1", call);
        usage.RecordUsage("event-1", call);
        usage.RecordUsage("event-2", new() { Model = "model-b", InputTokens = 40 });
        var turn = Assert.Single(usage.Turns);
        Assert.Equal(1_500_000_000d, turn.NanoAiu);
        Assert.Equal(140d, turn.InputTokens);
        Assert.Equal(20d, turn.OutputTokens);
        Assert.Equal(2, turn.UsageCalls);
        Assert.Equal(1, turn.UnreportedCostCalls);
        Assert.Equal(new[] { "model-a", "model-b" }, turn.Models);
        usage.CompleteTurn();
        Assert.NotNull(Assert.Single(usage.Turns).CompletedAt);
        usage.RecordUsage("late", new() { Model = "model-a", CopilotUsage = new() { TotalNanoAiu = 100 } });
        Assert.Equal(1_500_000_100d, usage.Info.NanoAiu);
        Assert.Equal(1_500_000_000d, Assert.Single(usage.Turns).NanoAiu);
    }

    [Fact]
    public async Task SdkTitleUpdatesWhileIdleButDoesNotOverrideManualNames()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copilot-title-test-" + Guid.NewGuid().ToString("N"));
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var onEvent = typeof(ChatRuntime).GetMethod("OnEvent", flags)!;
        var identity = runtime.Snapshot.SessionId;
        void Title(string name, string? session = null) => onEvent.Invoke(runtime, [session ?? identity,
            new GitHub.Copilot.SessionTitleChangedEvent { Data = new() { Title = name } }]);
        try
        {
            Title("Heap investigation");
            Assert.Equal("Heap investigation", runtime.Snapshot.SessionTitle);
            Title("Wrong session", "old-session");
            Title("\n\t");
            Assert.Equal("Heap investigation", runtime.Snapshot.SessionTitle);
            var store = new SessionStore(directory);
            store.Save(new(new(identity, "Heap investigation", DateTimeOffset.UtcNow), "auto", [], null, []));
            typeof(ChatRuntime).GetField("_store", flags)!.SetValue(runtime, store);
            await runtime.RenameAsync(identity, "My investigation");
            Title("Late generated title");
            Assert.Equal("My investigation", runtime.Snapshot.SessionTitle);
            Assert.True(store.Load(identity).TitleManuallySet);
            Assert.Equal("My investigation", store.Load(identity).Entry.Title);
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ClickedCommandRunsWithoutChatApprovalOrTranscriptOutput()
    {
        await using var runtime = new ChatRuntime();
        var debugger = new FakeDebugger();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ChatRuntime).GetField("_debugger", flags)!.SetValue(runtime, debugger);
        var run = typeof(ChatRuntime).GetMethod("RunLocalCommandAsync", flags)!;
        await ((Task)run.Invoke(runtime, ["k", CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, debugger.Executions);
        Assert.Empty(runtime.Snapshot.Approvals);
        Assert.Empty(runtime.Snapshot.Messages);
        debugger.Current = debugger.Current with { Available = false };
        await (Task)run.Invoke(runtime, ["k", CancellationToken.None])!;
        Assert.Equal(1, debugger.Executions);
        Assert.Contains("No stopped target", runtime.Snapshot.Error);
    }

    [Fact]
    public async Task SdkToolInvocationKeepsOutputAndCompletionInOneActivity()
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ChatRuntime).GetField("_debugger", flags)!.SetValue(runtime, new FakeDebugger());
        typeof(ChatRuntime).GetField("_target", flags)!.SetValue(runtime, Target);
        typeof(ChatRuntime).GetField("_busy", flags)!.SetValue(runtime, true);
        typeof(ChatRuntime).GetField("_cancellation", flags)!.SetValue(runtime, new CancellationTokenSource());
        var config = (GitHub.Copilot.SessionConfig)typeof(ChatRuntime).GetMethod("Configure", flags)!
            .MakeGenericMethod(typeof(GitHub.Copilot.SessionConfig)).Invoke(runtime, [new GitHub.Copilot.SessionConfig()])!;
        var tool = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(Assert.Single(config.Tools!, item => item.Name == "debugger_command"));
        Assert.DoesNotContain("invocation", tool.JsonSchema.ToString(), StringComparison.OrdinalIgnoreCase);
        var identity = runtime.Snapshot.SessionId;
        var onEvent = typeof(ChatRuntime).GetMethod("OnEvent", flags)!;
        void Emit(GitHub.Copilot.SessionEvent message) => onEvent.Invoke(runtime, [identity, message]);
        Emit(new GitHub.Copilot.ToolExecutionStartEvent { Data = new() { ToolCallId = "command-call", ToolName = "debugger_command" } });
        var invocation = new GitHub.Copilot.ToolInvocation { SessionId = identity, ToolCallId = "command-call", ToolName = "debugger_command" };
        var arguments = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["command"] = "k",
            Context = new Dictionary<object, object?> { [typeof(GitHub.Copilot.ToolInvocation)] = invocation }
        };
        var pending = tool.InvokeAsync(arguments).AsTask();
        var execute = Assert.Single(runtime.Snapshot.Approvals);
        Assert.Equal("execute", execute.Kind);
        runtime.ResolveApproval(identity, execute.Id, true);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!runtime.Snapshot.Approvals.Any(item => item.Kind == "share") && DateTime.UtcNow < deadline)
            await Task.Yield();
        var activity = Assert.Single(runtime.Snapshot.Messages);
        Assert.Equal("tool-command-call", activity.Id);
        Assert.Equal("sensitive-output", activity.Text);
        Assert.False(activity.Complete);
        Assert.Equal("Awaiting output approval", activity.ActivityStatus);
        var approval = Assert.Single(runtime.Snapshot.Approvals);
        runtime.ResolveApproval(identity, approval.Id, false);
        Assert.Equal("User withheld debugger output.", Assert.IsType<System.Text.Json.JsonElement>(await pending).GetString());
        Emit(new GitHub.Copilot.ToolExecutionCompleteEvent { Data = new() { ToolCallId = "command-call", Success = true, Result = new() { Content = "User withheld debugger output." } } });
        Emit(new GitHub.Copilot.ToolExecutionStartEvent { Data = new() { ToolCallId = "command-call", ToolName = "debugger_command" } });
        Emit(new GitHub.Copilot.ToolExecutionProgressEvent { Data = new() { ToolCallId = "command-call", ProgressMessage = "Late progress" } });
        activity = Assert.Single(runtime.Snapshot.Messages);
        Assert.Equal("sensitive-output\n\nUser withheld debugger output.", activity.Text);
        Assert.True(activity.Complete);
        Assert.Equal("Completed", activity.ActivityStatus);
    }

    [Fact]
    public async Task SdkActivityEventsPreserveIdentityAndReplaceReasoningDeltas()
    {
        var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(ChatRuntime).GetField("_busy", flags)!.SetValue(runtime, true);
        var onEvent = typeof(ChatRuntime).GetMethod("OnEvent", flags)!;
        var sessionId = runtime.Snapshot.SessionId;
        void Emit(GitHub.Copilot.SessionEvent message, string? identity = null) => onEvent.Invoke(runtime, [identity ?? sessionId, message]);
        Emit(new GitHub.Copilot.AssistantReasoningDeltaEvent { Data = new() { ReasoningId = "reason", DeltaContent = "Partial" } });
        Emit(new GitHub.Copilot.AssistantReasoningEvent { Data = new() { ReasoningId = "reason", Content = "Published reasoning" } });
        Emit(new GitHub.Copilot.AssistantReasoningEvent { Data = new() { ReasoningId = "stale", Content = "Must not appear" } }, "old-session");
        var reasoning = Assert.Single(runtime.Snapshot.Messages);
        Assert.Equal("reasoning", reasoning.Role);
        Assert.Equal("Published reasoning", reasoning.Text);
        Assert.True(reasoning.Complete);
        Emit(new GitHub.Copilot.ToolExecutionStartEvent { Data = new() { ToolCallId = "call", ToolName = "debugger_target" } });
        var fullOutput = "Target metadata" + new string('x', 150_000) + "End of output";
        Emit(new GitHub.Copilot.ToolExecutionCompleteEvent { Data = new() { ToolCallId = "call", Success = true, Result = new() { Content = fullOutput } } });
        var tool = Assert.Single(runtime.Snapshot.Messages, message => message.Role == "tool");
        Assert.Equal("debugger_target", tool.Title);
        Assert.Contains(fullOutput, tool.Text);
        Assert.True(tool.Complete);
        Emit(new GitHub.Copilot.ToolExecutionStartEvent { Data = new() { ToolCallId = "unfinished", ToolName = "debugger_command" } });
        Assert.False(runtime.Snapshot.Messages.Single(message => message.Id == "tool-unfinished").Complete);
        typeof(ChatRuntime).GetMethod("CompleteAssistant", flags)!.Invoke(runtime, null);
        Assert.Equal("Interrupted", runtime.Snapshot.Messages.Single(message => message.Id == "tool-unfinished").ActivityStatus);
        typeof(ChatRuntime).GetField("_busy", flags)!.SetValue(runtime, false);
        await runtime.DisposeAsync();
    }

    [Fact]
    public void UsagePreservesUnknownValuesAndResetsWithSession()
    {
        var usage = new UsageTracker();
        usage.BeginTurn("first", DateTimeOffset.UtcNow);
        usage.RecordUsage("missing", new() { Model = "model-a" });
        usage.RecordUsage("negative", new() { Model = "model-a", CopilotUsage = new() { TotalNanoAiu = -1 } });
        Assert.Null(usage.Info.NanoAiu);
        Assert.Equal(2, usage.Info.UnreportedCostCalls);
        usage.RecordUsage("zero", new() { Model = "model-a", CopilotUsage = new() { TotalNanoAiu = 0 } });
        Assert.Equal(0d, usage.Info.NanoAiu);
        Assert.Null(Assert.Single(usage.Turns).InputTokens);
        usage.RecordContext(new() { CurrentTokens = 1200, TokenLimit = 8000, SystemTokens = 400,
            ToolDefinitionsTokens = 300, ConversationTokens = 500, MessagesLength = 4 });
        Assert.Equal(1200d, usage.Info.CurrentTokens);
        usage.Reset();
        Assert.Empty(usage.Turns);
        Assert.Null(usage.CurrentTurnId);
        Assert.Null(usage.Info.CurrentTokens);
        Assert.Null(usage.Info.NanoAiu);
        Assert.Equal(0, usage.Info.UnreportedCostCalls);
    }

    [Fact]
    public void AccountSignInUsesDeviceCodeAndKeepsNormalWindowsEnvironment()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var method = typeof(WinDbgChatView.ChatPane).GetMethod("CreateSignInStartInfo", flags)!;
        var start = Assert.IsType<System.Diagnostics.ProcessStartInfo>(method.Invoke(null, ["copilot.exe", "C:\\temp"]));
        Assert.EndsWith("\\WindowsPowerShell\\v1.0\\powershell.exe", start.FileName);
        Assert.Equal(new[] { "-NoLogo", "-NoProfile", "-Command",
            "& $env:WINDBG_COPILOT_SIGNIN_CLI login --device-code 2>&1 | ForEach-Object { $_.ToString() } | Out-Host; exit $LASTEXITCODE" }, start.ArgumentList);
        Assert.Equal("copilot.exe", start.Environment["WINDBG_COPILOT_SIGNIN_CLI"]);
        Assert.False(start.UseShellExecute);
        Assert.False(start.RedirectStandardOutput);
        Assert.Equal("C:\\temp", start.Environment["COPILOT_HOME"]);
        Assert.Equal(Environment.GetEnvironmentVariable("SystemRoot"), start.Environment["SystemRoot"]);
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }) Assert.False(start.Environment.ContainsKey(name));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task SignInConsoleForwardsCliOutputAndExitCode(int exitCode)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copilot console & test " + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var cli = System.IO.Path.Combine(directory, "fake cli.cmd");
            await System.IO.File.WriteAllTextAsync(cli,
                $"@echo off\r\necho Simulated authorization instructions\r\necho arguments: %1 %2\r\necho Simulated diagnostic 1>&2\r\nexit /b {exitCode}\r\n");
            var method = typeof(WinDbgChatView.ChatPane).GetMethod("CreateSignInStartInfo",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var start = (System.Diagnostics.ProcessStartInfo)method.Invoke(null, [cli, directory])!;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.CreateNoWindow = true;
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            var text = await output;
            Assert.Empty(await error);
            Assert.Contains("Simulated authorization instructions", text);
            Assert.Contains("arguments: login --device-code", text);
            Assert.Contains("Simulated diagnostic", text);
            Assert.DoesNotContain("NativeCommandError", text);
            Assert.DoesNotContain("CategoryInfo", text);
            Assert.DoesNotContain("FullyQualifiedErrorId", text);
            Assert.Equal(exitCode, process.ExitCode);
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void CliEnvironmentPreservesWindowsVariablesWithoutTokenOverrides()
    {
        var factory = typeof(ChatRuntime).GetMethod("CreateCliEnvironment",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var environment = Assert.IsType<Dictionary<string, string>>(factory.Invoke(null, null));
        foreach (var name in new[] { "SystemRoot", "PATH", "TEMP", "USERPROFILE" })
        {
            Assert.False(string.IsNullOrEmpty(environment[name]));
            Assert.Equal(Environment.GetEnvironmentVariable(name), environment[name]);
        }
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            Assert.Equal("", environment[name]);
    }

    [Fact]
    public void DockingExportRetainsItsPersistedName()
    {
        using var catalog = new System.ComponentModel.Composition.Hosting.TypeCatalog(typeof(WinDbgChatView.ChatExtension), typeof(WinDbgChatView.ChatRibbon));
        var export = Assert.Single(catalog.Parts.SelectMany(part => part.ExportDefinitions),
            item => item.ContractName == System.ComponentModel.Composition.AttributedModelServices.GetContractName(typeof(DbgX.Interfaces.IDbgToolWindow)));
        Assert.Equal("OssCopilotChat", Assert.IsType<string>(export.Metadata["Name"]));
        var ribbon = Assert.Single(catalog.Parts.SelectMany(part => part.ExportDefinitions),
            item => item.ContractName == System.ComponentModel.Composition.AttributedModelServices.GetContractName(typeof(DbgX.Interfaces.IDbgRibbonTab)));
        Assert.Equal("OssCopilotRibbonTab", Assert.IsType<string>(ribbon.Metadata["Name"]));
        Assert.Equal(int.MinValue, Convert.ToInt32(ribbon.Metadata["Order"]));
    }

    [Fact]
    public void MissingUiStillReturnsValidDockingContent()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var extension = new WinDbgChatView.ChatExtension();
                var ribbon = new WinDbgChatView.ChatRibbon();
                var tab = Assert.IsType<Fluent.RibbonTabItem>(ribbon.Tab);
                Assert.Equal("Copilot", tab.Header);
                var ribbonGroup = Assert.Single(tab.Groups);
                var chatButton = Assert.IsType<Fluent.Button>(Assert.Single(ribbonGroup.Items.Cast<object>()));
                Assert.Equal("Chat", chatButton.Header);
                Assert.Same(tab, ribbon.Tab);
                var view = extension.GetToolWindowView(null!);
                var control = Assert.IsType<DbgX.Interfaces.UI.ToolWindowView>(view);
                Assert.IsType<System.Windows.Controls.TextBlock>(control.Content);
                var title = DbgX.Interfaces.UI.ToolWindowView.GetTabTitle(view);
                Assert.NotNull(title);
                Assert.Equal("Copilot Chat", title.Title);
                Assert.True(DbgX.Interfaces.UI.ToolWindowView.GetIsWindowPersisted(view));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void BootstrapDoesNotReferenceUiOrWebView()
    {
        var references = typeof(WinDbgChatView.ChatExtension).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, name => name.Name == "WinDbgCopilot.UI"
            || name.Name!.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal));
    }

    [Fact]
    public void IsolatedUiUsesPrivateWebViewAndSharedHostTypes()
    {
        var path = typeof(WinDbgChatView.ChatPane).Assembly.Location;
        var context = new WinDbgChatView.UiAssemblyLoadContext(path);
        var ui = context.LoadFromAssemblyPath(path);
        var pane = ui.GetType("WinDbgChatView.ChatPane", true)!;
        Assert.True(typeof(System.Windows.FrameworkElement).IsAssignableFrom(pane));
        Assert.NotSame(typeof(WinDbgChatView.ChatPane).Assembly, ui);
        foreach (var shared in new[] { typeof(IChatRuntime).Assembly, typeof(DbgX.Interfaces.Services.IDbgConsole).Assembly })
            Assert.Same(shared, context.LoadFromAssemblyName(shared.GetName()));
        foreach (var hostAssembly in new[] { typeof(Microsoft.Web.WebView2.Core.CoreWebView2Environment).Assembly,
            typeof(Microsoft.Web.WebView2.Wpf.WebView2).Assembly })
        {
            var isolated = context.LoadFromAssemblyName(hostAssembly.GetName());
            Assert.NotSame(hostAssembly, isolated);
            Assert.Same(context, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(isolated));
        }
    }

    [Fact]
    public async Task ApprovalIsBoundToOneRequestAndCannotBeReplayed()
    {
        var gate = new ApprovalGate();
        var pending = gate.RequestAsync("execute", "k", CancellationToken.None);
        Assert.False(pending.IsCompleted);
        var request = Assert.Single(gate.Pending);
        Assert.False(gate.Resolve("forged", true));
        Assert.True(gate.Resolve(request.Id, true));
        Assert.False(gate.Resolve(request.Id, true));
        Assert.True(await pending);
        Assert.Empty(gate.Pending);
    }

    [Fact]
    public async Task CancellationDeniesPendingOutputSharing()
    {
        var gate = new ApprovalGate();
        using var cancellation = new CancellationTokenSource();
        var pending = gate.RequestAsync("share", "private output", cancellation.Token);
        cancellation.Cancel();
        Assert.False(await pending);
        Assert.Empty(gate.Pending);
    }

    [Fact]
    public async Task ResetDeniesAllWaiters()
    {
        var gate = new ApprovalGate();
        var first = gate.RequestAsync("execute", "k", CancellationToken.None);
        var second = gate.RequestAsync("share", "result", CancellationToken.None);
        gate.CancelAll();
        Assert.False(await first);
        Assert.False(await second);
        Assert.Empty(gate.Pending);
    }

    private sealed class FakeDebugger : IDebuggerAdapter
    {
        public TargetInfo Current { get; set; } = Target;
        public int Executions { get; private set; }
        public Action? OnExecute { get; set; }
        public Action? OnGetDetails { get; set; }
        public Task<TargetInfo> GetTargetAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task<string> GetTargetDetailsAsync(CancellationToken cancellationToken)
        {
            OnGetDetails?.Invoke();
            return Task.FromResult("target-details");
        }
        public Task<string> ExecuteAsync(string command, TargetInfo expectedTarget, CancellationToken cancellationToken)
        {
            Executions++;
            OnExecute?.Invoke();
            return Task.FromResult("sensitive-output");
        }
    }

    [Fact]
    public async Task DeniedToolNeverTouchesDebugger()
    {
        var debugger = new FakeDebugger();
        var gate = new ApprovalGate();
        var call = new DebuggerTool(debugger, gate).ExecuteAsync("k", Target, ApprovalMode.AskEveryTime, CancellationToken.None);
        gate.Resolve(Assert.Single(gate.Pending).Id, false);
        Assert.Equal("Command execution denied.", await call);
        Assert.Equal(0, debugger.Executions);
    }

    [Fact]
    public async Task AutoExecutionAlsoApprovesOutputConsent()
    {
        var debugger = new FakeDebugger();
        var gate = new ApprovalGate();
        var call = new DebuggerTool(debugger, gate).ExecuteAsync("k", Target, ApprovalMode.ApproveAll, CancellationToken.None);
        Assert.Equal(1, debugger.Executions);
        Assert.Equal("sensitive-output", await call);
        Assert.Empty(gate.Pending);
    }

    [Fact]
    public async Task ReturningToAskModeDuringExecutionRequiresSharingConsent()
    {
        var mode = ApprovalMode.ApproveAll;
        var debugger = new FakeDebugger { OnExecute = () => mode = ApprovalMode.AskEveryTime };
        var gate = new ApprovalGate();
        var call = new DebuggerTool(debugger, gate, getMode: () => mode).ExecuteAsync("k", Target, mode, CancellationToken.None);
        var pending = Assert.Single(gate.Pending);
        Assert.Equal("share", pending.Kind);
        gate.Resolve(pending.Id, false);
        Assert.DoesNotContain("sensitive-output", await call);
    }

    [Fact]
    public async Task TargetInformationNeedsNoConsent()
    {
        var debugger = new FakeDebugger();
        var gate = new ApprovalGate();
        var tool = new DebuggerTool(debugger, gate);
        Assert.Equal("target-details", await tool.GetTargetDetailsAsync(ApprovalMode.AskEveryTime, CancellationToken.None));
        Assert.Equal("target-details", await tool.GetTargetDetailsAsync(ApprovalMode.ApproveAll, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.GetTargetDetailsAsync(ApprovalMode.AskEveryTime, new CancellationToken(true)));
        Assert.Empty(gate.Pending);
        Assert.Equal(0, debugger.Executions);
    }

    [Fact]
    public async Task MissingTargetWhileAwaitingExecutionApprovalPreventsExecution()
    {
        var debugger = new FakeDebugger();
        var gate = new ApprovalGate();
        var call = new DebuggerTool(debugger, gate).ExecuteAsync("k", Target, ApprovalMode.AskEveryTime, CancellationToken.None);
        debugger.Current = new(false);
        gate.Resolve(Assert.Single(gate.Pending).Id, true);
        Assert.Contains("No stopped debug target", await call);
        Assert.Equal(0, debugger.Executions);
    }

    [Fact]
    public async Task IsolatedCoreSharesOnlyTheContractIdentity()
    {
        var path = typeof(ChatRuntime).Assembly.Location;
        var context = new WinDbgChatView.CopilotAssemblyLoadContext(path);
        var assembly = context.LoadFromAssemblyPath(path);
        var instance = Activator.CreateInstance(assembly.GetType("ChatCore.ChatRuntime", true)!);
        var runtime = Assert.IsAssignableFrom<IChatRuntime>(instance);
        Assert.NotSame(typeof(ChatRuntime).Assembly, runtime.GetType().Assembly);
        Assert.Same(context, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(runtime.GetType().Assembly));
        Assert.Equal(ApprovalMode.AskEveryTime, runtime.Snapshot.Mode);
        Assert.False(runtime.ResolveApproval("stale-session", "forged", true));
        await runtime.DisposeAsync();
    }
}
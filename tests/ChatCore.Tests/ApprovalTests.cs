using ChatCore;
using Contracts;
using System.Runtime.InteropServices;
using Xunit;

public sealed class ApprovalTests
{
    private static readonly TargetInfo Target = new(true);

    #pragma warning disable GHCP001
    [Theory]
    [InlineData(null, true, false, true)]
    [InlineData("*", true, false, true)]
    [InlineData("read", true, false, true)]
    [InlineData("other", true, false, false)]
    [InlineData("", true, false, false)]
    [InlineData("*", false, false, false)]
    [InlineData("*", true, true, false)]
    public async Task McpCatalogDetectsMissingToolsWithoutIgnoringConfiguredFilters(string? filter, bool enabled, bool present, bool expected)
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var definitions = (Dictionary<string, GitHub.Copilot.McpServerConfig>)typeof(ChatRuntime).GetField("_mcpDefinitions", flags)!.GetValue(runtime)!;
        definitions["server"] = new GitHub.Copilot.McpHttpServerConfig { Url = "https://example.com/mcp", Tools = filter is null ? null : filter.Length == 0 ? [] : [filter] };
        if (enabled) ((HashSet<string>)typeof(ChatRuntime).GetField("_enabledServers", flags)!.GetValue(runtime)!).Add("server");
        var metadata = new List<GitHub.Copilot.Rpc.CurrentToolMetadata>();
        if (present) metadata.Add(new() { Name = "server-read", McpServerName = "server", McpToolName = "read" });
        var missing = Assert.IsType<string[]>(typeof(ChatRuntime).GetMethod("FindMissingMcpServers", flags)!.Invoke(runtime,
            [metadata, new Dictionary<string, string[]> { ["server"] = ["read"], ["unknown"] = ["read"] }]));
        Assert.Equal(expected ? ["server"] : Array.Empty<string>(), missing);
    }
    #pragma warning restore GHCP001

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    [InlineData("stdio")]
    [InlineData("busy")]
    [InlineData("disconnected")]
    public async Task McpReauthenticationRejectsInvalidRequestsBeforeRpc(string scenario)
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        if (scenario != "disconnected")
            typeof(ChatRuntime).GetField("_client", flags)!.SetValue(runtime, new GitHub.Copilot.CopilotClient(new GitHub.Copilot.CopilotClientOptions()));
        var definitions = (Dictionary<string, GitHub.Copilot.McpServerConfig>)typeof(ChatRuntime).GetField("_mcpDefinitions", flags)!.GetValue(runtime)!;
        if (scenario != "unknown") definitions["server"] = scenario == "stdio"
            ? new GitHub.Copilot.McpStdioServerConfig { Command = "unused" }
            : new GitHub.Copilot.McpHttpServerConfig { Url = "https://example.com/mcp" };
        if (scenario != "disabled") ((HashSet<string>)typeof(ChatRuntime).GetField("_enabledServers", flags)!.GetValue(runtime)!).Add("server");
        typeof(ChatRuntime).GetMethod("ResetMcpSessionState", flags)!.Invoke(runtime, null);
        if (scenario == "busy") typeof(ChatRuntime).GetField("_busy", flags)!.SetValue(runtime, true);
        if (scenario is "busy" or "disconnected") await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.AuthenticateMcpAsync("server", true));
        else await Assert.ThrowsAsync<ArgumentException>(() => runtime.AuthenticateMcpAsync("server", true));
    }

    [Fact]
    public async Task McpReconnectRejectsStaleConnectionAndCoalescesRefreshRequests()
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var definitions = (Dictionary<string, GitHub.Copilot.McpServerConfig>)typeof(ChatRuntime).GetField("_mcpDefinitions", flags)!.GetValue(runtime)!;
        definitions["server"] = new GitHub.Copilot.McpHttpServerConfig { Url = "https://example.com/mcp" };
        ((HashSet<string>)typeof(ChatRuntime).GetField("_enabledServers", flags)!.GetValue(runtime)!).Add("server");
        var reset = typeof(ChatRuntime).GetMethod("ResetMcpSessionState", flags)!;
        var onEvent = typeof(ChatRuntime).GetMethod("OnSessionEvent", flags)!;
        var refresh = typeof(ChatRuntime).GetField("_mcpRefreshRequested", flags)!;
        var identity = runtime.Snapshot.SessionId;
        void Emit(long generation, string status) => onEvent.Invoke(runtime, [generation, identity,
            new GitHub.Copilot.SessionMcpServerStatusChangedEvent { Data = new() { ServerName = "server", Status = new(status) } }]);
        var oldGeneration = (long)reset.Invoke(runtime, null)!;
        Emit(oldGeneration, "connected");
        Assert.True((bool)refresh.GetValue(runtime)!);
        refresh.SetValue(runtime, false);
        Emit(oldGeneration, "connected");
        Assert.False((bool)refresh.GetValue(runtime)!);
        var generation = (long)reset.Invoke(runtime, null)!;
        Emit(oldGeneration, "connected");
        Assert.Equal("pending", Assert.Single(runtime.Snapshot.ToolSettings!.Servers).Status);
        Assert.False((bool)refresh.GetValue(runtime)!);
        Emit(generation, "needs-auth");
        Assert.True(Assert.Single(runtime.Snapshot.ToolSettings!.Servers).CanAuthenticate);
        typeof(ChatRuntime).GetField("_busy", flags)!.SetValue(runtime, true);
        var sessionField = typeof(ChatRuntime).GetField("_session", flags)!;
        var session = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GitHub.Copilot.CopilotSession));
        GC.SuppressFinalize(session);
        sessionField.SetValue(runtime, session);
        try
        {
            Emit(generation, "connected");
            Assert.True((bool)refresh.GetValue(runtime)!);
            Assert.Null(typeof(ChatRuntime).GetField("_mcpRefreshTask", flags)!.GetValue(runtime));
        }
        finally { sessionField.SetValue(runtime, null); }
        refresh.SetValue(runtime, false);
        Emit(generation, "pending");
        Emit(generation, "connected");
        Assert.True((bool)refresh.GetValue(runtime)!);
        Assert.Empty(runtime.Snapshot.Messages);
    }

    [Fact]
    public async Task McpStatusEventsUpdateIdlePickerWithoutStartingAuthentication()
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var definitions = (Dictionary<string, GitHub.Copilot.McpServerConfig>)typeof(ChatRuntime).GetField("_mcpDefinitions", flags)!.GetValue(runtime)!;
        definitions["server"] = new GitHub.Copilot.McpHttpServerConfig { Url = "https://example.com/mcp" };
        var enabled = (HashSet<string>)typeof(ChatRuntime).GetField("_enabledServers", flags)!.GetValue(runtime)!;
        enabled.Add("server");
        var onEvent = typeof(ChatRuntime).GetMethod("OnEvent", flags)!;
        var applySnapshot = typeof(ChatRuntime).GetMethod("ApplyMcpStatusSnapshot", flags)!;
        var statusVersion = typeof(ChatRuntime).GetField("_mcpStatusVersion", flags)!;
        var identity = runtime.Snapshot.SessionId;
        var publications = 0;
        runtime.Changed += _ => publications++;
        void Status(string status, string? session = null, string server = "server") => onEvent.Invoke(runtime, [session ?? identity,
            new GitHub.Copilot.SessionMcpServerStatusChangedEvent { Data = new() { ServerName = server, Status = new(status) } }]);
        Status("pending");
        Assert.Equal("pending", Assert.Single(runtime.Snapshot.ToolSettings!.Servers).Status);
        Assert.False(runtime.Snapshot.Busy);
        var requestedAtVersion = (long)statusVersion.GetValue(runtime)!;
        Status("needs-auth");
        applySnapshot.Invoke(runtime, [new Dictionary<string, string> { ["server"] = "pending" }, requestedAtVersion]);
        Assert.True(Assert.Single(runtime.Snapshot.ToolSettings!.Servers).CanAuthenticate);
        Status("connected", "old-session");
        Assert.Equal("needs-auth", Assert.Single(runtime.Snapshot.ToolSettings!.Servers).Status);
        Status("connected");
        Assert.False(Assert.Single(runtime.Snapshot.ToolSettings!.Servers).CanAuthenticate);
        onEvent.Invoke(runtime, [identity, new GitHub.Copilot.McpOauthRequiredEvent
        { Data = new() { Reason = new("missing-credentials"), RequestId = "request", ServerName = "server", ServerUrl = "https://example.com/mcp" } }]);
        Assert.True(Assert.Single(runtime.Snapshot.ToolSettings!.Servers).CanAuthenticate);
        Status("failed", server: "unknown");
        Assert.Single(runtime.Snapshot.ToolSettings!.Servers);
        enabled.Clear();
        Status("pending");
        Assert.Equal("needs-auth", Assert.Single(runtime.Snapshot.ToolSettings!.Servers).Status);
        applySnapshot.Invoke(runtime, [new Dictionary<string, string> { ["server"] = "pending" }, requestedAtVersion]);
        Assert.Equal("disabled", Assert.Single(runtime.Snapshot.ToolSettings!.Servers).Status);
        Assert.False(Assert.Single(runtime.Snapshot.ToolSettings!.Servers).CanAuthenticate);
        enabled.Add("server");
        applySnapshot.Invoke(runtime, [new Dictionary<string, string> { ["server"] = "connected" }, (long)statusVersion.GetValue(runtime)!]);
        Assert.Equal("connected", Assert.Single(runtime.Snapshot.ToolSettings!.Servers).Status);
        Assert.True(publications >= 4);
        Assert.Empty(runtime.Snapshot.Messages);
    }

    [Fact]
    public async Task McpStatusTransitionsAreLoggedWithoutServerConfigurationValues()
    {
        var log = new RecordingLogSink();
        await using var runtime = new ChatRuntime(log);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var definitions = (Dictionary<string, GitHub.Copilot.McpServerConfig>)typeof(ChatRuntime).GetField("_mcpDefinitions", flags)!.GetValue(runtime)!;
        definitions["server"] = new GitHub.Copilot.McpHttpServerConfig
        {
            Url = "https://secret.example/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret" }
        };
        ((HashSet<string>)typeof(ChatRuntime).GetField("_enabledServers", flags)!.GetValue(runtime)!).Add("server");
        typeof(ChatRuntime).GetMethod("ResetMcpSessionState", flags)!.Invoke(runtime, null);
        typeof(ChatRuntime).GetMethod("UpdateMcpStatus", flags)!.Invoke(runtime, ["server", "needs-auth"]);

        Assert.Contains(log.Entries, entry => entry.Level == ChatLogLevel.Information
            && entry.Message.Contains("server status changed from pending to needs-auth", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("secret.example", StringComparison.Ordinal)
            || entry.Message.Contains("Bearer secret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("needs-auth", true, true, true)]
    [InlineData("connected", true, true, false)]
    [InlineData("pending", true, true, false)]
    [InlineData("failed", true, true, false)]
    [InlineData("not_configured", true, true, false)]
    [InlineData("disabled", false, true, false)]
    [InlineData("needs-auth", false, true, false)]
    [InlineData("needs-auth", true, false, false)]
    public void McpSignInIsAvailableOnlyWhenAuthenticationIsRequired(string status, bool enabled, bool http, bool expected)
    {
        GitHub.Copilot.McpServerConfig definition = http
            ? new GitHub.Copilot.McpHttpServerConfig { Url = "https://example.com/mcp" }
            : new GitHub.Copilot.McpStdioServerConfig { Command = "unused" };
        var method = typeof(ChatRuntime).GetMethod("CreateMcpOption", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var option = Assert.IsType<McpOption>(method.Invoke(null, ["server", enabled, status, definition]));
        Assert.Equal(expected, option.CanAuthenticate);
        Assert.Equal(enabled && http, option.CanReauthenticate);
        Assert.Equal(status, option.Status);
        Assert.Equal(enabled, option.Enabled);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    [InlineData("stdio")]
    [InlineData("busy")]
    [InlineData("disconnected")]
    [InlineData("connected")]
    public async Task McpAuthenticationRejectsInvalidRequestsBeforeRpc(string scenario)
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        if (scenario != "disconnected")
            typeof(ChatRuntime).GetField("_client", flags)!.SetValue(runtime, new GitHub.Copilot.CopilotClient(new GitHub.Copilot.CopilotClientOptions()));
        var definitions = (Dictionary<string, GitHub.Copilot.McpServerConfig>)typeof(ChatRuntime).GetField("_mcpDefinitions", flags)!.GetValue(runtime)!;
        if (scenario != "unknown") definitions["server"] = scenario == "stdio"
            ? new GitHub.Copilot.McpStdioServerConfig { Command = "unused" }
            : new GitHub.Copilot.McpHttpServerConfig { Url = "https://example.com/mcp" };
        if (scenario != "disabled") ((HashSet<string>)typeof(ChatRuntime).GetField("_enabledServers", flags)!.GetValue(runtime)!).Add("server");
        if (scenario == "busy") typeof(ChatRuntime).GetField("_busy", flags)!.SetValue(runtime, true);
        if (scenario is "busy" or "disconnected") await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.AuthenticateMcpAsync("server"));
        else await Assert.ThrowsAsync<ArgumentException>(() => runtime.AuthenticateMcpAsync("server"));
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?state=test", true)]
    [InlineData("http://login.example.com/authorize", false)]
    [InlineData("file:///C:/test.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:password@example.com/", false)]
    [InlineData("https://example.com/\n", false)]
    public void McpAuthenticationOpensOnlySafeHttpsUrls(string url, bool allowed)
    {
        var method = typeof(WinDbgChatView.ChatPane).GetMethod("CreateMcpAuthenticationStartInfo",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        if (!allowed)
        {
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(null, [url]));
            Assert.IsType<ArgumentException>(failure.InnerException);
            return;
        }
        var start = Assert.IsType<System.Diagnostics.ProcessStartInfo>(method.Invoke(null, [url]));
        Assert.True(start.UseShellExecute);
        Assert.Equal(url, start.FileName);
        Assert.Empty(start.ArgumentList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"servers\":{}}")]
    [InlineData("{ invalid JSON being edited")]
    public void McpEditorCreatesMissingFileAndPreservesExistingContents(string? existing)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp editor & test " + Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "settings", "mcp.json");
        try
        {
            if (existing is not null)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.WriteAllText(path, existing);
            }
            var method = typeof(WinDbgChatView.ChatPane).GetMethod("CreateMcpEditorStartInfo",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var start = Assert.IsType<System.Diagnostics.ProcessStartInfo>(method.Invoke(null, [path]));
            Assert.Equal(path, start.FileName);
            Assert.True(start.UseShellExecute);
            Assert.Equal("open", start.Verb);
            Assert.Empty(start.Arguments);
            Assert.Empty(start.ArgumentList);
            if (existing is not null) Assert.Equal(existing, System.IO.File.ReadAllText(path));
            else Assert.Empty(ToolConfiguration.Load(path));
            var contents = System.IO.File.ReadAllText(path);
            method.Invoke(null, [path]);
            Assert.Equal(contents, System.IO.File.ReadAllText(path));
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("success", 0, "build,install,launch")]
    [InlineData("restore", 0, "build,install,launch")]
    [InlineData("build-failure", 1, "build")]
    [InlineData("install-failure", 1, "build,install")]
    [InlineData("running", 1, "")]
    [InlineData("preview", 0, "")]
    public async Task F5WorkflowOrdersStagesAndStopsOnFailure(string scenario, int exitCode, string stages)
    {
        var root = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !System.IO.File.Exists(System.IO.Path.Combine(root.FullName, "scripts", "debug.ps1"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "windbg f5 & test " + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            System.IO.File.Copy(System.IO.Path.Combine(root.FullName, "scripts", "debug.ps1"), System.IO.Path.Combine(directory, "debug.ps1"));
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(directory, "build.ps1"), """
                param($Configuration, $Architecture, [switch]$SkipWebBuild, [switch]$SkipWebRestore, [switch]$NoRestore)
                if ($Configuration -ne 'Debug' -or $SkipWebBuild -or !$NoRestore) { throw 'Incorrect build arguments' }
                if ($SkipWebRestore -ne ($env:F5_TEST_SCENARIO -ne 'restore')) { throw 'Incorrect dependency restore policy' }
                Write-Output 'STAGE:build'
                if ($env:F5_TEST_SCENARIO -eq 'build-failure') { throw 'Simulated build failure' }
                """);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(directory, "install.ps1"), """
                Write-Output 'STAGE:install'
                if ($env:F5_TEST_SCENARIO -eq 'install-failure') { throw 'Simulated install failure' }
                """);
            var runner = System.IO.Path.Combine(directory, "run.ps1");
            await System.IO.File.WriteAllTextAsync(runner, """
                $ErrorActionPreference = 'Stop'
                function Get-Process { param($Name, $ErrorAction) if ($env:F5_TEST_SCENARIO -eq 'running') { 'Existing WinDbg' } }
                function Start-Process {
                    param($FilePath, $WorkingDirectory)
                    if ($FilePath -ne 'WinDbgX.exe') { throw 'Must shell-launch the WinDbgX.exe app execution alias' }
                    Write-Output 'STAGE:launch'
                }
                try {
                    & (Join-Path $PSScriptRoot 'debug.ps1') -NoRestore -RestoreWebDependencies:($env:F5_TEST_SCENARIO -eq 'restore') -WhatIf:($env:F5_TEST_SCENARIO -eq 'preview')
                } catch { Write-Output $_.Exception.Message; exit 1 }
                """);
            var start = new System.Diagnostics.ProcessStartInfo("pwsh")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-File", runner }) start.ArgumentList.Add(argument);
            start.Environment["F5_TEST_SCENARIO"] = scenario;
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var text = await output;
            Assert.True(process.ExitCode == exitCode, text + await error);
            Assert.Equal(stages, string.Join(",", text.Split('\n').Where(line => line.StartsWith("STAGE:")).Select(line => line[6..].Trim())));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    #pragma warning disable GHCP001
    [Theory]
    [InlineData("powershell", "bash")]
    [InlineData("bash", "powershell")]
    public async Task ToolCatalogUsesSessionMetadataAndRemovesUnsupportedSelections(string shell, string absentShell)
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var selected = (HashSet<string>)typeof(ChatRuntime).GetField("_selectedTools", flags)!.GetValue(runtime)!;
        selected.UnionWith(["skill", "ask_user", "agent", "task", shell, absentShell, "view", "obsolete_tool"]);
        var enabled = (HashSet<string>)typeof(ChatRuntime).GetField("_enabledServers", flags)!.GetValue(runtime)!;
        enabled.Add("example");
        var shellTools = new[] { shell, "read_" + shell, "stop_" + shell, "list_" + shell };
        var metadata = new[] { "skill", "ask_user", "agent", "task", "view", "str_replace_editor", "grep", "glob" }.Concat(shellTools)
            .Select(name => new GitHub.Copilot.Rpc.CurrentToolMetadata { Name = name, Description = "Session-specific " + name }).ToList();
        metadata.Add(new() { Name = "example-task", McpServerName = "example", McpToolName = "task" });
        metadata.Add(new() { Name = "disabled-task", McpServerName = "disabled", McpToolName = "task" });
        var update = typeof(ChatRuntime).GetMethod("UpdateToolCatalog", flags)!;
        update.Invoke(runtime, [metadata]);
        var catalog = runtime.Snapshot.ToolSettings!.Tools;
        foreach (var name in new[] { "skill", "ask_user", "agent", "task", "obsolete_tool", "disabled-task", absentShell })
        {
            Assert.DoesNotContain(catalog, tool => tool.Id == name);
            Assert.DoesNotContain(name, selected);
        }
        Assert.Contains(shell, selected);
        foreach (var name in new[] { "view", "str_replace_editor", "grep", "glob", "debugger_command", "debugger_target", "example-task" }.Concat(shellTools))
            Assert.Contains(catalog, tool => tool.Id == name);
        Assert.Equal("Session-specific view", Assert.Single(catalog, tool => tool.Id == "view").Description);
        Assert.Contains("view", selected);
        update.Invoke(runtime, [new List<GitHub.Copilot.Rpc.CurrentToolMetadata> { new() { Name = shell } }]);
        Assert.DoesNotContain(runtime.Snapshot.ToolSettings!.Tools, tool => tool.Id == "view");
        Assert.DoesNotContain("view", selected);
    }
    #pragma warning restore GHCP001

    [Fact]
    public void McpConfigurationReadsVscodeAndSdkFormatsWithoutStartingServers()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"servers":{"local":{"command":"test-mcp","args":["${userHome}"],"env":{"MODE":"test"}},
            "remote":{"type":"http","url":"https://example.com/mcp","tools":["read"],"timeout":10000}}}
            """);
        var servers = ToolConfiguration.Parse(document.RootElement, System.IO.Path.GetTempPath());
        var local = Assert.IsType<GitHub.Copilot.McpStdioServerConfig>(servers["local"]);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Assert.Single(local.Args!));
        Assert.Equal("test", local.Env!["MODE"]);
        Assert.Equal("read", Assert.Single(servers["remote"].Tools!));
        Assert.Equal(10000, servers["remote"].Timeout);
        using var sdk = System.Text.Json.JsonDocument.Parse("""{"mcpServers":{"local":{"type":"local","command":"test"}}}""");
        Assert.Single(ToolConfiguration.Parse(sdk.RootElement, System.IO.Path.GetTempPath()));
    }

    [Theory]
    [InlineData("servers", "\"command\":\"test\"")]
    [InlineData("servers", "\"type\":\"http\",\"url\":\"https://example.com/mcp\"")]
    [InlineData("mcpServers", "\"command\":\"test\"")]
    [InlineData("mcpServers", "\"type\":\"http\",\"url\":\"https://example.com/mcp\"")]
    public void McpConfigurationMakesDefaultToolSelectionExplicit(string format, string transport)
    {
        foreach (var selection in new[] { "", ",\"tools\":[]", ",\"tools\":[\"read\"]", ",\"tools\":[\"*\"]" })
        {
            using var document = System.Text.Json.JsonDocument.Parse($$$$"""{"{{{{format}}}}":{"test":{ {{{{transport}}}}{{{{selection}}}} }}}""");
            var config = Assert.Single(ToolConfiguration.Parse(document.RootElement, System.IO.Path.GetTempPath())).Value;
            string[] expected = selection.Contains("[]") ? [] : selection.Contains("read") ? ["read"] : ["*"];
            Assert.Equal(expected, config.Tools);
        }
    }

    [Theory]
    [InlineData("{\"servers\":{\"test\":{\"command\":\"${input:secret}\"}}}")]
    [InlineData("{\"servers\":{\"test\":{\"type\":\"http\",\"url\":\"file:///secret\"}}}")]
    [InlineData("{\"servers\":{\"test\":{\"type\":\"unknown\"}}}")]
    [InlineData("{}")]
    [InlineData("{\"servers\":{\"test\":{\"command\":\"test\",\"timeout\":0}}}")]
    [InlineData("{\"servers\":{\"test\":{\"command\":\"test\",\"tools\":[null]}}}")]
    [InlineData("{\"servers\":{\"test\":{\"command\":\"test\",\"envFile\":\".env\"}}}")]
    public void McpConfigurationRejectsUnsupportedValues(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => ToolConfiguration.Parse(document.RootElement, System.IO.Path.GetTempPath()));
    }

    [Fact]
    public async Task ToolSelectionGatesExecutionApprovalAndCancellation()
    {
        await using var runtime = new ChatRuntime();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var config = (GitHub.Copilot.SessionConfig)typeof(ChatRuntime).GetMethod("Configure", flags)!
            .MakeGenericMethod(typeof(GitHub.Copilot.SessionConfig)).Invoke(runtime, [new GitHub.Copilot.SessionConfig()])!;
        Assert.Equal(new[] { "debugger_command", "debugger_target" }, config.AvailableTools);
        Assert.Empty(config.McpServers!);
        Assert.Equal(GitHub.Copilot.McpOAuthTokenStorageMode.Persistent, config.McpOAuthTokenStorage);
        var input = new GitHub.Copilot.PreToolUseHookInput { SessionId = runtime.Snapshot.SessionId, ToolName = "view" };
        var invoke = new GitHub.Copilot.HookInvocation();
        Assert.Equal("deny", (await config.Hooks!.OnPreToolUse!(input, invoke))!.PermissionDecision);
        typeof(ChatRuntime).GetField("_busy", flags)!.SetValue(runtime, true);
        var cancellation = new CancellationTokenSource();
        typeof(ChatRuntime).GetField("_cancellation", flags)!.SetValue(runtime, cancellation);
        Assert.Equal("deny", (await config.Hooks.OnPreToolUse(input, invoke))!.PermissionDecision);
        var selected = (HashSet<string>)typeof(ChatRuntime).GetField("_selectedTools", flags)!.GetValue(runtime)!;
        selected.Add("view");
        var pending = config.Hooks.OnPreToolUse(input, invoke);
        var approval = Assert.Single(runtime.Snapshot.Approvals);
        Assert.Equal("tool", approval.Kind);
        runtime.ResolveApproval(runtime.Snapshot.SessionId, approval.Id, false);
        Assert.Equal("deny", (await pending)!.PermissionDecision);
        runtime.SetMode(ApprovalMode.ApproveAll);
        Assert.Equal("allow", (await config.Hooks.OnPreToolUse(input, invoke))!.PermissionDecision);
        selected.Remove("view");
        Assert.Equal("deny", (await config.Hooks.OnPreToolUse(input, invoke))!.PermissionDecision);
        selected.Add("view");
        runtime.SetMode(ApprovalMode.AskEveryTime);
        var cancelled = config.Hooks.OnPreToolUse(input, invoke);
        Assert.Single(runtime.Snapshot.Approvals);
        cancellation.Cancel();
        Assert.Equal("deny", (await cancelled)!.PermissionDecision);
        Assert.Equal("deny", (await config.Hooks.OnPreToolUse(input, invoke))!.PermissionDecision);
    }

    [Fact]
    public void McpConfigurationAllowsJsonCommentsAndTrailingCommas()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "{ // VS Code configuration\n\"servers\": {\"test\": {\"command\": \"test\",},},}");
            Assert.Single(ToolConfiguration.Load(path));
        }
        finally { System.IO.File.Delete(path); }
    }

    [Theory]
    [InlineData(Architecture.X64, "win-x64")]
    [InlineData(Architecture.Arm64, "win-arm64")]
    public void RuntimeAssetsSelectSupportedArchitecture(Architecture architecture, string expected) =>
        Assert.Equal(expected, RuntimeAssets.GetRuntimeIdentifier(architecture));

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

    [Fact]
    public void AccountSignInFindsInstalledCliOnlyInAbsoluteSearchDirectories()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copilot lookup & test " + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var method = typeof(WinDbgChatView.ChatPane).GetMethod("FindInstalledCommand",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            string[] names = ["copilot.exe", "copilot.cmd"];
            Assert.Null(method.Invoke(null, [";.;relative;", names]));
            Assert.Null(method.Invoke(null, [directory, names]));
            var script = System.IO.Path.Combine(directory, "copilot.cmd");
            System.IO.File.WriteAllText(script, "@exit /b 0");
            Assert.Equal(script, method.Invoke(null, [$";.;\"{directory}\";", names]));
            var executable = System.IO.Path.Combine(directory, "copilot.exe");
            System.IO.File.WriteAllText(executable, "not executed");
            Assert.Equal(executable, method.Invoke(null, [directory, names]));
            Assert.Null(method.Invoke(null, [directory, new[] { "winget.exe" }]));
        }
        finally { System.IO.Directory.Delete(directory, true); }
    }

    [Fact]
    public void AccountSignInInstallerUsesOfficialWingetPackageWithoutElevationOrAutoConsent()
    {
        var method = typeof(WinDbgChatView.ChatPane).GetMethod("CreateCopilotInstallStartInfo",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var path = "C:\\Program Files\\WindowsApps\\winget.exe";
        var start = Assert.IsType<System.Diagnostics.ProcessStartInfo>(method.Invoke(null, [path]));
        Assert.Equal(path, start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Empty(start.Verb);
        Assert.False(start.CreateNoWindow);
        Assert.Equal(new[] { "install", "--id", "GitHub.Copilot", "--exact", "--source", "winget", "--scope", "user" }, start.ArgumentList);
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
    public void DbgReporterSinkHonorsLevelAndRoutesSeverity()
    {
        var reporter = new RecordingReporter();
        var type = typeof(WinDbgChatView.ChatExtension).Assembly.GetType("WinDbgChatView.DbgReporterLogSink", true)!;
        var sink = Assert.IsAssignableFrom<IChatLogSink>(Activator.CreateInstance(type,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
            null, [reporter, ChatLogLevel.Warning], null));

        sink.Log(ChatLogLevel.Information, "test", "ignored");
        sink.Log(ChatLogLevel.Warning, "test", "warning");
        var failure = new InvalidOperationException("failure");
        sink.Log(ChatLogLevel.Error, "test", "error", failure);

        Assert.Empty(reporter.Information);
        Assert.Single(reporter.Warnings, "[WinDbgCopilotChat] [test] warning");
        Assert.Single(reporter.Errors, item => item.Message == "[WinDbgCopilotChat] [test] error" && item.Exception == failure);
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
        var instance = Activator.CreateInstance(assembly.GetType("ChatCore.ChatRuntime", true)!, new RecordingLogSink());
        var runtime = Assert.IsAssignableFrom<IChatRuntime>(instance);
        Assert.NotSame(typeof(ChatRuntime).Assembly, runtime.GetType().Assembly);
        Assert.Same(context, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(runtime.GetType().Assembly));
        Assert.Equal(ApprovalMode.AskEveryTime, runtime.Snapshot.Mode);
        Assert.False(runtime.ResolveApproval("stale-session", "forged", true));
        await runtime.DisposeAsync();
    }

    private sealed class RecordingLogSink : IChatLogSink
    {
        public List<(ChatLogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = [];
        public ChatLogLevel MinimumLevel => ChatLogLevel.Debug;
        public void Log(ChatLogLevel level, string category, string message, Exception? exception = null) =>
            Entries.Add((level, category, message, exception));
    }

    private sealed class RecordingReporter : DbgX.Interfaces.Listeners.IDbgReporter
    {
        public List<string> Information { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<(string Message, Exception? Exception)> Errors { get; } = [];
        public void Info(string message) => Information.Add(message);
        public void Warning(string message) => Warnings.Add(message);
        public void Error(bool isFatal, string message) => Errors.Add((message, null));
        public void Error(bool isFatal, Exception exception, string message) => Errors.Add((message, exception));
    }
}
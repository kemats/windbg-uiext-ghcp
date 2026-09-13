using Contracts;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Text.Json;

namespace ChatCore;

public sealed partial class ChatRuntime
{
    private static readonly string SettingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDbgCopilotChat");
    private Dictionary<string, McpServerConfig> _mcpDefinitions = new(StringComparer.Ordinal);
    private HashSet<string> _selectedTools = new(["debugger_command", "debugger_target"], StringComparer.Ordinal);
    private HashSet<string> _enabledServers = new(StringComparer.Ordinal);
    private ToolOption[] _toolCatalog = [
        new("debugger_command", "debugger_command", "WinDbg", "Execute a command on the current target.", true),
        new("debugger_target", "debugger_target", "WinDbg", "Read current target details.", true)];
    private McpOption[] _serverCatalog = [];
    private readonly Dictionary<string, long> _mcpStatusVersions = new(StringComparer.Ordinal);
    private long _mcpStatusVersion;
    private long _sessionEventGeneration;
    private bool _mcpRefreshRequested;
    private Task? _mcpRefreshTask;
    private string? _mcpPath;
    private string? _toolError;
    private bool _toolConfigurationValid = true;
    private sealed record ToolPreferences(string? ConfigPath, string[] Tools, string[] Servers);

    private ToolSettings CurrentToolSettings() => new(
        _toolCatalog.Select(tool => tool with { Selected = _selectedTools.Contains(tool.Id) }).ToArray(),
        _serverCatalog, _mcpPath, _toolError);

    private void LoadToolPreferences()
    {
        try
        {
            var settingsPath = Path.Combine(SettingsDirectory, "tools.json");
            if (File.Exists(settingsPath))
            {
                var saved = JsonSerializer.Deserialize<ToolPreferences>(File.ReadAllText(settingsPath))
                    ?? throw new JsonException("Invalid tool preferences.");
                _mcpPath = saved.ConfigPath;
                _selectedTools = new(saved.Tools, StringComparer.Ordinal);
                _enabledServers = new(saved.Servers, StringComparer.Ordinal);
            }
            _mcpPath ??= new[] { Path.Combine(SettingsDirectory, "mcp.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "mcp.json") }.FirstOrDefault(File.Exists);
            if (_mcpPath is not null) _mcpDefinitions = ToolConfiguration.Load(_mcpPath);
            _enabledServers.IntersectWith(_mcpDefinitions.Keys);
            _serverCatalog = _mcpDefinitions.Keys.Select(name => new McpOption(name, _enabledServers.Contains(name), "Not connected")).ToArray();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            _enabledServers.Clear();
            _toolError = "Could not load tool settings: " + failure.Message;
        }
    }

    private void SaveToolPreferences()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var path = Path.Combine(SettingsDirectory, "tools.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new ToolPreferences(_mcpPath, _selectedTools.ToArray(), _enabledServers.ToArray())));
        File.Move(path + ".tmp", path, true);
    }

    private void UpdateToolCatalog(IList<CurrentToolMetadata> metadata)
    {
        lock (_sync)
        {
            _toolCatalog = _toolCatalog.Where(tool => tool.Group == "WinDbg")
                .Concat(metadata.Where(tool => tool.McpServerName is null &&
                        tool.Name is not ("debugger_command" or "debugger_target" or "skill" or "ask_user" or "agent" or "task"))
                    .Select(tool => new ToolOption(tool.Name, tool.Name, "Built-In", tool.Description, false)))
                .Concat(metadata.Where(tool => tool.McpServerName is not null && _enabledServers.Contains(tool.McpServerName))
                    .Select(tool => new ToolOption(tool.Name, tool.McpToolName ?? tool.Name, tool.McpServerName!, tool.Description, false)))
                .DistinctBy(tool => tool.Id).ToArray();
            _selectedTools.IntersectWith(_toolCatalog.Select(tool => tool.Id));
        }
    }

    private async Task DiscoverSessionToolsAsync()
    {
        var session = _session!;
        string? discoveryError = null;
        try
        {
            await session.Rpc.Options.UpdateAsync(availableTools: new ToolSet().AddBuiltIn("*").AddMcp("*").AddCustom("*"));
            await session.Rpc.Tools.InitializeAndValidateAsync();
            long statusVersion;
            lock (_sync) statusVersion = _mcpStatusVersion;
            var servers = await session.Rpc.Mcp.ListAsync();
            var serverTools = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var server in servers.Servers.Where(server => server.Status.ToString() == "connected" && _enabledServers.Contains(server.Name)))
            {
                discoveryError = $"Could not retrieve tools from MCP server '{server.Name}'. Reload tools or sign in again to check access.";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var listed = await session.Rpc.Mcp.ListToolsAsync(server.Name, timeout.Token);
                serverTools[server.Name] = listed.Tools.Select(tool => tool.Name).ToArray();
            }
            discoveryError = null;
            lock (_sync) _mcpRefreshRequested = false;
            var metadata = await session.Rpc.Tools.GetCurrentMetadataAsync();
            var missingServers = FindMissingMcpServers(metadata.Tools ?? [], serverTools);
            if (missingServers.Length > 0)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                foreach (var server in missingServers)
                {
                    await session.Rpc.Mcp.DisableAsync(server, timeout.Token);
                    await session.Rpc.Mcp.EnableAsync(server, timeout.Token);
                }
                await session.Rpc.Options.UpdateAsync(availableTools: new ToolSet().AddBuiltIn("*").AddMcp("*").AddCustom("*"));
                await session.Rpc.Tools.InitializeAndValidateAsync();
                lock (_sync) statusVersion = _mcpStatusVersion;
                servers = await session.Rpc.Mcp.ListAsync();
                lock (_sync) _mcpRefreshRequested = false;
                metadata = await session.Rpc.Tools.GetCurrentMetadataAsync();
                var missing = FindMissingMcpServers(metadata.Tools ?? [], serverTools);
                if (missing.Length > 0)
                {
                    discoveryError = $"MCP tools are connected but unavailable in the SDK tool catalog: {string.Join(", ", missing)}. Reload tools or sign in again.";
                    throw new InvalidOperationException(discoveryError);
                }
            }
            UpdateToolCatalog(metadata.Tools ?? throw new InvalidOperationException("Session tool metadata is unavailable."));
            lock (_sync)
            {
                ApplyMcpStatusSnapshot(servers.Servers.ToDictionary(server => server.Name, server => server.Status.ToString()), statusVersion);
                var empty = serverTools.Where(server => server.Value.Length == 0).Select(server => server.Key).ToArray();
                _toolError = empty.Length == 0 ? null : $"MCP server returned no tools: {string.Join(", ", empty)}. Check server permissions/configuration or sign in again.";
            }
        }
        catch
        {
            lock (_sync) { _toolConfigurationValid = false; _toolError = discoveryError ?? "Could not discover session tools. Check the configuration and reload."; }
            throw;
        }
        finally
        {
            try { await session.Rpc.Options.UpdateAsync(availableTools: _selectedTools.ToList()); }
            catch { lock (_sync) _toolConfigurationValid = false; throw; }
        }
        lock (_sync) _toolConfigurationValid = true;
    }

    private string[] FindMissingMcpServers(IList<CurrentToolMetadata> metadata, Dictionary<string, string[]> serverTools) =>
        serverTools.Where(server => _enabledServers.Contains(server.Key) && _mcpDefinitions.TryGetValue(server.Key, out var definition) &&
            server.Value.Any(name => (definition.Tools is null || definition.Tools.Contains("*") || definition.Tools.Contains(name)) &&
                !metadata.Any(tool => tool.McpServerName == server.Key && tool.McpToolName == name)))
            .Select(server => server.Key).ToArray();

    private static McpOption CreateMcpOption(string name, bool enabled, string status, McpServerConfig definition) =>
        new(name, enabled, status, enabled && status == "needs-auth" && definition is McpHttpServerConfig,
            enabled && definition is McpHttpServerConfig);

    private long ResetMcpSessionState()
    {
        lock (_sync)
        {
            _mcpRefreshRequested = false;
            _mcpStatusVersions.Clear();
            _serverCatalog = _mcpDefinitions.Select(item => CreateMcpOption(item.Key, _enabledServers.Contains(item.Key),
                _enabledServers.Contains(item.Key) ? "pending" : "disabled", item.Value)).ToArray();
            return ++_sessionEventGeneration;
        }
    }

    private void OnSessionEvent(long generation, string identity, SessionEvent message) =>
        HandleSessionEvent(identity, message, generation);

    private void UpdateMcpStatus(string name, string status)
    {
        if (!_enabledServers.Contains(name) || !_mcpDefinitions.TryGetValue(name, out var definition)) return;
        if (status == "connected" && _serverCatalog.FirstOrDefault(server => server.Name == name)?.Status != "connected")
            _mcpRefreshRequested = true;
        _mcpStatusVersions[name] = ++_mcpStatusVersion;
        var option = CreateMcpOption(name, true, status, definition);
        _serverCatalog = _serverCatalog.Any(server => server.Name == name)
            ? _serverCatalog.Select(server => server.Name == name ? option : server).ToArray()
            : [.. _serverCatalog, option];
    }

    private void ApplyMcpStatusSnapshot(Dictionary<string, string> statuses, long requestedAtVersion)
    {
        _serverCatalog = _mcpDefinitions.Select(item =>
        {
            var enabled = _enabledServers.Contains(item.Key);
            var status = statuses.GetValueOrDefault(item.Key, enabled ? "pending" : "disabled");
            if (_mcpStatusVersions.GetValueOrDefault(item.Key) > requestedAtVersion)
                status = _serverCatalog.FirstOrDefault(server => server.Name == item.Key)?.Status ?? status;
            return CreateMcpOption(item.Key, enabled, enabled ? status : "disabled", item.Value);
        }).ToArray();
    }

    private void ScheduleMcpRefresh()
    {
        lock (_sync)
        {
            if (_disposed || _busy || _session is null || !_mcpRefreshRequested || _mcpRefreshTask is not null) return;
            _mcpRefreshTask = Task.Run(RefreshConnectedMcpToolsAsync);
        }
    }

    private async Task RefreshConnectedMcpToolsAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_disposed || _busy || _session is null || !_mcpRefreshRequested) return;
                _mcpRefreshRequested = false;
                _busy = true;
                _status = "Updating tools";
            }
            Publish();
            try { await DiscoverSessionToolsAsync(); }
            catch
            {
                lock (_sync)
                {
                    _toolConfigurationValid = false;
                    _toolError ??= "Could not refresh tools after MCP connection. Reload tools to retry.";
                }
            }
            finally
            {
                lock (_sync) { _busy = false; _status = "Ready"; }
                Publish();
            }
        }
        finally
        {
            _lifecycle.Release();
            lock (_sync) _mcpRefreshTask = null;
            ScheduleMcpRefresh();
        }
    }

    public async Task<string?> AuthenticateMcpAsync(string server, bool forceReauth = false)
    {
        string? authorizationUrl = null;
        await ChangeToolsAsync(async () =>
        {
            if (!_enabledServers.Contains(server) || !_mcpDefinitions.TryGetValue(server, out var definition)
                || definition is not McpHttpServerConfig || !_serverCatalog.Any(option => option.Name == server &&
                    (forceReauth ? option.CanReauthenticate : option.CanAuthenticate)))
                throw new ArgumentException("Sign-in is available only for enabled HTTP MCP servers requiring authentication.");
            lock (_sync)
            {
                _toolError = null;
                if (forceReauth) UpdateMcpStatus(server, "pending");
            }
            Publish();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await _session!.Rpc.Mcp.Oauth.LoginAsync(serverName: server,
                forceReauth: forceReauth,
                clientName: "WinDbg Copilot Chat", callbackSuccessMessage: "Return to WinDbg Copilot Chat.",
                cancellationToken: timeout.Token);
            authorizationUrl = result.AuthorizationUrl;
            if (string.IsNullOrEmpty(authorizationUrl)) await DiscoverSessionToolsAsync();
        });
        return authorizationUrl;
    }

    public Task LoadMcpAsync(string? path) => ChangeToolsAsync(async () =>
    {
        var nextPath = path ?? _mcpPath;
        var definitions = nextPath is null ? new Dictionary<string, McpServerConfig>() : ToolConfiguration.Load(nextPath);
        lock (_sync)
        {
            if (path is not null && path != _mcpPath) _enabledServers.Clear();
            _mcpPath = nextPath;
            _mcpDefinitions = definitions;
            _enabledServers.IntersectWith(definitions.Keys);
            _toolError = null;
        }
        await ReconnectToolsAsync();
    });

    public Task SetToolsAsync(string[] tools, string[] servers) => ChangeToolsAsync(async () =>
    {
        if (tools.Any(id => !_toolCatalog.Any(tool => tool.Id == id)) || servers.Any(name => !_mcpDefinitions.ContainsKey(name)))
            throw new ArgumentException("Unknown tool or MCP server. Reload the tool list.");
        var nextServers = new HashSet<string>(servers, StringComparer.Ordinal);
        var reconnect = !_enabledServers.SetEquals(nextServers);
        lock (_sync)
        {
            _selectedTools = new(tools.Where(id => !_toolCatalog.Any(tool => tool.Id == id && _mcpDefinitions.ContainsKey(tool.Group) && !nextServers.Contains(tool.Group))), StringComparer.Ordinal);
            _enabledServers = nextServers;
            _toolError = null;
        }
        if (reconnect) await ReconnectToolsAsync();
        else await _session!.Rpc.Options.UpdateAsync(availableTools: _selectedTools.ToList());
    });

    private async Task ChangeToolsAsync(Func<Task> change)
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is null) throw new InvalidOperationException("Connect to Copilot first.");
            lock (_sync)
            {
                if (_busy) throw new InvalidOperationException("Wait for the response or stop it before changing tools.");
                _busy = true;
                _status = "Updating tools";
            }
            Publish();
            try { await change(); SaveToolPreferences(); _toolConfigurationValid = true; }
            catch
            {
                lock (_sync) { _toolConfigurationValid = false; _toolError ??= "Tool update failed. Reload before sending another message."; }
                throw;
            }
            finally
            {
                lock (_sync) { _busy = false; _status = "Ready"; }
                Publish();
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ReconnectToolsAsync()
    {
        SaveCurrent();
        var generation = ResetMcpSessionState();
        _subscription?.Dispose();
        var previous = _session;
        _session = null;
        if (previous is not null) await previous.DisposeAsync();
        _session = _messages.Any(message => message.Role is "user" or "assistant")
            ? await _client!.ResumeSessionAsync(_sessionId, Configure(new ResumeSessionConfig { Model = _model }))
            : await _client!.CreateSessionAsync(Configure(new SessionConfig { SessionId = _sessionId, Model = _model }));
        var identity = _sessionId;
        _subscription = _session.On<SessionEvent>(message => OnSessionEvent(generation, identity, message));
        await DiscoverSessionToolsAsync();
    }

    private async Task<PreToolUseHookOutput?> ApproveToolAsync(PreToolUseHookInput input, HookInvocation invocation)
    {
        CancellationToken token;
        lock (_sync)
        {
            if (!_busy || _status == "Updating tools" || !_toolConfigurationValid || _cancellation is null || _cancellation.IsCancellationRequested || input.SessionId != _sessionId || !_selectedTools.Contains(input.ToolName))
                return new() { PermissionDecision = "deny" };
            if (input.ToolName is "debugger_command" or "debugger_target") return new() { PermissionDecision = "allow" };
            token = _cancellation.Token;
        }
        try
        {
            var approved = await _approvals.RequestAsync("tool", input.ToolName + "\n" + JsonSerializer.Serialize(input.ToolArgs), token);
            return new() { PermissionDecision = approved && !token.IsCancellationRequested ? "allow" : "deny" };
        }
        catch (OperationCanceledException) { return new() { PermissionDecision = "deny" }; }
    }

    private async Task<PermissionDecision> ApprovePermissionAsync(PermissionRequest request, PermissionInvocation invocation)
    {
        CancellationToken token;
        lock (_sync)
        {
            if (!_busy || _status == "Updating tools" || !_toolConfigurationValid || _cancellation is null || _cancellation.IsCancellationRequested || invocation.SessionId != _sessionId)
                return new() { Kind = "denied-by-rules" };
            token = _cancellation.Token;
        }
        try
        {
            var approved = await _approvals.RequestAsync("permission", JsonSerializer.Serialize(request, request.GetType()), token);
            return new() { Kind = approved && !token.IsCancellationRequested ? "approved" : "denied-by-rules" };
        }
        catch (OperationCanceledException) { return new() { Kind = "denied-by-rules" }; }
    }
}
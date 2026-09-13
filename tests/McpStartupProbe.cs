#:sdk Microsoft.NET.Sdk.Web
#:project ../src/ChatCore/ChatCore.csproj
#:property CopilotSkipCliDownload=true
#:property NoWarn=GHCP001
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using ChatCore;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

var scenario = args.SingleOrDefault() ?? "delayed";
if (scenario is not ("delayed" or "stable" or "empty" or "denied")) throw new ArgumentException("Unknown scenario.");
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
await using var app = builder.Build();
app.Urls.Add("http://127.0.0.1:0");
var listRequests = 0;
app.MapPost("/mcp", async (HttpContext context) =>
{
    using var document = await JsonDocument.ParseAsync(context.Request.Body);
    var root = document.RootElement;
    if (!root.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; return; }
    var method = root.GetProperty("method").GetString();
    var requestNumber = method == "tools/list" ? Interlocked.Increment(ref listRequests) : 0;
    if (scenario == "denied" && requestNumber > 1) { context.Response.StatusCode = 403; return; }
    var count = scenario == "empty" || (scenario == "delayed" && requestNumber == 1) ? 0 : 150;
    object result = method switch
    {
        "initialize" => new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "local-probe", version = "1.0" } },
        "tools/list" => new { tools = Enumerable.Range(0, count).Select(index => new { name = "probe_read_" + index, description = "Synthetic test tool", inputSchema = new { type = "object", properties = new { } } }).ToArray() },
        _ => throw new InvalidOperationException("Unexpected MCP method: " + method)
    };
    await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result });
});
await app.StartAsync();
var directory = Path.Combine(Path.GetTempPath(), "windbg-mcp-startup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase);
    environment["COPILOT_HOME"] = directory;
    foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "COPILOT_GITHUB_TOKEN" }) environment[name] = "";
    await using var runtime = new ChatRuntime();
    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
    void Set(string name, object value) => typeof(ChatRuntime).GetField(name, flags)!.SetValue(runtime, value);
    var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    var client = new CopilotClient(new CopilotClientOptions
    {
        UseLoggedInUser = false, BaseDirectory = directory, WorkingDirectory = directory, Environment = environment,
        Connection = RuntimeConnection.ForStdio(path: Path.GetFullPath($"../artifacts/package/WinDbgCopilotChat/core/runtimes/win-{architecture}/native/copilot.exe"))
    });
    Set("_client", client);
    await client.StartAsync();
    var definitions = new Dictionary<string, McpServerConfig>
    { ["local-probe"] = new McpHttpServerConfig { Url = app.Urls.Single() + "/mcp", Tools = ["*"], Timeout = 15000 } };
    var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = "probe", Provider = new GitHub.Copilot.ProviderConfig { Type = "openai", BaseUrl = "http://127.0.0.1:1", ApiKey = "unused-probe-key" },
        WorkingDirectory = directory, ConfigDirectory = directory, EnableConfigDiscovery = false,
        EnableFileHooks = false, EnableSkills = false, DisabledSkills = ["*"], SkipCustomInstructions = true,
        McpOAuthTokenStorage = McpOAuthTokenStorageMode.InMemory, AvailableTools = ["view"], McpServers = definitions,
        OnPermissionRequest = (_, _) => Task.FromResult(new PermissionDecision { Kind = "denied-by-rules" })
    });
    Set("_session", session);
    Set("_mcpDefinitions", definitions);
    Set("_enabledServers", new HashSet<string>(["local-probe"]));
    Set("_selectedTools", new HashSet<string>(["view"]));
    var generation = (long)typeof(ChatRuntime).GetMethod("ResetMcpSessionState", flags)!.Invoke(runtime, null)!;
    var onEvent = typeof(ChatRuntime).GetMethod("OnSessionEvent", flags)!;
    var identity = runtime.Snapshot.SessionId;
    Set("_subscription", session.On<SessionEvent>(message => onEvent.Invoke(runtime, [generation, identity, message])));
    var lifecycle = (SemaphoreSlim)typeof(ChatRuntime).GetField("_lifecycle", flags)!.GetValue(runtime)!;
    await lifecycle.WaitAsync();
    try
    {
        Exception? failure = null;
        try { await ((Task)typeof(ChatRuntime).GetMethod("DiscoverSessionToolsAsync", flags)!.Invoke(runtime, null)!).WaitAsync(TimeSpan.FromSeconds(45)); }
        catch (Exception exception) { failure = exception; }
        var settings = runtime.Snapshot.ToolSettings!;
        var tools = settings.Tools.Where(tool => tool.Group == "local-probe").ToArray();
        if (scenario == "denied")
        {
            if (failure is null || settings.Error?.Contains("Could not retrieve tools from MCP server 'local-probe'") != true ||
                (bool)typeof(ChatRuntime).GetField("_toolConfigurationValid", flags)!.GetValue(runtime)!)
                throw new InvalidOperationException("Server rejection was not surfaced safely.", failure);
        }
        else
        {
            if (failure is not null) throw new InvalidOperationException("Startup discovery failed.", failure);
            if (tools.Length != (scenario == "empty" ? 0 : 150) || tools.Any(tool => tool.Selected))
                throw new InvalidOperationException("Startup discovery lost MCP tools or changed selection.");
            if (scenario == "empty" ? settings.Error?.Contains("returned no tools") != true : settings.Error is not null)
                throw new InvalidOperationException("Empty server result was not distinguished from discovery failure.");
        }
        await session.Rpc.Tools.InitializeAndValidateAsync();
        var active = await session.Rpc.Tools.GetCurrentMetadataAsync();
        if (active.Tools!.Any(tool => tool.McpServerName is not null)) throw new InvalidOperationException("MCP allowlist was not restored.");
        Console.WriteLine($"PASS: {scenario}; picker tools={tools.Length}; no synthetic events, user credentials, model calls or tool execution; allowlist restored.");
    }
    finally { lifecycle.Release(); }
}
finally
{
    await app.StopAsync();
    Directory.Delete(directory, true);
}
using GitHub.Copilot;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatCore;

public static class ToolConfiguration
{
    public static Dictionary<string, McpServerConfig> Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        });
        return Parse(document.RootElement, Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public static Dictionary<string, McpServerConfig> Parse(JsonElement root, string directory)
    {
        if (!root.TryGetProperty("servers", out var servers) && !root.TryGetProperty("mcpServers", out servers))
            throw new ArgumentException("mcp.json must contain a servers or mcpServers object.");
        if (servers.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP servers must be an object.");
        var result = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        foreach (var server in servers.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(server.Name) || server.Name.Any(char.IsControl))
                throw new ArgumentException("Invalid MCP server name.");
            var value = server.Value;
            string? Text(string name) => value.TryGetProperty(name, out var item) ? Expand(item.GetString() ?? "") : null;
            Dictionary<string, string>? Map(string name) => value.TryGetProperty(name, out var item)
                ? item.EnumerateObject().ToDictionary(entry => entry.Name, entry => Expand(entry.Value.GetString() ?? "")) : null;
            var type = Text("type") ?? (value.TryGetProperty("command", out _) ? "stdio" : "http");
            McpServerConfig config;
            if (type is "stdio" or "local")
            {
                var command = Text("command");
                if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException($"MCP server '{server.Name}' requires a command.");
                config = new McpStdioServerConfig
                {
                    Command = command,
                    Args = value.TryGetProperty("args", out var args) ? args.EnumerateArray().Select(item => Expand(item.GetString() ?? "")).ToList() : [],
                    Env = Map("env"),
                    WorkingDirectory = Path.GetFullPath(Text("cwd") ?? Text("workingDirectory") ?? directory, directory)
                };
            }
            else if (type == "http")
            {
                var url = Text("url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new ArgumentException($"MCP server '{server.Name}' requires an HTTP or HTTPS URL.");
                config = new McpHttpServerConfig { Url = url!, Headers = Map("headers") };
            }
            else throw new ArgumentException($"Unsupported MCP transport '{type}' for '{server.Name}'.");
            config.Tools = value.TryGetProperty("tools", out var tools) ? tools.EnumerateArray().Select(item => item.GetString()!).ToList() : ["*"];
            if (config.Tools?.Any(string.IsNullOrWhiteSpace) == true) throw new ArgumentException($"MCP server '{server.Name}' has an invalid tool name.");
            if (value.TryGetProperty("timeout", out var timeout))
            {
                var milliseconds = timeout.GetInt32();
                if (milliseconds <= 0) throw new ArgumentException($"MCP server '{server.Name}' requires a positive timeout.");
                config.Timeout = milliseconds;
            }
            if (value.TryGetProperty("envFile", out _)) throw new ArgumentException($"MCP server '{server.Name}': envFile is not supported. Use env instead.");
            if (!result.TryAdd(server.Name, config)) throw new ArgumentException($"Duplicate MCP server '{server.Name}'.");
        }
        return result;
    }

    private static string Expand(string value) => Regex.Replace(value, @"\$\{([^}]+)\}", match =>
    {
        var expression = match.Groups[1].Value;
        if (expression == "userHome") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (expression.StartsWith("env:", StringComparison.Ordinal))
            return Environment.GetEnvironmentVariable(expression[4..]) ?? throw new ArgumentException($"Environment variable '{expression[4..]}' is not set.");
        throw new ArgumentException($"Unsupported MCP variable '${{{expression}}}'. Use an environment variable instead.");
    });
}
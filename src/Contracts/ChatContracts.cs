namespace Contracts;

public enum ApprovalMode { AskEveryTime, ApproveAll }
public enum ChatLogLevel { Trace, Debug, Information, Warning, Error, Critical, None }

public interface IChatLogSink
{
    ChatLogLevel MinimumLevel { get; }
    void Log(ChatLogLevel level, string category, string message, Exception? exception = null);
}

public sealed record TargetInfo(bool Available);
public sealed record ChatMessage(string Id, string Role, string Text, bool Complete,
    string? TurnId = null, DateTimeOffset? Timestamp = null, string? Model = null, AttachmentInfo[]? Attachments = null,
    string? Title = null, string? ActivityStatus = null);
public sealed record ChatAttachment(string Name, string MimeType, string Data);
public sealed record AttachmentInfo(string Name, string MimeType, int Size, string? Data = null);
public sealed record ModelPrices(double? BatchSize, double? Input, double? Output, double? CacheRead, double? CacheWrite);
public sealed record AccountInfo(string? Login, string? Host, bool Authenticated);
public sealed record TurnInfo(string Id, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt,
    string[] Models, double? NanoAiu, double? InputTokens, double? OutputTokens, int UsageCalls, int UnreportedCostCalls);
public sealed record SessionInfo(DateTimeOffset StartedAt, double? NanoAiu, int UnreportedCostCalls,
    double? CurrentTokens, double? TokenLimit, double? SystemTokens, double? ToolDefinitionsTokens,
    double? ConversationTokens, double? MessagesLength);
public sealed record ApprovalRequest(string Id, string Kind, string Text);
public sealed record SessionEntry(string Id, string Title, DateTimeOffset UpdatedAt);
public sealed record ToolOption(string Id, string Name, string Group, string? Description, bool Selected);
public sealed record McpOption(string Name, bool Enabled, string Status, bool CanAuthenticate = false, bool CanReauthenticate = false);
public sealed record ToolSettings(ToolOption[] Tools, McpOption[] Servers, string? ConfigPath, string? Error = null);
public sealed record ModelOption(string Id, string Name, double? ContextTokens = null,
    double? MaxPromptTokens = null, bool? Vision = null, string[]? ReasoningEfforts = null,
    string? DefaultReasoningEffort = null, double? Multiplier = null, string? Policy = null, ModelPrices? Prices = null);
public sealed record ChatSnapshot(string SessionId, ApprovalMode Mode, bool Busy,
    string Status, string? Error, string? Model, TargetInfo Target,
    ChatMessage[] Messages, ApprovalRequest[] Approvals, ModelOption[] Models,
    AccountInfo? Account = null, SessionInfo? Info = null, TurnInfo[]? Turns = null,
    SessionEntry[]? Sessions = null, string? SessionTitle = null, ToolSettings? ToolSettings = null);

public interface IDebuggerAdapter
{
    Task<TargetInfo> GetTargetAsync(CancellationToken cancellationToken);
    Task<string> GetTargetDetailsAsync(CancellationToken cancellationToken) => Task.FromResult("Target details are unavailable.");
    Task<string> ExecuteAsync(string command, TargetInfo expectedTarget, CancellationToken cancellationToken);
}

public interface IChatRuntime : IAsyncDisposable
{
    event Action<ChatSnapshot>? Changed;
    ChatSnapshot Snapshot { get; }
    Task InitializeAsync(IDebuggerAdapter debugger);
    Task NewChatAsync(string? model);
    Task SetModelAsync(string model);
    Task SetToolsAsync(string[] tools, string[] servers);
    Task LoadMcpAsync(string? path);
    Task<string?> AuthenticateMcpAsync(string server, bool forceReauth = false);
    Task ResumeAsync(string sessionId);
    Task RenameAsync(string sessionId, string title);
    Task DeleteAsync(string sessionId);
    Task RunCommandAsync(string command);
    Task SendAsync(string prompt, ChatAttachment[]? attachments = null);
    Task CancelAsync();
    void SetMode(ApprovalMode mode);
    bool ResolveApproval(string sessionId, string approvalId, bool approved);
}
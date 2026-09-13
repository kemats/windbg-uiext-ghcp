using Contracts;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace ChatCore;

internal sealed class ChatLogger(IChatLogSink sink, string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => ToChatLevel(logLevel) >= sink.MinimumLevel
        && sink.MinimumLevel != ChatLogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel)) sink.Log(ToChatLevel(logLevel), category, formatter(state, exception), exception);
    }

    internal static CopilotLogLevel ToCopilotLevel(ChatLogLevel level) => level switch
    {
        ChatLogLevel.Trace => CopilotLogLevel.All,
        ChatLogLevel.Debug => CopilotLogLevel.Debug,
        ChatLogLevel.Information => CopilotLogLevel.Info,
        ChatLogLevel.Warning => CopilotLogLevel.Warning,
        ChatLogLevel.Error or ChatLogLevel.Critical => CopilotLogLevel.Error,
        _ => CopilotLogLevel.None
    };

    private static ChatLogLevel ToChatLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => ChatLogLevel.Trace,
        LogLevel.Debug => ChatLogLevel.Debug,
        LogLevel.Information => ChatLogLevel.Information,
        LogLevel.Warning => ChatLogLevel.Warning,
        LogLevel.Error => ChatLogLevel.Error,
        LogLevel.Critical => ChatLogLevel.Critical,
        _ => ChatLogLevel.None
    };
}

internal sealed class NullChatLogSink : IChatLogSink
{
    internal static NullChatLogSink Instance { get; } = new();
    public ChatLogLevel MinimumLevel => ChatLogLevel.None;
    public void Log(ChatLogLevel level, string category, string message, Exception? exception = null) { }
}
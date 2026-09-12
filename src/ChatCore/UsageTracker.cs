using Contracts;
using GitHub.Copilot;

namespace ChatCore;

public sealed class UsageTracker
{
    private readonly HashSet<string> _events = [];
    private readonly List<TurnInfo> _turns = [];
    public SessionInfo Info { get; private set; } = EmptySession();
    public TurnInfo[] Turns => _turns.ToArray();
    public string? CurrentTurnId { get; private set; }

    private static SessionInfo EmptySession() => new(DateTimeOffset.UtcNow, null, 0, null, null, null, null, null, null);

    public void Reset()
    {
        _events.Clear();
        _turns.Clear();
        CurrentTurnId = null;
        Info = EmptySession();
    }

    public void BeginTurn(string id, DateTimeOffset timestamp)
    {
        CurrentTurnId = id;
        _turns.Add(new(id, timestamp, null, [], null, null, null, 0, 0));
    }

    public void Restore(SessionInfo? info, TurnInfo[] turns)
    {
        Reset();
        Info = info ?? EmptySession();
        _turns.AddRange(turns);
    }

    public void CompleteTurn()
    {
        if (_turns.Count > 0) _turns[^1] = _turns[^1] with { CompletedAt = DateTimeOffset.UtcNow };
        CurrentTurnId = null;
    }

    public void RecordUsage(string eventId, AssistantUsageData data)
    {
        if (!_events.Add(eventId)) return;
        var nanoAiu = data.CopilotUsage?.TotalNanoAiu;
        if (nanoAiu is { } value && (!double.IsFinite(value) || value < 0)) nanoAiu = null;
        Info = Info with
        {
            NanoAiu = Sum(Info.NanoAiu, nanoAiu),
            UnreportedCostCalls = Info.UnreportedCostCalls + (nanoAiu is null ? 1 : 0)
        };
        if (CurrentTurnId is null) return;
        var turn = _turns[^1];
        var models = string.IsNullOrWhiteSpace(data.Model) || turn.Models.Contains(data.Model)
            ? turn.Models : [.. turn.Models, data.Model];
        _turns[^1] = turn with
        {
            Models = models, NanoAiu = Sum(turn.NanoAiu, nanoAiu),
            InputTokens = Sum(turn.InputTokens, data.InputTokens), OutputTokens = Sum(turn.OutputTokens, data.OutputTokens),
            UsageCalls = turn.UsageCalls + 1, UnreportedCostCalls = turn.UnreportedCostCalls + (nanoAiu is null ? 1 : 0)
        };
    }

    public void RecordContext(SessionUsageInfoData data) => Info = Info with
    {
        CurrentTokens = data.CurrentTokens, TokenLimit = data.TokenLimit, SystemTokens = data.SystemTokens,
        ToolDefinitionsTokens = data.ToolDefinitionsTokens, ConversationTokens = data.ConversationTokens,
        MessagesLength = data.MessagesLength
    };

    private static double? Sum(double? previous, double? value) => value is null ? previous : (previous ?? 0) + value;
}
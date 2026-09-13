using Contracts;

namespace ChatCore;

public sealed class DebuggerTool(IDebuggerAdapter debugger, ApprovalGate approvals, Action<string>? showOutput = null,
    Func<ApprovalMode>? getMode = null, Action<Exception>? logFailure = null)
{
    public async Task<string> ExecuteAsync(string command, TargetInfo target, ApprovalMode mode, CancellationToken token)
    {
        if (!target.Available) return "No stopped debug target is available.";
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096) return "Invalid command length.";
        if ((getMode?.Invoke() ?? mode) == ApprovalMode.AskEveryTime && !await approvals.RequestAsync("execute", command, token))
            return "Command execution denied.";
        token.ThrowIfCancellationRequested();
        target = await debugger.GetTargetAsync(token);
        if (!target.Available) return "No stopped debug target is available.";
        string output;
        try { output = await debugger.ExecuteAsync(command, target, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logFailure?.Invoke(exception);
            return "Debugger command failed. Inspect the WinDbg command window locally for details.";
        }
        token.ThrowIfCancellationRequested();
        showOutput?.Invoke(output);
        if ((getMode?.Invoke() ?? mode) == ApprovalMode.AskEveryTime && !await approvals.RequestAsync("share", output, token)) return "User withheld debugger output.";
        token.ThrowIfCancellationRequested();
        return output;
    }

    public async Task<string> GetTargetDetailsAsync(ApprovalMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var details = await debugger.GetTargetDetailsAsync(token);
        showOutput?.Invoke(details);
        token.ThrowIfCancellationRequested();
        return details;
    }
}
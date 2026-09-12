using System.Text;
using Contracts;
using DbgX.Interfaces.Services;
using DbgX.Interfaces.Structs;

namespace WinDbgChatView;

internal sealed class DebuggerAdapter : IDebuggerAdapter
{
    private readonly IDbgEngineSynchronizationContextSource _context;
    private readonly IDbgConsole _console;
    private readonly IDbgOutputEvents _output;
    private readonly IDbgTargetState _state;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DebuggerAdapter(IDbgEngineSynchronizationContextSource context, IDbgConsole console,
        IDbgOutputEvents output, IDbgTargetState state)
    {
        _context = context;
        _console = console;
        _output = output;
        _state = state;
    }

    private TargetInfo ReadTarget() => new(_state.RunningState == RunningState.Stopped);

    public Task<TargetInfo> GetTargetAsync(CancellationToken cancellationToken) =>
        OnEngineAsync(() => Task.FromResult(ReadTarget()), cancellationToken);

    public Task<string> GetTargetDetailsAsync(CancellationToken cancellationToken) =>
        OnEngineAsync(() => Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
        {
            Target = ReadTarget(), Type = _state.TargetType.ToString(), State = _state.RunningState.ToString(),
            EffectiveArchitecture = _state.EffectiveProcessorArchitecture.ToString(),
            ActualArchitecture = _state.ActualProcessorArchitecture.ToString()
        })), cancellationToken);

    public Task<string> ExecuteAsync(string command, TargetInfo expectedTarget, CancellationToken cancellationToken) =>
        OnEngineAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expectedTarget.Available || ReadTarget() != expectedTarget)
                throw new InvalidOperationException("The debug target changed or is running.");
            if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Contains('\0'))
                throw new ArgumentException("Invalid debugger command.");
            var output = new StringBuilder();
            var sync = new object();
            void Capture(object? sender, TextOutputEventArgs args)
            {
                lock (sync)
                {
                    output.AppendLine(args.Text);
                }
            }
            _output.OnDmlOutput += Capture;
            try
            {
                // Cancellation withholds the result, but cannot stop the engine task or release its gate early.
                await _console.ExecuteCommandAsync(command);
                cancellationToken.ThrowIfCancellationRequested();
                lock (sync) return output.ToString();
            }
            finally { _output.OnDmlOutput -= Capture; }
        }, cancellationToken);

    private async Task<TResult> OnEngineAsync<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _context.SyncContext.Post(async _ =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(await action());
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }, null);
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
using Contracts;

namespace ChatCore;

public sealed class ApprovalGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (ApprovalRequest Request, TaskCompletionSource<bool> Completion)> _pending = [];
    public event Action? Changed;
    public ApprovalRequest[] Pending { get { lock (_sync) return _pending.Values.Select(item => item.Request).ToArray(); } }

    public async Task<bool> RequestAsync(string kind, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new ApprovalRequest(Guid.NewGuid().ToString("N"), kind, text);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync) _pending.Add(request.Id, (request, completion));
        using var registration = cancellationToken.Register(() => Resolve(request.Id, false));
        Changed?.Invoke();
        try { return await completion.Task.ConfigureAwait(false); }
        finally
        {
            lock (_sync) _pending.Remove(request.Id);
            Changed?.Invoke();
        }
    }

    public bool Resolve(string id, bool approved)
    {
        lock (_sync)
        {
            return _pending.Remove(id, out var item) && item.Completion.TrySetResult(approved);
        }
    }

    public void CancelAll()
    {
        lock (_sync)
        {
            foreach (var item in _pending.Values) item.Completion.TrySetResult(false);
            _pending.Clear();
        }
        Changed?.Invoke();
    }
}
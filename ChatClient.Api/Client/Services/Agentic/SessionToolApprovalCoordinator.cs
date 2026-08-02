using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed class SessionToolApprovalCoordinator : ISessionToolApprovalCoordinator
{
    private readonly object _sync = new();
    private readonly LinkedList<PendingApprovalState> _pending = [];

    public SessionToolApprovalRequest? PendingRequest
    {
        get
        {
            lock (_sync)
            {
                return _pending.First?.Value.Request;
            }
        }
    }

    public event Action? PendingRequestChanged;

    public Task<ToolApprovalDecision> RequestApprovalAsync(
        SessionToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        PendingApprovalState state;
        var publish = false;
        lock (_sync)
        {
            state = new PendingApprovalState(
                request,
                new TaskCompletionSource<ToolApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
            publish = _pending.Count == 0;
            _pending.AddLast(state);
        }

        if (publish)
            PendingRequestChanged?.Invoke();

        if (!cancellationToken.CanBeCanceled)
        {
            return state.Completion.Task;
        }

        return WaitWithCancellationAsync(state, cancellationToken);
    }

    public bool TryRespond(
        string requestId,
        ToolApprovalDecision decision,
        Action<SessionToolApprovalRequest> beforeCompletion)
    {
        PendingApprovalState? state;
        lock (_sync)
        {
            if (_pending.First is not { } head ||
                !string.Equals(head.Value.Request.RequestId, requestId, StringComparison.Ordinal))
            {
                return false;
            }

            state = head.Value;
            beforeCompletion(state.Request);
            _pending.RemoveFirst();
        }

        PendingRequestChanged?.Invoke();
        state.Completion.TrySetResult(decision);
        return true;
    }

    public void CancelPending()
    {
        PendingApprovalState[] states;
        lock (_sync)
        {
            states = _pending.ToArray();
            _pending.Clear();
        }

        if (states.Length == 0)
        {
            return;
        }

        PendingRequestChanged?.Invoke();
        foreach (var state in states)
            state.Completion.TrySetCanceled();
    }

    private async Task<ToolApprovalDecision> WaitWithCancellationAsync(
        PendingApprovalState state,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Cancel(state));
        return await state.Completion.Task;
    }

    private void Cancel(PendingApprovalState state)
    {
        var publish = false;
        lock (_sync)
        {
            var node = _pending.Find(state);
            if (node is null)
                return;
            publish = ReferenceEquals(node, _pending.First);
            _pending.Remove(node);
        }

        if (publish)
            PendingRequestChanged?.Invoke();
        state.Completion.TrySetCanceled();
    }

    private sealed record PendingApprovalState(
        SessionToolApprovalRequest Request,
        TaskCompletionSource<ToolApprovalDecision> Completion);
}

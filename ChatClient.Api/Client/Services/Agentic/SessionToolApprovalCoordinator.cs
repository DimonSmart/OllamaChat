using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Client.Services.Agentic;

internal sealed class SessionToolApprovalCoordinator : ISessionToolApprovalCoordinator
{
    private readonly object _sync = new();
    private PendingApprovalState? _pending;

    public SessionToolApprovalRequest? PendingRequest
    {
        get
        {
            lock (_sync)
            {
                return _pending?.Request;
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
        lock (_sync)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("Another tool approval request is already pending for this session.");
            }

            state = new PendingApprovalState(
                request,
                new TaskCompletionSource<ToolApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending = state;
        }

        PendingRequestChanged?.Invoke();

        if (!cancellationToken.CanBeCanceled)
        {
            return state.Completion.Task;
        }

        return WaitWithCancellationAsync(state, cancellationToken);
    }

    public bool TryRespond(string requestId, ToolApprovalDecision decision)
    {
        PendingApprovalState? state;
        lock (_sync)
        {
            if (_pending is null ||
                !string.Equals(_pending.Request.RequestId, requestId, StringComparison.Ordinal))
            {
                return false;
            }

            state = _pending;
            _pending = null;
        }

        PendingRequestChanged?.Invoke();
        state.Completion.TrySetResult(decision);
        return true;
    }

    public void CancelPending()
    {
        PendingApprovalState? state;
        lock (_sync)
        {
            state = _pending;
            _pending = null;
        }

        if (state is null)
        {
            return;
        }

        PendingRequestChanged?.Invoke();
        state.Completion.TrySetCanceled();
    }

    private async Task<ToolApprovalDecision> WaitWithCancellationAsync(
        PendingApprovalState state,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static pendingState =>
        {
            ((PendingApprovalState)pendingState!).Completion.TrySetCanceled();
        }, state);

        try
        {
            return await state.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pending, state))
                {
                    _pending = null;
                    PendingRequestChanged?.Invoke();
                }
            }
        }
    }

    private sealed record PendingApprovalState(
        SessionToolApprovalRequest Request,
        TaskCompletionSource<ToolApprovalDecision> Completion);
}

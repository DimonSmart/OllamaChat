using ChatClient.Api.Services.BuiltIn;
using ChatClient.Application.Services.Agentic;

namespace ChatClient.Api.Services.AgentRuntime;

public interface IWorkflowExecutionState
{
    Task<bool> IsCompletedAsync(
        string sessionId,
        AgentWorkflowExecutionDefinition execution,
        CancellationToken cancellationToken = default);

    Task<string?> TryGetSummaryAsync(
        string sessionId,
        string label,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowExecutionState(TaskSessionStore taskSessionStore) : IWorkflowExecutionState
{
    public async Task<bool> IsCompletedAsync(
        string sessionId,
        AgentWorkflowExecutionDefinition execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(execution);

        var snapshot = await taskSessionStore.GetSessionAsync(sessionId, cancellationToken);

        return (!string.IsNullOrWhiteSpace(execution.CompletionPhase) &&
                string.Equals(snapshot.Phase, execution.CompletionPhase, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(execution.CompletionSummaryLabel) &&
                snapshot.Summaries.Any(summary =>
                    string.Equals(summary.Label, execution.CompletionSummaryLabel, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<string?> TryGetSummaryAsync(
        string sessionId,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return (await taskSessionStore.TryGetSummaryAsync(sessionId, label, cancellationToken))?.Markdown;
    }
}
